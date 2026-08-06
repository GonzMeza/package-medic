using System.ComponentModel;
using System.Security;
using System.Text.Json;

namespace PackageMedic.Core;

public sealed record TestProjectVerificationResult(
    string Project,
    VerificationTestRunnerKind Runner,
    VerificationStageStatus Status,
    int? ExitCode,
    VerificationFailureKind? FailureKind,
    int Total,
    int Passed,
    int Failed,
    int Skipped,
    IReadOnlyList<string> FailedTestIdentities,
    TrxTestEvidenceError? EvidenceError,
    string? Error);

public sealed record TestVerificationResult(
    VerificationStageEvidence Evidence,
    IReadOnlyList<TestProjectVerificationResult> Projects,
    long Total,
    long Passed,
    long Failed,
    long Skipped,
    IReadOnlyList<string> FailedTestIdentities,
    IReadOnlyList<string> Errors)
{
    public bool Succeeded => Evidence.Status == VerificationStageStatus.Passed;

    public bool HasAdditionalFailedTests => Failed > FailedTestIdentities.Count;
}

/// <summary>
/// Executes only the fixed VSTest and Microsoft Testing Platform command shapes declared by a
/// verification plan. Every project receives a separate PackageMedic-owned results directory;
/// only bounded, path-independent TRX evidence survives its disposal.
/// </summary>
public sealed class TestVerificationRunner
{
    internal const int MaximumConfigurationCharacters = 256;
    internal const int MaximumErrorCharacters = 4_096;

    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromHours(1);
    private static readonly TimeSpan DefaultProjectTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan DefaultTotalTimeout = TimeSpan.FromHours(1);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly IProcessRunner processRunner;
    private readonly TrxTestResultParser resultParser;
    private readonly TrxTestResultLimits resultLimits;
    private readonly TimeSpan projectTimeout;
    private readonly TimeSpan totalTimeout;

    public TestVerificationRunner(IProcessRunner processRunner)
        : this(
            processRunner,
            new TrxTestResultParser(),
            TrxTestResultLimits.Default,
            DefaultProjectTimeout,
            DefaultTotalTimeout)
    {
    }

    public TestVerificationRunner(
        IProcessRunner processRunner,
        TrxTestResultParser resultParser,
        TrxTestResultLimits resultLimits,
        TimeSpan projectTimeout,
        TimeSpan totalTimeout)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        this.resultParser = resultParser ?? throw new ArgumentNullException(nameof(resultParser));
        this.resultLimits = (resultLimits ?? throw new ArgumentNullException(nameof(resultLimits))).Validate();
        ValidateTimeout(projectTimeout, nameof(projectTimeout));
        ValidateTimeout(totalTimeout, nameof(totalTimeout));
        this.projectTimeout = projectTimeout;
        this.totalTimeout = totalTimeout;
    }

    public async Task<TestVerificationResult> RunAsync(
        VerificationPlan plan,
        string snapshotRoot,
        string configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plan.TestProjects);
        var root = ValidateSnapshotRoot(snapshotRoot);
        ValidateConfiguration(configuration);
        var projects = ValidateAndOrderProjects(plan.TestProjects, root);
        if (projects.Count == 0)
        {
            return EmptyResult(
                VerificationFailureKind.NoTestsDiscovered,
                "No projects explicitly identified by MSBuild as test projects were found.");
        }

        using var totalSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalSource.CancelAfter(totalTimeout);
        var outcomes = new List<TestProjectVerificationResult>(projects.Count);
        foreach (var project in projects)
        {
            if (totalSource.IsCancellationRequested)
            {
                var failure = cancellationToken.IsCancellationRequested
                    ? VerificationFailureKind.Cancelled
                    : VerificationFailureKind.TimedOut;
                outcomes.Add(IncompleteProject(
                    project,
                    PortableProject(root, project.ProjectPath),
                    failure,
                    failure == VerificationFailureKind.Cancelled
                        ? "Test verification was cancelled."
                        : "Test verification exceeded its total timeout."));
                return Aggregate(VerificationStageEvidence.Incomplete(failure), outcomes);
            }

            var outcome = await RunProjectAsync(
                project,
                root,
                configuration,
                totalSource.Token,
                cancellationToken).ConfigureAwait(false);
            outcomes.Add(outcome);
            if (outcome.Status != VerificationStageStatus.Passed)
            {
                var evidence = outcome.Status == VerificationStageStatus.Failed
                    ? VerificationStageEvidence.Failed(VerificationFailureKind.TestsFailed)
                    : VerificationStageEvidence.Incomplete(outcome.FailureKind ?? VerificationFailureKind.Unknown);
                return Aggregate(evidence, outcomes);
            }
        }

        return Aggregate(VerificationStageEvidence.Passed, outcomes);
    }

    private async Task<TestProjectVerificationResult> RunProjectAsync(
        VerificationTestProject project,
        string snapshotRoot,
        string configuration,
        CancellationToken totalToken,
        CancellationToken callerToken)
    {
        var portableProject = PortableProject(snapshotRoot, project.ProjectPath);
        var mode = await ResolveCliModeAsync(
                project,
                snapshotRoot,
                totalToken,
                callerToken)
            .ConfigureAwait(false);
        if (mode.Mode is null)
        {
            return IncompleteProject(
                project,
                portableProject,
                mode.FailureKind ?? VerificationFailureKind.UnsupportedRunner,
                mode.Error ?? $"The test runner could not be selected safely for '{portableProject}'.");
        }

        OwnedTemporaryDirectory resultsDirectory;
        try
        {
            resultsDirectory = OwnedTemporaryDirectory.Create(snapshotRoot);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return IncompleteProject(
                project,
                portableProject,
                VerificationFailureKind.Unknown,
                $"A private test-results directory could not be prepared for '{portableProject}'.");
        }

        TestProjectVerificationResult outcome;
        using var projectSource = CancellationTokenSource.CreateLinkedTokenSource(totalToken);
        projectSource.CancelAfter(projectTimeout);
        try
        {
            outcome = await ExecuteProjectAsync(
                project,
                resultsDirectory,
                snapshotRoot,
                configuration,
                mode.Mode.Value,
                portableProject,
                projectSource.Token,
                callerToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            outcome = IncompleteProject(
                project,
                portableProject,
                VerificationFailureKind.Unknown,
                $"Test verification could not be completed for '{portableProject}'.");
        }

        try
        {
            resultsDirectory.Dispose();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            outcome = IncompleteProject(
                project,
                portableProject,
                VerificationFailureKind.Unknown,
                $"The private test-results directory could not be safely removed for '{portableProject}'.",
                outcome.ExitCode,
                outcome.Total,
                outcome.Passed,
                outcome.Failed,
                outcome.Skipped,
                outcome.FailedTestIdentities,
                outcome.EvidenceError);
        }

        return outcome;
    }

    private async Task<TestProjectVerificationResult> ExecuteProjectAsync(
        VerificationTestProject project,
        OwnedTemporaryDirectory resultsDirectory,
        string snapshotRoot,
        string configuration,
        DotNetTestCliMode cliMode,
        string portableProject,
        CancellationToken projectToken,
        CancellationToken callerToken)
    {
        ProcessResult processResult;
        try
        {
            processResult = await processRunner.RunAsync(
                "dotnet",
                BuildArguments(project, configuration, resultsDirectory.DirectoryPath, cliMode),
                Path.GetDirectoryName(project.ProjectPath)!,
                projectToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var failure = callerToken.IsCancellationRequested
                ? VerificationFailureKind.Cancelled
                : VerificationFailureKind.TimedOut;
            return IncompleteProject(
                project,
                portableProject,
                failure,
                failure == VerificationFailureKind.Cancelled
                    ? $"Tests were cancelled for '{portableProject}'."
                    : $"Tests timed out for '{portableProject}'.");
        }
        catch (Exception exception) when (IsProcessStartFailure(exception))
        {
            return IncompleteProject(
                project,
                portableProject,
                VerificationFailureKind.ProcessStartFailed,
                $"dotnet test could not start for '{portableProject}'.");
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return IncompleteProject(
                project,
                portableProject,
                VerificationFailureKind.Unknown,
                $"dotnet test could not be completed for '{portableProject}'.");
        }

        if (processResult is null)
        {
            return IncompleteProject(
                project,
                portableProject,
                VerificationFailureKind.Unknown,
                $"dotnet test returned no process result for '{portableProject}'.");
        }

        if (processResult.StandardOutputTruncated || processResult.StandardErrorTruncated)
        {
            return IncompleteProject(
                project,
                portableProject,
                VerificationFailureKind.OutputLimitExceeded,
                $"dotnet test output exceeded the safety limit for '{portableProject}'.",
                processResult.ExitCode);
        }

        TrxTestEvidence trx;
        try
        {
            trx = await resultParser.ParseAsync(
                resultsDirectory,
                resultLimits,
                projectToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var failure = callerToken.IsCancellationRequested
                ? VerificationFailureKind.Cancelled
                : VerificationFailureKind.TimedOut;
            return IncompleteProject(
                project,
                portableProject,
                failure,
                failure == VerificationFailureKind.Cancelled
                    ? $"Test result processing was cancelled for '{portableProject}'."
                    : $"Test result processing timed out for '{portableProject}'.",
                processResult.ExitCode);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return IncompleteProject(
                project,
                portableProject,
                VerificationFailureKind.Unknown,
                $"Test results could not be processed for '{portableProject}'.",
                processResult.ExitCode);
        }

        return Classify(project, portableProject, processResult.ExitCode, trx);
    }

    private static TestProjectVerificationResult Classify(
        VerificationTestProject project,
        string portableProject,
        int exitCode,
        TrxTestEvidence trx)
    {
        if (trx.Status == TrxTestEvidenceStatus.Incomplete)
        {
            var evidence = trx.ToVerificationStageEvidence();
            return IncompleteProject(
                project,
                portableProject,
                evidence.FailureKind ?? VerificationFailureKind.TestResultsUnavailable,
                $"Complete test evidence was unavailable for '{portableProject}'.",
                exitCode,
                trx.Total,
                trx.Passed,
                trx.Failed,
                trx.Skipped,
                trx.FailedTestIdentities,
                trx.Error);
        }

        var processPassed = exitCode == 0;
        var evidencePassed = trx.Status == TrxTestEvidenceStatus.Passed;
        if (processPassed != evidencePassed)
        {
            return IncompleteProject(
                project,
                portableProject,
                VerificationFailureKind.TestResultsUnavailable,
                $"The dotnet test exit code contradicted its TRX evidence for '{portableProject}'.",
                exitCode,
                trx.Total,
                trx.Passed,
                trx.Failed,
                trx.Skipped,
                trx.FailedTestIdentities,
                trx.Error);
        }

        if (!processPassed)
        {
            return new TestProjectVerificationResult(
                portableProject,
                project.Runner,
                VerificationStageStatus.Failed,
                exitCode,
                VerificationFailureKind.TestsFailed,
                trx.Total,
                trx.Passed,
                trx.Failed,
                trx.Skipped,
                trx.FailedTestIdentities.ToArray(),
                trx.Error,
                SanitizeAndBound($"Tests failed for '{portableProject}'."));
        }

        return new TestProjectVerificationResult(
            portableProject,
            project.Runner,
            VerificationStageStatus.Passed,
            exitCode,
            null,
            trx.Total,
            trx.Passed,
            trx.Failed,
            trx.Skipped,
            trx.FailedTestIdentities.ToArray(),
            trx.Error,
            null);
    }

    private static IReadOnlyList<string> BuildArguments(
        VerificationTestProject project,
        string configuration,
        string resultsDirectory,
        DotNetTestCliMode cliMode) => (cliMode, project.Runner) switch
        {
            (DotNetTestCliMode.VSTest, VerificationTestRunnerKind.VSTest) =>
            [
                "test",
                project.ProjectPath,
                "--no-build",
                "--no-restore",
                "--nologo",
                "--configuration",
                configuration,
                "--logger",
                "trx",
                "--results-directory",
                resultsDirectory,
            ],
            (DotNetTestCliMode.VSTest, VerificationTestRunnerKind.MicrosoftTestingPlatform) =>
            [
                "test",
                project.ProjectPath,
                "--no-build",
                "--no-restore",
                "--configuration",
                configuration,
                "--results-directory",
                resultsDirectory,
                "--",
                "--report-trx",
            ],
            (DotNetTestCliMode.MicrosoftTestingPlatform, VerificationTestRunnerKind.MicrosoftTestingPlatform) =>
            [
                "test",
                "--project",
                project.ProjectPath,
                "--no-build",
                "--no-restore",
                "--configuration",
                configuration,
                "--results-directory",
                resultsDirectory,
                "--",
                "--report-trx",
            ],
            _ => throw new InvalidOperationException(
                "The selected dotnet test mode is incompatible with the test project."),
        };

    private async Task<TestCliModeResolution> ResolveCliModeAsync(
        VerificationTestProject project,
        string snapshotRoot,
        CancellationToken operationToken,
        CancellationToken callerToken)
    {
        string? configuredRunner;
        try
        {
            configuredRunner = ReadConfiguredTestRunner(project.ProjectPath, snapshotRoot);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return TestCliModeResolution.Incomplete(
                VerificationFailureKind.UnsupportedRunner,
                "The repository global.json test runner selection could not be validated.");
        }

        if (configuredRunner is null || configuredRunner.Equals("VSTest", StringComparison.OrdinalIgnoreCase))
        {
            return TestCliModeResolution.Resolved(DotNetTestCliMode.VSTest);
        }

        if (!configuredRunner.Equals("Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase) ||
            project.Runner != VerificationTestRunnerKind.MicrosoftTestingPlatform)
        {
            return TestCliModeResolution.Incomplete(
                VerificationFailureKind.UnsupportedRunner,
                "The repository test runner selection is unsupported or mixes VSTest projects with native Microsoft Testing Platform mode.");
        }

        ProcessResult version;
        try
        {
            version = await processRunner.RunAsync(
                "dotnet",
                ["--version"],
                Path.GetDirectoryName(project.ProjectPath)!,
                operationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return TestCliModeResolution.Incomplete(
                callerToken.IsCancellationRequested
                    ? VerificationFailureKind.Cancelled
                    : VerificationFailureKind.TimedOut,
                callerToken.IsCancellationRequested
                    ? "Test runner detection was cancelled."
                    : "Test runner detection timed out.");
        }
        catch (Exception exception) when (IsProcessStartFailure(exception))
        {
            return TestCliModeResolution.Incomplete(
                VerificationFailureKind.ProcessStartFailed,
                "dotnet could not start while validating native Microsoft Testing Platform mode.");
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return TestCliModeResolution.Incomplete(
                VerificationFailureKind.UnsupportedRunner,
                "The active .NET SDK could not be validated for native Microsoft Testing Platform mode.");
        }

        var value = version.StandardOutput.Trim();
        var majorText = value.Split('.', 2)[0];
        if (version.ExitCode != 0 || version.StandardOutputTruncated || version.StandardErrorTruncated ||
            value.Contains('\r', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal) ||
            !int.TryParse(
                majorText,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var major) ||
            major < 10)
        {
            return TestCliModeResolution.Incomplete(
                VerificationFailureKind.UnsupportedRunner,
                "Native Microsoft Testing Platform mode requires a successfully selected .NET 10 or newer SDK.");
        }

        return TestCliModeResolution.Resolved(DotNetTestCliMode.MicrosoftTestingPlatform);
    }

    private static string? ReadConfiguredTestRunner(string projectPath, string snapshotRoot)
    {
        var root = Path.GetFullPath(snapshotRoot);
        var directory = new DirectoryInfo(Path.GetDirectoryName(projectPath)!);
        string? globalJson = null;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "global.json");
            if (File.Exists(candidate))
            {
                if (!GitSnapshotProvider.IsWithin(candidate, root))
                {
                    throw new InvalidDataException(
                        "dotnet test runner selection would depend on a global.json outside the immutable snapshot.");
                }

                globalJson = candidate;
                break;
            }

            directory = directory.Parent;
        }

        if (globalJson is null)
        {
            return null;
        }

        var info = new FileInfo(globalJson);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > 1024 * 1024)
        {
            throw new InvalidDataException("The repository global.json is not a bounded regular file.");
        }

        using var stream = new FileStream(globalJson, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 32,
        });
        if (!document.RootElement.TryGetProperty("test", out var test) ||
            test.ValueKind != JsonValueKind.Object ||
            !test.TryGetProperty("runner", out var runner))
        {
            return null;
        }

        return runner.ValueKind == JsonValueKind.String
            ? runner.GetString()
            : throw new InvalidDataException("The global.json test runner must be a string.");
    }

    private static TestProjectVerificationResult IncompleteProject(
        VerificationTestProject project,
        string portableProject,
        VerificationFailureKind failureKind,
        string error,
        int? exitCode = null,
        int total = 0,
        int passed = 0,
        int failed = 0,
        int skipped = 0,
        IReadOnlyList<string>? failedIdentities = null,
        TrxTestEvidenceError? evidenceError = null) => new(
        portableProject,
        project.Runner,
        VerificationStageStatus.Incomplete,
        exitCode,
        failureKind,
        total,
        passed,
        failed,
        skipped,
        failedIdentities?.ToArray() ?? [],
        evidenceError,
        SanitizeAndBound(error));

    private static TestVerificationResult Aggregate(
        VerificationStageEvidence evidence,
        IReadOnlyList<TestProjectVerificationResult> projects)
    {
        var failures = projects
            .SelectMany(project => project.FailedTestIdentities.Select(identity => $"{project.Project}::{identity}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var errors = projects
            .Select(project => project.Error)
            .Where(error => !string.IsNullOrWhiteSpace(error))
            .Select(error => error!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new TestVerificationResult(
            evidence,
            projects.ToArray(),
            projects.Sum(project => (long)project.Total),
            projects.Sum(project => (long)project.Passed),
            projects.Sum(project => (long)project.Failed),
            projects.Sum(project => (long)project.Skipped),
            failures,
            errors);
    }

    private static TestVerificationResult EmptyResult(
        VerificationFailureKind failureKind,
        string error)
    {
        var sanitized = SanitizeAndBound(error);
        return new TestVerificationResult(
            VerificationStageEvidence.Incomplete(failureKind),
            [],
            0,
            0,
            0,
            0,
            [],
            [sanitized]);
    }

    private static IReadOnlyList<VerificationTestProject> ValidateAndOrderProjects(
        IReadOnlyList<VerificationTestProject> projects,
        string snapshotRoot)
    {
        if (projects.Count > VerificationPlanBuilder.MaximumTestProjects)
        {
            throw new InvalidDataException(
                $"The test verification plan exceeds the {VerificationPlanBuilder.MaximumTestProjects}-project safety limit.");
        }

        var seen = new HashSet<string>(PathComparer);
        var normalized = new List<VerificationTestProject>(projects.Count);
        foreach (var project in projects)
        {
            ArgumentNullException.ThrowIfNull(project);
            if (!Enum.IsDefined(project.Runner))
            {
                throw new ArgumentOutOfRangeException(nameof(projects), project.Runner, "Unknown test runner.");
            }

            if (!Path.IsPathFullyQualified(project.ProjectPath))
            {
                throw new ArgumentException("Test project paths must be absolute.", nameof(projects));
            }

            var projectPath = Path.GetFullPath(project.ProjectPath);
            if (!Path.GetExtension(projectPath).Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(projectPath) ||
                !ProjectDiscovery.IsSafelyContained(snapshotRoot, projectPath))
            {
                throw new InvalidDataException(
                    $"Test project '{projectPath}' must be a regular C# project inside the disposable snapshot.");
            }

            _ = PortableProject(snapshotRoot, projectPath);

            if (!seen.Add(projectPath))
            {
                throw new InvalidDataException($"Test project '{projectPath}' appears more than once.");
            }

            normalized.Add(project with { ProjectPath = projectPath });
        }

        return normalized
            .OrderBy(project => project.ProjectPath, PathComparer)
            .ToArray();
    }

    private static string PortableProject(string snapshotRoot, string projectPath)
    {
        var portable = Path.GetRelativePath(snapshotRoot, projectPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (Path.IsPathFullyQualified(portable) ||
            portable.Equals("..", StringComparison.Ordinal) ||
            portable.StartsWith("../", StringComparison.Ordinal) ||
            portable.Any(char.IsControl))
        {
            throw new InvalidDataException("A test project could not be represented by a safe portable path.");
        }

        return portable;
    }

    private static string ValidateSnapshotRoot(string snapshotRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotRoot);
        if (!Path.IsPathFullyQualified(snapshotRoot))
        {
            throw new ArgumentException("The test verification snapshot root must be absolute.", nameof(snapshotRoot));
        }

        var root = Path.GetFullPath(snapshotRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"The test verification snapshot does not exist: {root}");
        }

        if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("The test verification snapshot root cannot be a symbolic link or junction.");
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
                $"The test configuration exceeds the {MaximumConfigurationCharacters}-character safety limit.");
        }

        if (configuration.Any(char.IsControl))
        {
            throw new ArgumentException("The test configuration cannot contain control characters.", nameof(configuration));
        }
    }

    private static void ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan || timeout > MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Test verification timeouts must be greater than zero and no longer than {MaximumTimeout.TotalHours:0} hour.");
        }
    }

    private static string SanitizeAndBound(string value)
    {
        var sanitized = string.Join(
            ' ',
            ProcessRunner.RedactSecrets(value)
                .Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return sanitized.Length <= MaximumErrorCharacters
            ? sanitized
            : sanitized[..MaximumErrorCharacters];
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
        AccessViolationException and not
        StackOverflowException;

    private enum DotNetTestCliMode
    {
        VSTest,
        MicrosoftTestingPlatform,
    }

    private sealed record TestCliModeResolution(
        DotNetTestCliMode? Mode,
        VerificationFailureKind? FailureKind,
        string? Error)
    {
        public static TestCliModeResolution Resolved(DotNetTestCliMode mode) => new(mode, null, null);

        public static TestCliModeResolution Incomplete(
            VerificationFailureKind failureKind,
            string error) => new(null, failureKind, error);
    }
}
