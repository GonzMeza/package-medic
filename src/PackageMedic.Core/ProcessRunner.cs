using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PackageMedic.Core;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string CombinedOutput => string.Concat(StandardOutput, Environment.NewLine, StandardError);

    public bool StandardOutputTruncated => ProcessRunner.WasTruncated(StandardOutput);

    public bool StandardErrorTruncated => ProcessRunner.WasTruncated(StandardError);
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken);

    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        ProcessEnvironment environment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (environment.ClearInheritedEnvironment ||
            environment.Variables.Count != 0 ||
            environment.UntrustedExecutableRoots.Count != 0)
        {
            throw new NotSupportedException(
                "This process runner does not support explicit environment isolation.");
        }

        return RunAsync(fileName, arguments, workingDirectory, cancellationToken);
    }
}

public sealed partial class ProcessRunner : IProcessRunner
{
    internal const int DefaultMaximumOutputCharacters = 8_000_000;
    private const string TruncationMarker = "[PackageMedic: subprocess output truncated]";
    private static readonly TimeSpan CancellationCleanupTimeout = TimeSpan.FromSeconds(2);

    private readonly int maximumOutputCharacters;

    public ProcessRunner(int maximumOutputCharacters = DefaultMaximumOutputCharacters)
    {
        if (maximumOutputCharacters < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOutputCharacters));
        }

        this.maximumOutputCharacters = maximumOutputCharacters;
    }

    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken) =>
        RunAsync(fileName, arguments, workingDirectory, ProcessEnvironment.Inherit, cancellationToken);

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        ProcessEnvironment environment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var searchPath = environment.Variables.TryGetValue("PATH", out var isolatedPath)
            ? isolatedPath
            : Environment.GetEnvironmentVariable("PATH");
        var executableExtensions = environment.Variables.TryGetValue("PATHEXT", out var isolatedExtensions)
            ? isolatedExtensions
            : Environment.GetEnvironmentVariable("PATHEXT");
        var executable = ResolveExecutable(
            fileName,
            workingDirectory,
            searchPath,
            executableExtensions,
            environment.UntrustedExecutableRoots);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        environment.ApplyTo(startInfo);

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, maximumOutputCharacters, cancellationToken);
        var stderrTask = ReadBoundedAsync(process.StandardError, maximumOutputCharacters, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (IsExpectedTerminationException(exception))
            {
                // Cancellation remains the public result even if the process exited,
                // access was denied, or a descendant could not be terminated.
            }

            using var cleanupSource = new CancellationTokenSource(CancellationCleanupTimeout);
            try
            {
                await process.WaitForExitAsync(cleanupSource.Token).ConfigureAwait(false);
                await Task.WhenAll(stdoutTask, stderrTask)
                    .WaitAsync(cleanupSource.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // Cancellation is the public result; bounded cleanup and stream errors are incidental.
            }

            throw;
        }

        return new ProcessResult(
            process.ExitCode,
            environment.Redact(await stdoutTask.ConfigureAwait(false)),
            environment.Redact(await stderrTask.ConfigureAwait(false)));
    }

    internal static string ResolveExecutable(
        string fileName,
        string workingDirectory,
        string? searchPath,
        string? executableExtensions = null,
        IReadOnlyList<string>? untrustedRoots = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var workingRoot = Path.GetFullPath(workingDirectory);
        if (Path.IsPathFullyQualified(fileName))
        {
            return ValidateExecutable(fileName, workingRoot, untrustedRoots);
        }

        if (fileName.Contains(Path.DirectorySeparatorChar) ||
            fileName.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidOperationException("Subprocess executables must be absolute or resolved from a trusted PATH entry.");
        }

        var extensions = OperatingSystem.IsWindows()
            ? BuildWindowsExecutableExtensions(fileName, executableExtensions)
            : [string.Empty];
        foreach (var rawEntry in (searchPath ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = rawEntry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(entry))
            {
                continue;
            }

            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(entry, fileName + extension);
                if (!File.Exists(candidate))
                {
                    continue;
                }

                try
                {
                    return ValidateExecutable(candidate, workingRoot, untrustedRoots);
                }
                catch (InvalidOperationException)
                {
                    // Never fall back to a repository-local PATH entry. Continue looking for a
                    // host executable in a separate absolute PATH directory.
                }
            }
        }

        throw new InvalidOperationException($"Could not resolve the required '{fileName}' executable from trusted host PATH entries.");
    }

    private static string ValidateExecutable(
        string candidate,
        string workingRoot,
        IReadOnlyList<string>? untrustedRoots)
    {
        var fullPath = Path.GetFullPath(candidate);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.Directory))
        {
            throw new InvalidOperationException("The resolved subprocess executable is not a regular file.");
        }

        if (IsInsideUntrustedRoot(fullPath, workingRoot, untrustedRoots))
        {
            throw new InvalidOperationException(
                "Refusing to execute a subprocess binary from inside the analyzed repository or snapshot.");
        }

        fullPath = ResolvePhysicalPath(fullPath, finalEntryIsDirectory: false);
        info = new FileInfo(fullPath);

        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.Directory) ||
            IsInsideUntrustedRoot(fullPath, workingRoot, untrustedRoots))
        {
            throw new InvalidOperationException(
                "Refusing to execute a subprocess binary from inside the analyzed repository or snapshot.");
        }

        return fullPath;
    }

    private static string ResolvePhysicalPath(string path, bool finalEntryIsDirectory)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("The subprocess executable path does not have a filesystem root.");
        }

        var segments = Path.GetRelativePath(root, fullPath)
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != ".")
            .ToArray();
        var current = root;
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            var isLast = index == segments.Length - 1;
            FileSystemInfo entry = isLast && !finalEntryIsDirectory
                ? new FileInfo(current)
                : new DirectoryInfo(current);
            if (!entry.Exists)
            {
                throw new InvalidOperationException("The resolved subprocess executable path no longer exists.");
            }

            if (!entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && entry.LinkTarget is null)
            {
                continue;
            }

            var resolved = entry.ResolveLinkTarget(returnFinalTarget: true)
                ?? throw new InvalidOperationException(
                    "The resolved subprocess executable has an invalid symbolic-link or junction target.");
            current = Path.GetFullPath(resolved.FullName);
        }

        return current;
    }

    private static bool IsInsideUntrustedRoot(
        string candidate,
        string workingRoot,
        IReadOnlyList<string>? untrustedRoots) =>
        IsInsideRoot(workingRoot, candidate) ||
        (untrustedRoots?.Any(root => IsInsideRoot(root, candidate)) ?? false);

    private static bool IsInsideRoot(string root, string candidate)
    {
        if (IsLexicallyContained(root, candidate))
        {
            return true;
        }

        try
        {
            return IsLexicallyContained(
                ResolvePhysicalPath(root, finalEntryIsDirectory: true),
                candidate);
        }
        catch (Exception exception) when (exception is
            IOException or
            InvalidOperationException or
            UnauthorizedAccessException)
        {
            // If a trust boundary changes or cannot be canonicalized, failing closed is safer
            // than executing a binary whose relationship to the repository is unknown.
            return true;
        }
    }

    private static bool IsLexicallyContained(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return !Path.IsPathFullyQualified(relative) &&
            relative != ".." &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string[] BuildWindowsExecutableExtensions(string fileName, string? executableExtensions)
    {
        if (Path.HasExtension(fileName))
        {
            return [string.Empty];
        }

        var extensions = (executableExtensions ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(extension => extension.StartsWith('.') && extension.All(character =>
                character == '.' || char.IsAsciiLetterOrDigit(character)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return extensions.Length == 0 ? [".exe"] : extensions;
    }

    internal static async Task<string> ReadBoundedAsync(
        TextReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (maximumCharacters < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        }

        var buffer = new char[8192];
        var builder = new StringBuilder(Math.Min(maximumCharacters, buffer.Length));
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var remaining = maximumCharacters - builder.Length;
            if (remaining > 0)
            {
                builder.Append(buffer, 0, Math.Min(read, remaining));
            }

            truncated |= read > remaining;
        }

        if (truncated)
        {
            builder.AppendLine();
            builder.Append(TruncationMarker);
        }

        return builder.ToString();
    }

    internal static bool WasTruncated(string value) =>
        value.EndsWith(TruncationMarker, StringComparison.Ordinal);

    public static string RedactSecrets(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value = CredentialsInUrlRegex().Replace(value, "${scheme}[REDACTED]@");
        value = SecretAssignmentRegex().Replace(value, "${name}=[REDACTED]");
        return UnsafeControlCharactersRegex().Replace(value, string.Empty);
    }

    internal static bool IsExpectedTerminationException(Exception exception) => exception is
        InvalidOperationException or
        Win32Exception or
        NotSupportedException or
        AggregateException;

    [GeneratedRegex("(?<scheme>https?://)[^/@\\s:]+:[^/@\\s]+@", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex CredentialsInUrlRegex();

    [GeneratedRegex("(?<name>(?:password|token|access_token|client_secret|apikey|api_key|username|signature|sig))\\s*=\\s*[^;&\\s\"',}\\]]+", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex("[\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F\\x7F]", RegexOptions.NonBacktracking)]
    private static partial Regex UnsafeControlCharactersRegex();
}
