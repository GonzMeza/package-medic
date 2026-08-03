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

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

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
            RedactSecrets(await stdoutTask.ConfigureAwait(false)),
            RedactSecrets(await stderrTask.ConfigureAwait(false)));
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

    internal static string RedactSecrets(string value)
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

    [GeneratedRegex("(?<name>(?:password|token|apikey|api_key|username))\\s*=\\s*[^;\\s\"',}\\]]+", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex("[\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F\\x7F]", RegexOptions.NonBacktracking)]
    private static partial Regex UnsafeControlCharactersRegex();
}
