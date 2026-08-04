using System.Collections.ObjectModel;
using System.Diagnostics;

namespace PackageMedic.Core;

/// <summary>
/// Describes the environment boundary for one subprocess invocation. The inherited
/// environment remains the compatibility default; security-sensitive callers should use
/// <see cref="CreateIsolatedDotNet"/> and an <see cref="EnvironmentScopedProcessRunner"/>.
/// </summary>
public sealed class ProcessEnvironment
{
    public const int MaximumVariableNameLength = 256;

    private static readonly string[] DotNetHostAllowList =
    [
        "PATH",
        "SystemRoot",
        "WINDIR",
        "COMSPEC",
        "PATHEXT",
        "OS",
        "PROCESSOR_ARCHITECTURE",
        "PROCESSOR_ARCHITEW6432",
        "NUMBER_OF_PROCESSORS",
        "ProgramFiles",
        "ProgramFiles(x86)",
        "ProgramW6432",
        "ProgramData",
        "DOTNET_ROOT",
        "DOTNET_ROOT_X64",
        "DOTNET_ROOT_X86",
        "LANG",
        "LC_ALL",
        "TZ",
    ];

    private static readonly string[] IsolatedPathVariables =
    [
        "NUGET_PACKAGES",
        "NUGET_HTTP_CACHE_PATH",
        "NUGET_PLUGINS_CACHE_PATH",
        "DOTNET_CLI_HOME",
        "HOME",
        "USERPROFILE",
        "APPDATA",
        "LOCALAPPDATA",
        "TEMP",
        "TMP",
        "TMPDIR",
    ];

    private readonly string[] secretValues;

    private ProcessEnvironment(
        bool clearInheritedEnvironment,
        IReadOnlyDictionary<string, string?> variables,
        IEnumerable<string>? secrets = null,
        IEnumerable<string>? untrustedExecutableRoots = null)
    {
        ClearInheritedEnvironment = clearInheritedEnvironment;
        var copy = new Dictionary<string, string?>(EnvironmentNameComparer);
        foreach (var (name, value) in variables)
        {
            ValidateVariable(name, value);
            copy.Add(name, value);
        }

        Variables = new ReadOnlyDictionary<string, string?>(copy);
        UntrustedExecutableRoots = (untrustedExecutableRoots ?? [])
            .Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
        secretValues = (secrets ?? [])
            .Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToArray();
    }

    public static ProcessEnvironment Inherit { get; } = new(false, new Dictionary<string, string?>());

    public bool ClearInheritedEnvironment { get; }

    public IReadOnlyDictionary<string, string?> Variables { get; }

    public IReadOnlyList<string> UntrustedExecutableRoots { get; }

    /// <summary>
    /// Creates non-destructive overrides over the caller environment. A null value removes
    /// that variable. Values listed in <paramref name="secrets"/> are redacted from output.
    /// </summary>
    public static ProcessEnvironment CreateOverrides(
        IReadOnlyDictionary<string, string?> variables,
        IEnumerable<string>? secrets = null,
        IEnumerable<string>? untrustedExecutableRoots = null)
    {
        ArgumentNullException.ThrowIfNull(variables);
        return new ProcessEnvironment(false, variables, secrets, untrustedExecutableRoots);
    }

    /// <summary>
    /// Validates a variable name before a caller explicitly copies it from the host.
    /// This is suitable for credential-variable CLI options; it validates only the name,
    /// never reads or exposes the corresponding value.
    /// </summary>
    public static void ValidateVariableName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > MaximumVariableNameLength ||
            !string.Equals(name, name.Trim(), StringComparison.Ordinal) ||
            name.Contains('=') ||
            name.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"Environment variable names must be at most {MaximumVariableNameLength} characters and cannot contain surrounding whitespace, '=' or control characters.",
                nameof(name));
        }
    }

    /// <summary>
    /// Creates a clean environment for dotnet/NuGet work inside an owned temporary root.
    /// Private-feed credentials and credential-provider variables are deliberately not inherited:
    /// callers must copy only the required variables explicitly and name secret-bearing variables
    /// in <paramref name="sensitiveVariableNames"/> so their values are redacted from child output.
    /// </summary>
    public static ProcessEnvironment CreateIsolatedDotNet(
        string ownedRoot,
        string? packagesDirectory = null,
        IReadOnlyDictionary<string, string>? additionalVariables = null,
        IReadOnlyCollection<string>? sensitiveVariableNames = null,
        IReadOnlyCollection<string>? untrustedExecutableRoots = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownedRoot);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ownedRoot));
        Directory.CreateDirectory(root);
        if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("The isolated process root cannot be a symbolic link or junction.");
        }
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var dotnetHome = CreateContainedDirectory(root, Path.Combine(root, "dotnet-home"));
        var appData = CreateContainedDirectory(root, Path.Combine(dotnetHome, "appdata"));
        var localAppData = CreateContainedDirectory(root, Path.Combine(dotnetHome, "local-appdata"));
        var packages = CreateContainedDirectory(
            root,
            packagesDirectory is null ? Path.Combine(root, "nuget", "packages") : packagesDirectory);
        var httpCache = CreateContainedDirectory(root, Path.Combine(root, "nuget", "http-cache"));
        var pluginsCache = CreateContainedDirectory(root, Path.Combine(root, "nuget", "plugins-cache"));
        var temporary = CreateContainedDirectory(root, Path.Combine(root, "temp"));

        var variables = new Dictionary<string, string?>(EnvironmentNameComparer);
        foreach (var name in DotNetHostAllowList)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is not null)
            {
                variables[name] = value;
            }
        }

        variables["DOTNET_CLI_HOME"] = dotnetHome;
        variables["HOME"] = dotnetHome;
        variables["USERPROFILE"] = dotnetHome;
        variables["APPDATA"] = appData;
        variables["LOCALAPPDATA"] = localAppData;
        variables["NUGET_PACKAGES"] = packages;
        variables["NUGET_HTTP_CACHE_PATH"] = httpCache;
        variables["NUGET_PLUGINS_CACHE_PATH"] = pluginsCache;
        variables["TEMP"] = temporary;
        variables["TMP"] = temporary;
        variables["TMPDIR"] = temporary;
        variables["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        variables["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        variables["DOTNET_NOLOGO"] = "1";
        variables["MSBUILDDISABLENODEREUSE"] = "1";
        variables["NUGET_XMLDOC_MODE"] = "skip";

        if (additionalVariables is not null)
        {
            foreach (var (name, value) in additionalVariables)
            {
                if (IsolatedPathVariables.Contains(name, EnvironmentNameComparer))
                {
                    throw new ArgumentException(
                        $"The isolated path variable '{name}' is controlled by PackageMedic.",
                        nameof(additionalVariables));
                }

                ValidateVariable(name, value);
                variables[name] = value;
            }
        }

        var secrets = new List<string>();
        if (sensitiveVariableNames is not null)
        {
            foreach (var name in sensitiveVariableNames)
            {
                if (!variables.TryGetValue(name, out var value) || string.IsNullOrEmpty(value))
                {
                    throw new ArgumentException(
                        $"Sensitive environment variable '{name}' does not have an explicit value.",
                        nameof(sensitiveVariableNames));
                }

                secrets.Add(value);
            }
        }

        return new ProcessEnvironment(true, variables, secrets, untrustedExecutableRoots);
    }

    internal void ApplyTo(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (ClearInheritedEnvironment)
        {
            startInfo.Environment.Clear();
        }

        foreach (var (name, value) in Variables)
        {
            if (value is null)
            {
                startInfo.Environment.Remove(name);
            }
            else
            {
                startInfo.Environment[name] = value;
            }
        }
    }

    internal string Redact(string value)
    {
        var redacted = ProcessRunner.RedactSecrets(value);
        foreach (var secret in secretValues)
        {
            redacted = redacted.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }

        return redacted;
    }

    private static string CreateContainedDirectory(string root, string candidate)
    {
        var path = Path.GetFullPath(candidate);
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathFullyQualified(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException("An isolated process directory must remain inside its owned root.");
        }

        Directory.CreateDirectory(path);
        if (!ProjectDiscovery.IsSafelyContained(root, path))
        {
            throw new InvalidOperationException("An isolated process directory became a symbolic link or junction.");
        }
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private static void ValidateVariable(string name, string? value)
    {
        ValidateVariableName(name);

        if (value?.Contains('\0') == true)
        {
            throw new ArgumentException("Environment variable values cannot contain null characters.", nameof(value));
        }
    }

    private static StringComparer EnvironmentNameComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

/// <summary>
/// Applies one environment boundary to every subprocess started through the wrapped runner.
/// </summary>
public sealed class EnvironmentScopedProcessRunner : IProcessRunner
{
    private readonly IProcessRunner inner;
    private readonly ProcessEnvironment environment;

    public EnvironmentScopedProcessRunner(IProcessRunner inner, ProcessEnvironment environment)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await inner.RunAsync(
            fileName,
            arguments,
            workingDirectory,
            environment,
            cancellationToken).ConfigureAwait(false);
        return result with
        {
            StandardOutput = environment.Redact(result.StandardOutput),
            StandardError = environment.Redact(result.StandardError),
        };
    }
}
