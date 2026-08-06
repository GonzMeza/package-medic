using System.ComponentModel;
using System.Security;
using System.Text.RegularExpressions;

namespace PackageMedic.Core;

public sealed record BuildTargetVerificationResult(
    string Target,
    VerificationBuildTargetKind Kind,
    VerificationStageStatus Status,
    int? ExitCode = null,
    VerificationFailureKind? FailureKind = null,
    string? Error = null);

public sealed record BuildVerificationResult(
    VerificationStageEvidence Evidence,
    IReadOnlyList<BuildTargetVerificationResult> Targets,
    IReadOnlyList<string> Errors)
{
    public bool Succeeded => Evidence.Status == VerificationStageStatus.Passed;
}

/// <summary>
/// Builds an already validated verification plan inside a disposable snapshot. The runner emits
/// one fixed dotnet-build command shape and deliberately provides no extension point for arbitrary
/// commands or MSBuild properties.
/// </summary>
public sealed class BuildVerificationRunner
{
    internal const int MaximumConfigurationCharacters = 256;
    internal const int MaximumErrorCharacters = 4_096;
    internal const int MaximumErrorSourceCharacters = 64 * 1024;

    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromHours(1);
    private static readonly TimeSpan DefaultTargetTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DefaultTotalTimeout = TimeSpan.FromHours(1);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly Regex DeterministicDiagnostic = new(
        @"\berror\s+(?:CS|FS|BC|RZ|XLS|XAML)\d{3,6}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly string[] OperationalFailureMarkers =
    [
        "no space left on device",
        "disk full",
        "out of memory",
        "insufficient memory",
        "process was killed",
        "workload must be installed",
        "workload is not installed",
        "could not resolve the sdk",
        "requested sdk version was not found",
        "msb4236",
        "netsdk1147",
    ];

    private readonly IProcessRunner processRunner;
    private readonly TimeSpan targetTimeout;
    private readonly TimeSpan totalTimeout;

    public BuildVerificationRunner(IProcessRunner processRunner)
        : this(processRunner, DefaultTargetTimeout, DefaultTotalTimeout)
    {
    }

    public BuildVerificationRunner(
        IProcessRunner processRunner,
        TimeSpan targetTimeout,
        TimeSpan totalTimeout)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        ValidateTimeout(targetTimeout, nameof(targetTimeout));
        ValidateTimeout(totalTimeout, nameof(totalTimeout));
        this.targetTimeout = targetTimeout;
        this.totalTimeout = totalTimeout;
    }

    public async Task<BuildVerificationResult> RunAsync(
        VerificationPlan plan,
        string snapshotRoot,
        string configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plan.BuildTargets);
        var root = ValidateSnapshotRoot(snapshotRoot);
        ValidateConfiguration(configuration);
        var targets = ValidateTargets(plan.BuildTargets, root);

        if (targets.Count == 0)
        {
            return BlockedWithoutTarget(
                VerificationFailureKind.Unknown,
                "The build verification plan contains no targets.");
        }

        var outcomes = new List<BuildTargetVerificationResult>(targets.Count);
        using var totalSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalSource.CancelAfter(totalTimeout);
        foreach (var target in targets)
        {
            if (totalSource.IsCancellationRequested)
            {
                var failureKind = cancellationToken.IsCancellationRequested
                    ? VerificationFailureKind.Cancelled
                    : VerificationFailureKind.TimedOut;
                return Blocked(
                    outcomes,
                    target,
                    failureKind,
                    failureKind == VerificationFailureKind.Cancelled
                        ? "Build verification was cancelled."
                        : $"Build verification exceeded the {totalTimeout.TotalSeconds:0}-second total timeout.");
            }

            ProcessResult result;
            using var targetSource = CancellationTokenSource.CreateLinkedTokenSource(totalSource.Token);
            targetSource.CancelAfter(targetTimeout);
            try
            {
                result = await processRunner.RunAsync(
                    "dotnet",
                    BuildArguments(target, configuration),
                    Path.GetDirectoryName(target.Path)!,
                    targetSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var failureKind = cancellationToken.IsCancellationRequested
                    ? VerificationFailureKind.Cancelled
                    : VerificationFailureKind.TimedOut;
                return Blocked(
                    outcomes,
                    target,
                    failureKind,
                    failureKind == VerificationFailureKind.Cancelled
                        ? $"dotnet build was cancelled for '{target.Path}'."
                        : $"dotnet build timed out for '{target.Path}'.");
            }
            catch (Exception exception) when (IsProcessStartFailure(exception))
            {
                return Blocked(
                    outcomes,
                    target,
                    VerificationFailureKind.ProcessStartFailed,
                    $"dotnet build could not start for '{target.Path}'.");
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                return Blocked(
                    outcomes,
                    target,
                    VerificationFailureKind.Unknown,
                    $"dotnet build could not be completed for '{target.Path}'.");
            }

            if (result is null)
            {
                return Blocked(
                    outcomes,
                    target,
                    VerificationFailureKind.Unknown,
                    $"dotnet build returned no process result for '{target.Path}'.");
            }

            if (result.StandardOutputTruncated || result.StandardErrorTruncated)
            {
                return Blocked(
                    outcomes,
                    target,
                    VerificationFailureKind.OutputLimitExceeded,
                    $"dotnet build output exceeded the {ProcessRunner.DefaultMaximumOutputCharacters}-character safety limit for '{target.Path}'.",
                    result.ExitCode);
            }

            if (result.ExitCode != 0)
            {
                if (IsDeterministicBuildFailure(result))
                {
                    return Failed(
                        outcomes,
                        target,
                        result.ExitCode,
                        $"dotnet build reported a compiler failure for '{target.Path}' with exit code {result.ExitCode}.");
                }

                return Blocked(
                    outcomes,
                    target,
                    VerificationFailureKind.Unknown,
                    $"dotnet build exited unsuccessfully for '{target.Path}', but the failure could not be attributed reliably to the candidate.",
                    result.ExitCode);
            }

            outcomes.Add(new BuildTargetVerificationResult(
                target.Path,
                target.Kind,
                VerificationStageStatus.Passed,
                result.ExitCode));
        }

        return new BuildVerificationResult(
            VerificationStageEvidence.Passed,
            outcomes.ToArray(),
            []);
    }

    private static IReadOnlyList<string> BuildArguments(
        VerificationBuildTarget target,
        string configuration) =>
    [
        "build",
        target.Path,
        "--no-restore",
        "--nologo",
        "--verbosity",
        "minimal",
        "--configuration",
        configuration,
        "--property:UseSharedCompilation=false",
    ];

    private static BuildVerificationResult Failed(
        List<BuildTargetVerificationResult> outcomes,
        VerificationBuildTarget target,
        int exitCode,
        string error)
    {
        var sanitized = SanitizeAndBound(error);
        outcomes.Add(new BuildTargetVerificationResult(
            target.Path,
            target.Kind,
            VerificationStageStatus.Failed,
            exitCode,
            VerificationFailureKind.BuildFailed,
            sanitized));
        return new BuildVerificationResult(
            VerificationStageEvidence.Failed(VerificationFailureKind.BuildFailed),
            outcomes.ToArray(),
            [sanitized]);
    }

    private static BuildVerificationResult Blocked(
        List<BuildTargetVerificationResult> outcomes,
        VerificationBuildTarget target,
        VerificationFailureKind failureKind,
        string error,
        int? exitCode = null)
    {
        var sanitized = SanitizeAndBound(error);
        outcomes.Add(new BuildTargetVerificationResult(
            target.Path,
            target.Kind,
            VerificationStageStatus.Incomplete,
            exitCode,
            failureKind,
            sanitized));
        return new BuildVerificationResult(
            VerificationStageEvidence.Incomplete(failureKind),
            outcomes.ToArray(),
            [sanitized]);
    }

    private static BuildVerificationResult BlockedWithoutTarget(
        VerificationFailureKind failureKind,
        string error)
    {
        var sanitized = SanitizeAndBound(error);
        return new BuildVerificationResult(
            VerificationStageEvidence.Incomplete(failureKind),
            [],
            [sanitized]);
    }

    private static bool IsDeterministicBuildFailure(ProcessResult result)
    {
        var output = string.Concat(result.StandardOutput, "\n", result.StandardError);
        if (OperationalFailureMarkers.Any(marker =>
                output.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return DeterministicDiagnostic.IsMatch(output);
    }

    private static string SanitizeAndBound(string value)
    {
        if (value.Length > MaximumErrorSourceCharacters)
        {
            value = value[..MaximumErrorSourceCharacters];
        }

        var sanitized = string.Join(
            ' ',
            ProcessRunner.RedactSecrets(value)
                .Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (sanitized.Length <= MaximumErrorCharacters)
        {
            return sanitized;
        }

        return sanitized[..MaximumErrorCharacters];
    }

    private static void ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan || timeout > MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Build verification timeouts must be greater than zero and no longer than {MaximumTimeout.TotalHours:0} hour.");
        }
    }

    private static string ValidateSnapshotRoot(string snapshotRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotRoot);
        if (!Path.IsPathFullyQualified(snapshotRoot))
        {
            throw new ArgumentException("The build verification snapshot root must be absolute.", nameof(snapshotRoot));
        }

        var root = Path.GetFullPath(snapshotRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"The build verification snapshot does not exist: {root}");
        }

        if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("The build verification snapshot root cannot be a symbolic link or junction.");
        }

        return root;
    }

    private static void ValidateConfiguration(string configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        if (configuration.Length > MaximumConfigurationCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                $"The build configuration exceeds the {MaximumConfigurationCharacters}-character safety limit.");
        }

        if (configuration.Any(char.IsControl))
        {
            throw new ArgumentException("The build configuration cannot contain control characters.", nameof(configuration));
        }
    }

    private static IReadOnlyList<VerificationBuildTarget> ValidateTargets(
        IReadOnlyList<VerificationBuildTarget> targets,
        string snapshotRoot)
    {
        if (targets.Count > VerificationPlanBuilder.MaximumBuildTargets)
        {
            throw new InvalidDataException(
                $"The build verification plan exceeds the {VerificationPlanBuilder.MaximumBuildTargets}-target safety limit.");
        }

        var seen = new HashSet<string>(PathComparer);
        var normalized = new List<VerificationBuildTarget>(targets.Count);
        foreach (var target in targets)
        {
            ArgumentNullException.ThrowIfNull(target);
            if (!Enum.IsDefined(target.Kind))
            {
                throw new ArgumentOutOfRangeException(nameof(targets), target.Kind, "Unknown build target kind.");
            }

            if (!Path.IsPathFullyQualified(target.Path))
            {
                throw new ArgumentException("Build verification targets must be absolute.", nameof(targets));
            }

            var path = Path.GetFullPath(target.Path);
            if (!File.Exists(path) || !ProjectDiscovery.IsSafelyContained(snapshotRoot, path))
            {
                throw new InvalidDataException(
                    $"Build verification target '{path}' must be a regular file inside the disposable snapshot.");
            }

            var extension = Path.GetExtension(path);
            var validKind = target.Kind switch
            {
                VerificationBuildTargetKind.Project => extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase),
                VerificationBuildTargetKind.Solution =>
                    extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
            if (!validKind)
            {
                throw new InvalidDataException(
                    $"Build verification target '{path}' does not match its declared {target.Kind} kind.");
            }

            if (!seen.Add(path))
            {
                throw new InvalidDataException($"Build verification target '{path}' appears more than once.");
            }

            normalized.Add(target with { Path = path });
        }

        return normalized;
    }

    private static bool IsProcessStartFailure(Exception exception) => exception is
        Win32Exception or
        FileNotFoundException or
        DirectoryNotFoundException or
        UnauthorizedAccessException or
        SecurityException or
        InvalidOperationException;

    private static bool IsRecoverable(Exception exception) => exception is not
        OutOfMemoryException and not
        AccessViolationException;
}
