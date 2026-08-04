using System.Security.Cryptography;
using PackageMedic.Core;

namespace PackageMedic.Cli;

public static class Program
{
    private static readonly byte[] NewLineUtf8 = [(byte)'\n'];

    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            return await ExecuteAsync(args, Console.Out, Console.Error, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    public static async Task<int> ExecuteAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        CliOptions options;
        try
        {
            options = CliOptions.Parse(args);
        }
        catch (UsageException exception)
        {
            await error.WriteLineAsync($"error: {ProcessRunner.RedactSecrets(exception.Message)}").ConfigureAwait(false);
            await error.WriteLineAsync("Run 'package-medic --help' for usage.").ConfigureAwait(false);
            return 2;
        }

        if (options.ShowHelp || options.Command == CliCommand.Help)
        {
            await output.WriteAsync(HelpText).ConfigureAwait(false);
            return 0;
        }

        try
        {
            return options.Command switch
            {
                CliCommand.Version => await WriteVersionAsync(output).ConfigureAwait(false),
                CliCommand.Rules => await WriteRulesAsync(output).ConfigureAwait(false),
                CliCommand.Explain => await ExplainRuleAsync(options.RuleCode!, output).ConfigureAwait(false),
                CliCommand.Init => await InitializeConfigurationAsync(options, output, cancellationToken).ConfigureAwait(false),
                CliCommand.BaselineCreate or CliCommand.BaselineUpdate =>
                    await WriteBaselineAsync(options, output, error, cancellationToken).ConfigureAwait(false),
                CliCommand.Clean => await WriteCleanPlanAsync(options, output, error, cancellationToken).ConfigureAwait(false),
                CliCommand.Simulate => await RunSimulationV2Async(options, output, error, cancellationToken).ConfigureAwait(false),
                CliCommand.Doctor or CliCommand.Audit =>
                    await RunDoctorAsync(options, output, error, cancellationToken).ConfigureAwait(false),
                CliCommand.Diff => await RunDiffAsync(options, output, error, cancellationToken).ConfigureAwait(false),
                _ => throw new UsageException("A command is required."),
            };
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("error: analysis was cancelled.").ConfigureAwait(false);
            return 2;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            System.ComponentModel.Win32Exception or
            InvalidDataException or
            InvalidOperationException or
            UnauthorizedAccessException or
            IOException or
            AggregateException or
            UsageException or
            PackageMedicConfigurationException)
        {
            await error.WriteLineAsync($"error: {ProcessRunner.RedactSecrets(exception.Message)}").ConfigureAwait(false);
            return 2;
        }
    }

    private static async Task<int> RunDoctorAsync(
        CliOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ValidateOutputPaths(options);
        var prepared = await AnalyzeAsync(options, error, cancellationToken).ConfigureAwait(false);
        await WriteAnalysisReportsAsync(
            prepared,
            options,
            output,
            error,
            null,
            cancellationToken).ConfigureAwait(false);

        if (prepared.HasOperationalError)
        {
            return 2;
        }

        var allReached = ReachesThreshold(prepared.Result.Diagnostics, prepared.Context.Policy.FailOn);
        var newDiagnostics = prepared.Context.Baseline.Current
            .Where(item => item.State == BaselineDiagnosticState.New)
            .Select(item => item.Diagnostic);
        var newReached = prepared.Context.Policy.FailOnNew is { } failOnNew &&
                         ReachesThreshold(newDiagnostics, failOnNew);
        return allReached || newReached ? 1 : 0;
    }

    private static async Task<int> RunDiffAsync(
        CliOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ValidateOutputPaths(options);
        var currentRepositoryRoot = FindRepositoryRoot(options.Path);
        var currentTarget = Path.GetFullPath(options.Path ?? Directory.GetCurrentDirectory());
        EnsureWithinRepository(currentTarget, currentRepositoryRoot, "The diff target");
        if (options.NoRestore && options.Verbosity != OutputVerbosity.Quiet)
        {
            await error.WriteLineAsync(
                "warning: diff --no-restore requires usable assets files to be tracked in both the current tree and Git reference.")
                .ConfigureAwait(false);
        }

        var processRunner = new ProcessRunner();
        using var snapshot = await new GitSnapshotProvider(processRunner)
            .MaterializeAsync(currentRepositoryRoot, options.GitReference!, cancellationToken)
            .ConfigureAwait(false);
        await new GitWorkingTreeInspector(processRunner)
            .EnsureArchiveSemanticsAreReproducibleAsync(
                currentRepositoryRoot,
                snapshot.Commit,
                cancellationToken)
            .ConfigureAwait(false);
        var relativeTarget = Path.GetRelativePath(currentRepositoryRoot, currentTarget);
        var snapshotTarget = Path.GetFullPath(Path.Combine(snapshot.SnapshotDirectory, relativeTarget));
        EnsureWithinRepository(snapshotTarget, snapshot.SnapshotDirectory, "The snapshot target");
        if (!File.Exists(snapshotTarget) && !Directory.Exists(snapshotTarget))
        {
            throw new InvalidOperationException(
                $"The target '{ToPortableDisplayPath(currentTarget, currentRepositoryRoot)}' does not exist in Git reference '{options.GitReference}'.");
        }

        using var currentRuntimeRoot = OwnedTemporaryDirectory.Create(currentRepositoryRoot);
        using var baselineRuntimeRoot = OwnedTemporaryDirectory.Create(currentRepositoryRoot);
        var untrustedRoots = new[] { currentRepositoryRoot, snapshot.SnapshotDirectory };
        var currentRuntime = CreateDiffRuntime(currentRuntimeRoot.DirectoryPath, untrustedRoots, processRunner);
        var baselineRuntime = CreateDiffRuntime(baselineRuntimeRoot.DirectoryPath, untrustedRoots, processRunner);
        var current = await AnalyzeAsync(
            options,
            error,
            cancellationToken,
            currentRepositoryRoot,
            packagesDirectory: currentRuntime.PackagesDirectory,
            processRunnerOverride: currentRuntime.ProcessRunner).ConfigureAwait(false);
        var baselineOptions = options with
        {
            Path = snapshotTarget,
            OutputPath = null,
            SarifOutputPath = null,
            BaselinePath = null,
            FailOnNew = null,
            GitReference = null,
        };
        var baseline = await AnalyzeAsync(
            baselineOptions,
            error,
            cancellationToken,
            snapshot.SnapshotDirectory,
            packagesDirectory: baselineRuntime.PackagesDirectory,
            processRunnerOverride: baselineRuntime.ProcessRunner).ConfigureAwait(false);
        var comparison = AnalysisDiffComparer.Compare(
            baseline.Result,
            snapshot.SnapshotDirectory,
            current.Result,
            currentRepositoryRoot,
            snapshot.Reference,
            snapshot.Commit,
            current.Context.Policy.Impact);

        var gateFingerprints = comparison.Changes
            .Where(change => change.Kind == DiagnosticChangeKind.Added ||
                             change.Kind == DiagnosticChangeKind.SeverityChanged &&
                             change.After!.Severity > change.Before!.Severity)
            .Select(change => change.Fingerprint)
            .ToHashSet(StringComparer.Ordinal);
        var gateDiagnostics = AnalysisDiffComparer.SelectDiagnosticsByFingerprint(
            current.Result,
            currentRepositoryRoot,
            gateFingerprints);
        var diffResult = current.Result with
        {
            Diagnostics = gateDiagnostics,
            Summary = Recount(current.Result.Summary, gateDiagnostics),
        };
        var diffBaseline = BaselineMatcher.Compare(
            diffResult,
            new PackageMedicBaseline(PackageMedicBaseline.CurrentSchemaVersion, diffResult.Version, []),
            currentRepositoryRoot);
        var diffPrepared = current with
        {
            Result = diffResult,
            Context = current.Context with { Baseline = diffBaseline, BaselineFile = null },
        };
        await WriteAnalysisReportsAsync(
            diffPrepared,
            options,
            output,
            error,
            comparison,
            cancellationToken).ConfigureAwait(false);

        if (!comparison.IsComplete || current.HasOperationalError || baseline.HasOperationalError)
        {
            return 2;
        }

        return comparison.Impact?.GatePassed == false ||
               ReachesThreshold(gateDiagnostics, current.Context.Policy.FailOn)
            ? 1
            : 0;
    }

    private static async Task<int> RunSimulationV2Async(
        CliOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        PackageVersionEditor.ValidatePackageId(options.SimulationPackageId!);
        PackageVersionEditor.ValidateExactVersion(options.SimulationTargetVersion!);
        var repositoryRoot = FindRepositoryRoot(options.Path);
        var currentTarget = Path.GetFullPath(options.Path ?? Directory.GetCurrentDirectory());
        EnsureWithinRepository(currentTarget, repositoryRoot, "The simulation target");
        if (!File.Exists(currentTarget) && !Directory.Exists(currentTarget))
        {
            throw new ArgumentException($"The simulation target does not exist: {currentTarget}");
        }

        var explicitCredentials = ReadExplicitCredentialEnvironment(options);
        var processRunner = new ProcessRunner();
        var worktreeInspector = new GitWorkingTreeInspector(processRunner);
        await worktreeInspector.EnsureCleanAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        await worktreeInspector.EnsureArchiveSemanticsAreReproducibleAsync(repositoryRoot, cancellationToken)
            .ConfigureAwait(false);
        var (report, exitCode) = await CreateSimulationReportAsync(
            options,
            repositoryRoot,
            currentTarget,
            explicitCredentials,
            processRunner,
            error,
            cancellationToken).ConfigureAwait(false);

        // The output file is the only intentional repository write and is produced after
        // both owned snapshots have been deleted and the original checkout revalidated.
        await worktreeInspector.EnsureCleanAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        var rendered = options.Format == OutputFormat.Json
            ? DependencySimulationSerializer.SerializeJson(report) + "\n"
            : DependencySimulationSerializer.SerializeText(report);
        if (options.OutputPath is null)
        {
            await output.WriteAsync(rendered).ConfigureAwait(false);
        }
        else
        {
            await AtomicOutputFile.WriteAsync(options.OutputPath, rendered, cancellationToken).ConfigureAwait(false);
            if (options.Verbosity != OutputVerbosity.Quiet)
            {
                await error.WriteLineAsync($"Wrote dependency simulation report to {options.OutputPath}")
                    .ConfigureAwait(false);
            }
        }

        return exitCode;
    }

    private static async Task<(DependencySimulationReport Report, int ExitCode)> CreateSimulationReportAsync(
        CliOptions options,
        string repositoryRoot,
        string currentTarget,
        IReadOnlyDictionary<string, string> explicitCredentials,
        IProcessRunner processRunner,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        GitSnapshot? ownedBaseline = null;
        GitSnapshot? ownedCandidate = null;
        try
        {
            var baselineSnapshot = ownedBaseline = await new GitSnapshotProvider(processRunner)
                    .MaterializeAsync(repositoryRoot, "HEAD", cancellationToken)
                    .ConfigureAwait(false);
            var candidateSnapshot = ownedCandidate = await new GitSnapshotProvider(processRunner)
                    .MaterializeAsync(repositoryRoot, baselineSnapshot.Commit, cancellationToken)
                    .ConfigureAwait(false);
            if (!baselineSnapshot.Commit.Equals(candidateSnapshot.Commit, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The independent simulation snapshots do not resolve to the same commit.");
            }

            var relativeTarget = Path.GetRelativePath(repositoryRoot, currentTarget);
            var baselineTarget = MapSimulationTarget(baselineSnapshot.SnapshotDirectory, relativeTarget);
            var candidateTarget = MapSimulationTarget(candidateSnapshot.SnapshotDirectory, relativeTarget);
            var baselineOptions = CreateSnapshotSimulationOptions(
                options,
                repositoryRoot,
                baselineSnapshot.SnapshotDirectory,
                baselineTarget);
            var candidateOptions = CreateSnapshotSimulationOptions(
                options,
                repositoryRoot,
                candidateSnapshot.SnapshotDirectory,
                candidateTarget);
            var baselineRuntime = CreateSimulationRuntime(
                baselineSnapshot.SnapshotDirectory,
                repositoryRoot,
                explicitCredentials,
                processRunner);
            var candidateRuntime = CreateSimulationRuntime(
                candidateSnapshot.SnapshotDirectory,
                repositoryRoot,
                explicitCredentials,
                processRunner);

            var baseline = await AnalyzeAsync(
                baselineOptions,
                error,
                cancellationToken,
                baselineSnapshot.SnapshotDirectory,
                packagesDirectory: baselineRuntime.PackagesDirectory,
                processRunnerOverride: baselineRuntime.ProcessRunner).ConfigureAwait(false);
            if (baseline.HasOperationalError)
            {
                throw new InvalidOperationException(
                    "The independent baseline restore or analysis was incomplete; no candidate mutation was attempted.");
            }

            var selectedPackages = SelectAndMapSimulationPackages(
                baseline.Result.Packages,
                baselineSnapshot.SnapshotDirectory,
                candidateSnapshot.SnapshotDirectory,
                options.SimulationPackageId!);
            var expectedSourceHash = ComputeObservedDeclarationHash(
                baseline.Result.Packages,
                baselineSnapshot.SnapshotDirectory,
                options.SimulationPackageId!);
            var edit = PackageVersionEditor.Apply(new PackageVersionEditRequest(
                candidateSnapshot.SnapshotDirectory,
                options.SimulationPackageId!,
                options.SimulationTargetVersion!,
                selectedPackages)
            {
                ExpectedSourceSha256 = expectedSourceHash,
            });
            var lockedMode = ResolveSimulationLockedMode(
                baseline.Result.ProjectSettings,
                baselineSnapshot.SnapshotDirectory,
                edit.AffectedProjects);
            var candidateDiscovery = new ProjectDiscovery().Discover(
                candidateTarget,
                candidateSnapshot.SnapshotDirectory);
            var (candidateConfiguration, _, candidateConfigurationDirectory) = LoadConfiguration(
                candidateOptions,
                candidateSnapshot.SnapshotDirectory);
            var restoreTimeout = AnalysisPolicyResolver.Resolve(
                candidateConfiguration,
                candidateConfigurationDirectory,
                new AnalysisPolicyOverrides(
                    candidateOptions.FailOn,
                    null,
                    null,
                    candidateOptions.RestoreTimeoutSeconds,
                    candidateOptions.EvaluationTimeoutSeconds)).Timeouts.Restore;
            var restoreRunner = new RestoreRunner(
                candidateRuntime.ProcessRunner,
                restoreTimeout,
                candidateOptions.MaxParallelism ?? candidateConfiguration.MaxParallelism ??
                AnalysisExecutionOptions.Default.MaxDegreeOfParallelism);
            var candidateRestore = await restoreRunner.RestoreDetailedAsync(
                candidateDiscovery,
                options.Verbosity == OutputVerbosity.Quiet ? null : message => error.WriteLine(message),
                cancellationToken,
                forceEvaluate: lockedMode == DependencySimulationLockedMode.NotEnabled,
                candidateRuntime.PackagesDirectory).ConfigureAwait(false);
            VerifySimulationMutationHash(candidateSnapshot.SnapshotDirectory, edit);

            if (!candidateRestore.Succeeded)
            {
                return CreateFailedRestoreSimulation(
                    options,
                    baselineSnapshot.Commit,
                    relativeTarget,
                    edit,
                    lockedMode,
                    candidateRestore);
            }

            var candidate = await AnalyzeAsync(
                candidateOptions with { NoRestore = true },
                error,
                cancellationToken,
                candidateSnapshot.SnapshotDirectory,
                packagesDirectory: candidateRuntime.PackagesDirectory,
                preparedRestore: candidateRestore,
                processRunnerOverride: candidateRuntime.ProcessRunner).ConfigureAwait(false);
            if (candidate.HasOperationalError)
            {
                var report = CreateUnavailableSimulationReport(
                    options,
                    baselineSnapshot.Commit,
                    relativeTarget,
                    edit,
                    lockedMode,
                    DependencySimulationVerificationStatus.Passed,
                    null,
                    DependencySimulationVerdict.Incomplete,
                    [],
                    ["The candidate restore completed, but dependency evaluation or the requested package audit was incomplete."],
                    "candidateAnalysisIncomplete");
                return (report, 2);
            }

            var comparison = AnalysisDiffComparer.Compare(
                baseline.Result,
                baselineSnapshot.SnapshotDirectory,
                candidate.Result,
                candidateSnapshot.SnapshotDirectory,
                $"time-machine:{options.SimulationPackageId}",
                baselineSnapshot.Commit,
                candidate.Context.Policy.Impact);
            if (!comparison.IsComplete)
            {
                var report = CreateUnavailableSimulationReport(
                    options,
                    baselineSnapshot.Commit,
                    relativeTarget,
                    edit,
                    lockedMode,
                    DependencySimulationVerificationStatus.Passed,
                    null,
                    DependencySimulationVerdict.Incomplete,
                    [],
                    ["The dependency comparison was incomplete."],
                    "comparisonIncomplete");
                return (report, 2);
            }

            var resolutionError = ValidateCandidateResolution(
                baseline.Result,
                baselineSnapshot.SnapshotDirectory,
                candidate.Result,
                candidateSnapshot.SnapshotDirectory,
                options.SimulationPackageId!,
                options.SimulationTargetVersion!);
            var rejectionReasons = EvaluateSimulationRejectionReasons(
                comparison,
                candidate,
                candidateSnapshot.SnapshotDirectory,
                resolutionError);
            var noChange = HasNoObservedSimulationImpact(comparison);
            var verdict = rejectionReasons.Count > 0
                ? DependencySimulationVerdict.Reject
                : noChange
                    ? DependencySimulationVerdict.NoChange
                    : DependencySimulationVerdict.Pass;
            var completedReport = new DependencySimulationReport(
                DependencySimulationReport.CurrentSchemaVersion,
                PackageMedicAnalyzer.Version,
                new DependencySimulationRepository(
                    baselineSnapshot.Commit,
                    ToPortableSimulationPath(relativeTarget),
                    WorkingTreeRequiredClean: true),
                new DependencySimulationRequest(options.SimulationPackageId!, options.SimulationTargetVersion!),
                DependencySimulationMutation.From(edit),
                DependencySimulationVerification.RestoreOnly(
                    DependencySimulationVerificationStatus.Passed,
                    options.AuditVulnerabilities,
                    options.AuditDeprecatedPackages,
                    lockedMode),
                CreateSimulationComparison(comparison),
                verdict,
                rejectionReasons,
                []);
            return (completedReport, verdict == DependencySimulationVerdict.Reject ? 1 : 0);
        }
        catch (Exception operationError)
        {
            var cleanupErrors = DisposeSimulationSnapshots(ownedCandidate, ownedBaseline);
            ownedCandidate = null;
            ownedBaseline = null;
            if (cleanupErrors.Count > 0)
            {
                throw new AggregateException(
                    "Dependency simulation failed and one or more owned snapshots could not be cleaned up.",
                    [operationError, .. cleanupErrors]);
            }

            throw;
        }
        finally
        {
            if (ownedCandidate is not null || ownedBaseline is not null)
            {
                var cleanupErrors = DisposeSimulationSnapshots(ownedCandidate, ownedBaseline);
                if (cleanupErrors.Count > 0)
                {
                    throw new AggregateException(
                        "Dependency simulation completed, but one or more owned snapshots could not be cleaned up.",
                        cleanupErrors);
                }
            }
        }
    }

    internal static bool HasNoObservedSimulationImpact(AnalysisDiffReport comparison)
    {
        if (comparison.Changes.Count != 0 || comparison.ProjectSettingsChanges.Count != 0)
        {
            return false;
        }

        if (comparison.RiskSummary.VulnerabilitiesIntroduced > 0 ||
            comparison.RiskSummary.VulnerabilitiesResolved > 0 ||
            comparison.RiskSummary.DeprecationsIntroduced > 0 ||
            comparison.RiskSummary.DeprecationsResolved > 0)
        {
            return false;
        }

        return comparison.PackageChanges.All(change =>
            change.Before is not null &&
            change.After is not null &&
            change.Before.ResolvedVersion.Equals(change.After.ResolvedVersion, StringComparison.OrdinalIgnoreCase) &&
            change.Before.DependencyKind == change.After.DependencyKind &&
            string.Equals(change.Before.PackageSource, change.After.PackageSource, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(change.Before.ContentHash, change.After.ContentHash, StringComparison.Ordinal) &&
            change.Before.SignaturePresent == change.After.SignaturePresent);
    }

    private static IReadOnlyList<Exception> DisposeSimulationSnapshots(
        GitSnapshot? candidate,
        GitSnapshot? baseline)
    {
        var errors = new List<Exception>();
        foreach (var snapshot in new[] { candidate, baseline })
        {
            if (snapshot is null)
            {
                continue;
            }

            try
            {
                snapshot.Dispose();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }

        return errors;
    }

    private static IReadOnlyDictionary<string, string> ReadExplicitCredentialEnvironment(CliOptions options)
    {
        var values = new Dictionary<string, string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        foreach (var name in options.SimulationCredentialEnvironmentVariables ?? [])
        {
            ProcessEnvironment.ValidateVariableName(name);
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value))
            {
                throw new UsageException(
                    $"Credential environment variable '{name}' is not defined or is empty in the current process.");
            }

            values.Add(name, value);
        }

        return values;
    }

    private static string MapSimulationTarget(string snapshotRoot, string relativeTarget)
    {
        var target = Path.GetFullPath(Path.Combine(snapshotRoot, relativeTarget));
        EnsureWithinRepository(target, snapshotRoot, "The snapshot simulation target");
        if (!File.Exists(target) && !Directory.Exists(target))
        {
            throw new InvalidOperationException(
                "The selected simulation target is not a tracked file or directory in the current HEAD commit.");
        }

        if (!ProjectDiscovery.IsSafelyContained(snapshotRoot, target))
        {
            throw new InvalidOperationException("The snapshot simulation target crosses a symbolic-link boundary.");
        }

        return target;
    }

    private static CliOptions CreateSnapshotSimulationOptions(
        CliOptions options,
        string repositoryRoot,
        string snapshotRoot,
        string snapshotTarget) => options with
        {
            Path = snapshotTarget,
            OutputPath = null,
            SarifOutputPath = null,
            BaselinePath = null,
            FailOnNew = null,
            GitReference = null,
            ConfigurationPath = MapSimulationConfiguration(
            options.ConfigurationPath,
            repositoryRoot,
            snapshotRoot),
        };

    private static SimulationRuntime CreateSimulationRuntime(
        string snapshotRoot,
        string repositoryRoot,
        IReadOnlyDictionary<string, string> explicitCredentials,
        IProcessRunner processRunner)
    {
        var cacheRoot = Path.Combine(snapshotRoot, ".packagemedic-time-machine");
        if (File.Exists(cacheRoot) || Directory.Exists(cacheRoot))
        {
            throw new InvalidOperationException(
                "The repository already contains the reserved '.packagemedic-time-machine' path.");
        }

        var packagesDirectory = Path.Combine(cacheRoot, "nuget", "packages");
        var environment = ProcessEnvironment.CreateIsolatedDotNet(
            cacheRoot,
            packagesDirectory,
            explicitCredentials,
            explicitCredentials.Keys.ToArray(),
            [repositoryRoot, snapshotRoot]);
        return new SimulationRuntime(
            new EnvironmentScopedProcessRunner(processRunner, environment),
            packagesDirectory);
    }

    private static SimulationRuntime CreateDiffRuntime(
        string runtimeRoot,
        IReadOnlyList<string> untrustedRoots,
        IProcessRunner processRunner)
    {
        var packagesDirectory = Directory.CreateDirectory(Path.Combine(runtimeRoot, "nuget", "packages")).FullName;
        var httpCache = Directory.CreateDirectory(Path.Combine(runtimeRoot, "nuget", "http-cache")).FullName;
        var pluginCache = Directory.CreateDirectory(Path.Combine(runtimeRoot, "nuget", "plugins-cache")).FullName;
        var dotnetHome = Directory.CreateDirectory(Path.Combine(runtimeRoot, "dotnet-home")).FullName;
        var temporary = Directory.CreateDirectory(Path.Combine(runtimeRoot, "temp")).FullName;
        var environment = ProcessEnvironment.CreateOverrides(
            new Dictionary<string, string?>(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            {
                ["NUGET_PACKAGES"] = packagesDirectory,
                ["NUGET_HTTP_CACHE_PATH"] = httpCache,
                ["NUGET_PLUGINS_CACHE_PATH"] = pluginCache,
                ["DOTNET_CLI_HOME"] = dotnetHome,
                ["TEMP"] = temporary,
                ["TMP"] = temporary,
                ["TMPDIR"] = temporary,
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
                ["DOTNET_NOLOGO"] = "1",
                ["MSBUILDDISABLENODEREUSE"] = "1",
                ["NUGET_XMLDOC_MODE"] = "skip",
            },
            untrustedExecutableRoots: untrustedRoots);
        return new SimulationRuntime(
            new EnvironmentScopedProcessRunner(processRunner, environment),
            packagesDirectory);
    }

    private static IReadOnlyList<PackageInventoryItem> SelectAndMapSimulationPackages(
        IReadOnlyList<PackageInventoryItem> packages,
        string baselineRoot,
        string candidateRoot,
        string packageId) => packages
        .Where(item => item.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase))
        .Select(item => item with
        {
            Project = MapSnapshotFile(item.Project, baselineRoot, candidateRoot, "An affected project"),
            SourceFile = item.SourceFile is null
                ? null
                : MapSnapshotFile(item.SourceFile, baselineRoot, candidateRoot, "The package declaration"),
        })
        .ToArray();

    private static string MapSnapshotFile(
        string value,
        string sourceRoot,
        string destinationRoot,
        string description)
    {
        var source = Path.GetFullPath(value, sourceRoot);
        if (!File.Exists(source) || !ProjectDiscovery.IsSafelyContained(sourceRoot, source))
        {
            throw new InvalidOperationException($"{description} is not a regular contained snapshot file.");
        }

        var destination = Path.GetFullPath(Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, source)));
        if (!File.Exists(destination) || !ProjectDiscovery.IsSafelyContained(destinationRoot, destination))
        {
            throw new InvalidOperationException($"{description} is missing from the independent candidate snapshot.");
        }

        return destination;
    }

    private static string ComputeObservedDeclarationHash(
        IReadOnlyList<PackageInventoryItem> packages,
        string baselineRoot,
        string packageId)
    {
        var matchingPackages = packages
            .Where(item => item.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matchingPackages.Length == 0)
        {
            throw new InvalidOperationException(
                $"Simulation refused: package '{packageId}' was not found in the selected dependency graph.");
        }

        if (matchingPackages.All(item => item.DependencyKind == PackageDependencyKind.Transitive))
        {
            throw new InvalidOperationException(
                $"Simulation refused: package '{packageId}' is transitive-only and has no direct declaration to mutate.");
        }

        var matches = matchingPackages
            .Where(item => item.DependencyKind == PackageDependencyKind.Direct)
            .Where(item => item.SourceFile is not null && item.SourceLine is > 0)
            .GroupBy(
                item => string.Join(
                    '\n',
                    Path.GetFullPath(item.SourceFile!, baselineRoot),
                    item.SourceLine,
                    item.VersionSource,
                    item.RequestedVersion),
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (matches.Length != 1)
        {
            var candidates = packages
                .Where(item => item.DependencyKind == PackageDependencyKind.Direct &&
                               item.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase))
                .Take(10)
                .Select(item => item.SourceFile is null
                    ? $"- implicit source for {item.Framework}"
                    : $"- {ToPortableDisplayPath(item.SourceFile, baselineRoot)}:{item.SourceLine} for {item.Framework} via {item.VersionSource}");
            var detail = string.Join(Environment.NewLine, candidates);
            throw new InvalidOperationException(
                $"Simulation refused: package '{packageId}' does not have exactly one effective declaration in the selected scope." +
                (detail.Length == 0 ? string.Empty : Environment.NewLine + detail) +
                Environment.NewLine + "Specify a narrower project or solution path, or resolve the ambiguity first.");
        }

        var sourceFile = Path.GetFullPath(matches[0].SourceFile!, baselineRoot);
        if (!File.Exists(sourceFile) || !ProjectDiscovery.IsSafelyContained(baselineRoot, sourceFile))
        {
            throw new InvalidOperationException("The observed package declaration is outside the immutable baseline snapshot.");
        }

        if (new FileInfo(sourceFile).Length > PackageVersionEditor.MaximumMutationXmlBytes)
        {
            throw new InvalidDataException(
                $"The package declaration exceeds the {PackageVersionEditor.MaximumMutationXmlBytes}-byte simulation limit.");
        }

        using var stream = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void VerifySimulationMutationHash(
        string snapshotRoot,
        PackageVersionEditResult edit)
    {
        var sourceFile = Path.GetFullPath(Path.Combine(snapshotRoot, edit.File));
        if (!File.Exists(sourceFile) ||
            !ProjectDiscovery.IsSafelyContained(snapshotRoot, sourceFile) ||
            new FileInfo(sourceFile).Length > PackageVersionEditor.MaximumMutationXmlBytes)
        {
            throw new InvalidOperationException(
                "The candidate package declaration changed its trusted filesystem boundary during restore.");
        }

        using var stream = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        var observed = SHA256.HashData(stream);
        if (!CryptographicOperations.FixedTimeEquals(
                observed,
                Convert.FromHexString(edit.SourceSha256After)))
        {
            throw new InvalidOperationException(
                "The candidate package declaration was modified during restore; the simulation is incomplete.");
        }
    }

    private static (DependencySimulationReport Report, int ExitCode) CreateFailedRestoreSimulation(
        CliOptions options,
        string commit,
        string relativeTarget,
        PackageVersionEditResult edit,
        DependencySimulationLockedMode lockedMode,
        RestoreExecutionResult restore)
    {
        var failureKind = ClassifySimulationRestoreFailure(restore, lockedMode);
        if (restore.FailureKind == RestoreProcessFailureKind.Rejected &&
            IsDeterministicCandidateRejection(failureKind))
        {
            var reason = DescribeSimulationRestoreRejection(failureKind, restore.Diagnostics);
            return (
                CreateUnavailableSimulationReport(
                    options,
                    commit,
                    relativeTarget,
                    edit,
                    lockedMode,
                    DependencySimulationVerificationStatus.Failed,
                    failureKind,
                    DependencySimulationVerdict.Reject,
                    [reason],
                    [],
                    "candidateRestoreFailed"),
                1);
        }

        var error = restore.FailureKind switch
        {
            RestoreProcessFailureKind.TimedOut => "The candidate restore exceeded the configured timeout.",
            RestoreProcessFailureKind.OutputLimitExceeded => "The candidate restore exceeded the subprocess output safety limit.",
            _ => failureKind switch
            {
                DependencySimulationRestoreFailureKind.AuthenticationFailed =>
                    "The candidate restore could not authenticate to a configured package source.",
                DependencySimulationRestoreFailureKind.SourceUnavailable =>
                    "A configured package source was unavailable during candidate restore.",
                _ => "The candidate restore could not be evaluated because of an operational failure.",
            },
        };
        return (
            CreateUnavailableSimulationReport(
                options,
                commit,
                relativeTarget,
                edit,
                lockedMode,
                DependencySimulationVerificationStatus.Failed,
                failureKind,
                DependencySimulationVerdict.Incomplete,
                [],
                [error],
                "candidateRestoreIncomplete"),
            2);
    }

    private static DependencySimulationReport CreateUnavailableSimulationReport(
        CliOptions options,
        string commit,
        string relativeTarget,
        PackageVersionEditResult edit,
        DependencySimulationLockedMode lockedMode,
        DependencySimulationVerificationStatus restoreStatus,
        DependencySimulationRestoreFailureKind? restoreFailureKind,
        DependencySimulationVerdict verdict,
        IReadOnlyList<string> rejectionReasons,
        IReadOnlyList<string> errors,
        string unavailableReason) => new(
            DependencySimulationReport.CurrentSchemaVersion,
            PackageMedicAnalyzer.Version,
            new DependencySimulationRepository(
                commit,
                ToPortableSimulationPath(relativeTarget),
                WorkingTreeRequiredClean: true),
            new DependencySimulationRequest(options.SimulationPackageId!, options.SimulationTargetVersion!),
            DependencySimulationMutation.From(edit),
            DependencySimulationVerification.RestoreOnly(
                restoreStatus,
                auditedVulnerabilities: false,
                auditedDeprecations: false,
                lockedMode,
                restoreFailureKind),
            EmptySimulationComparison(unavailableReason),
            verdict,
            rejectionReasons,
            errors);

    private static DependencySimulationComparison EmptySimulationComparison(string reason) => new(
        new AnalysisDiffSummary(0, 0, 0),
        [],
        new PackageDiffSummary(0, 0, 0, 0, 0, 0, 0, 0),
        [],
        new DependencyRiskDiffSummary(0, 0, 0, 0),
        [],
        null)
    {
        IsComplete = false,
        UnavailableReason = reason,
    };

    internal static DependencySimulationLockedMode ResolveSimulationLockedMode(
        IReadOnlyList<ProjectPackageSettings> projectSettings,
        string snapshotRoot,
        IReadOnlyList<string> affectedProjects)
    {
        var affected = affectedProjects
            .Select(ToPortableSimulationPath)
            .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var matched = projectSettings
            .Where(item => affected.Contains(ToPortableSimulationPath(
                Path.GetRelativePath(snapshotRoot, item.Project))))
            .Select(item => item.RestoreLockedMode)
            .ToArray();
        if (matched.Length != affected.Count)
        {
            throw new InvalidOperationException(
                "Could not determine locked-restore state for every project affected by the candidate.");
        }

        return matched.All(value => value)
            ? DependencySimulationLockedMode.Enforced
            : matched.Any(value => value)
                ? DependencySimulationLockedMode.Mixed
                : DependencySimulationLockedMode.NotEnabled;
    }

    internal static DependencySimulationRestoreFailureKind ClassifySimulationRestoreFailure(
        RestoreExecutionResult restore,
        DependencySimulationLockedMode lockedMode)
    {
        if (restore.FailureKind == RestoreProcessFailureKind.TimedOut)
        {
            return DependencySimulationRestoreFailureKind.TimedOut;
        }

        if (restore.FailureKind == RestoreProcessFailureKind.OutputLimitExceeded)
        {
            return DependencySimulationRestoreFailureKind.OutputLimitExceeded;
        }

        if (restore.Errors.Any(item =>
                item.Contains("401", StringComparison.OrdinalIgnoreCase) ||
                item.Contains("403", StringComparison.OrdinalIgnoreCase) ||
                item.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
                item.Contains("forbidden", StringComparison.OrdinalIgnoreCase)))
            return DependencySimulationRestoreFailureKind.AuthenticationFailed;

        var codes = restore.Diagnostics
            .Select(item => item.OriginalCode)
            .Where(item => item is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (codes.Contains("NU1300") || codes.Contains("NU1301"))
            return DependencySimulationRestoreFailureKind.SourceUnavailable;
        if (lockedMode is DependencySimulationLockedMode.Enforced or DependencySimulationLockedMode.Mixed &&
            (codes.Contains("NU1004") || codes.Contains("NU1005")))
        {
            return DependencySimulationRestoreFailureKind.LockedModeConflict;
        }

        if (codes.Contains("NU1101")) return DependencySimulationRestoreFailureKind.PackageNotFound;
        if (codes.Contains("NU1102")) return DependencySimulationRestoreFailureKind.VersionNotFound;
        if (codes.Contains("NU1106") || codes.Contains("NU1107") || codes.Contains("NU1605"))
            return DependencySimulationRestoreFailureKind.DependencyConflict;
        return DependencySimulationRestoreFailureKind.Unknown;
    }

    internal static bool IsDeterministicCandidateRejection(
        DependencySimulationRestoreFailureKind failureKind) => failureKind is
        DependencySimulationRestoreFailureKind.PackageNotFound or
        DependencySimulationRestoreFailureKind.VersionNotFound or
        DependencySimulationRestoreFailureKind.DependencyConflict or
        DependencySimulationRestoreFailureKind.LockedModeConflict;

    private static string DescribeSimulationRestoreRejection(
        DependencySimulationRestoreFailureKind failureKind,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        var code = diagnostics.Select(item => item.OriginalCode)
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
        var suffix = code is null ? string.Empty : $" ({code})";
        return failureKind switch
        {
            DependencySimulationRestoreFailureKind.LockedModeConflict =>
                "The candidate requires regenerating the NuGet lock file and is not applicable while locked mode is enforced." + suffix,
            DependencySimulationRestoreFailureKind.PackageNotFound =>
                "The candidate package was not found in the configured package sources." + suffix,
            DependencySimulationRestoreFailureKind.VersionNotFound =>
                "The requested candidate version was not found in the configured package sources." + suffix,
            DependencySimulationRestoreFailureKind.DependencyConflict =>
                "The candidate produced a NuGet dependency conflict." + suffix,
            DependencySimulationRestoreFailureKind.AuthenticationFailed =>
                "The candidate restore could not authenticate to a configured package source." + suffix,
            DependencySimulationRestoreFailureKind.SourceUnavailable =>
                "A configured package source was unavailable during candidate restore." + suffix,
            _ => "The candidate restore was rejected by NuGet in this environment." + suffix,
        };
    }

    private static IReadOnlyList<string> EvaluateSimulationRejectionReasons(
        AnalysisDiffReport comparison,
        PreparedAnalysis candidate,
        string candidateRoot,
        string? resolutionError)
    {
        var reasons = new List<string>();
        if (resolutionError is not null)
        {
            reasons.Add(resolutionError);
        }

        if (comparison.Impact is { GatePassed: false } impact)
        {
            reasons.AddRange(impact.Violations.Select(item => $"{item.Code}: {item.Message}"));
        }

        var gateFingerprints = comparison.Changes
            .Where(change => change.Kind == DiagnosticChangeKind.Added ||
                             change.Kind == DiagnosticChangeKind.SeverityChanged &&
                             change.After!.Severity > change.Before!.Severity)
            .Select(change => change.Fingerprint)
            .ToHashSet(StringComparer.Ordinal);
        var introduced = AnalysisDiffComparer.SelectDiagnosticsByFingerprint(
            candidate.Result,
            candidateRoot,
            gateFingerprints);
        if (ReachesThreshold(introduced, candidate.Context.Policy.FailOn))
        {
            reasons.AddRange(introduced
                .Where(item => candidate.Context.Policy.FailOn switch
                {
                    PolicyFailureLevel.Warning => item.Severity >= DiagnosticSeverity.Warning,
                    PolicyFailureLevel.Error => item.Severity >= DiagnosticSeverity.Error,
                    _ => false,
                })
                .Select(item => $"{item.Code}: the candidate introduces {item.Title}."));
        }

        return reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static DependencySimulationComparison CreateSimulationComparison(AnalysisDiffReport comparison) => new(
        comparison.Summary,
        comparison.Changes,
        comparison.PackageSummary,
        comparison.PackageChanges,
        comparison.RiskSummary,
        comparison.ProjectSettingsChanges,
        comparison.Impact)
    {
        IsComplete = comparison.IsComplete,
        UnavailableReason = comparison.IsComplete ? null : "comparisonIncomplete",
    };

    private static string ToPortableSimulationPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        return string.IsNullOrWhiteSpace(normalized) ? "." : normalized;
    }

    private static string? ValidateCandidateResolution(
        AnalysisResult baseline,
        string baselineRoot,
        AnalysisResult candidate,
        string candidateRoot,
        string packageId,
        string candidateVersion)
    {
        var expected = baseline.Packages
            .Where(item => item.DependencyKind == PackageDependencyKind.Direct &&
                           item.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase))
            .Select(item => PackageContext(item, baselineRoot))
            .ToHashSet(StringComparer.Ordinal);
        var resolved = candidate.Packages
            .Where(item => item.DependencyKind == PackageDependencyKind.Direct &&
                           item.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase) &&
                           AnalysisDiffComparer.TryCompareResolvedVersions(
                               item.ResolvedVersion,
                               candidateVersion,
                               out var comparison) &&
                           comparison == 0)
            .Select(item => PackageContext(item, candidateRoot))
            .ToHashSet(StringComparer.Ordinal);
        return expected.Count > 0 && expected.SetEquals(resolved)
            ? null
            : $"The candidate did not resolve exact version '{candidateVersion}' in every selected project/framework/runtime context.";
    }

    private sealed record SimulationRuntime(IProcessRunner ProcessRunner, string PackagesDirectory);

    private static async Task WriteAnalysisReportsAsync(
        PreparedAnalysis prepared,
        CliOptions options,
        TextWriter output,
        TextWriter error,
        AnalysisDiffReport? diff,
        CancellationToken cancellationToken)
    {
        if (options.SarifOutputPath is not null)
        {
            await WriteSarifFileAsync(options.SarifOutputPath, prepared, cancellationToken).ConfigureAwait(false);
            if (options.Verbosity != OutputVerbosity.Quiet)
            {
                await error.WriteLineAsync($"Wrote sarif report to {options.SarifOutputPath}").ConfigureAwait(false);
            }
        }

        if (options.OutputPath is null)
        {
            var rendered = options.Format switch
            {
                OutputFormat.Json => ResultJsonSerializer.Serialize(prepared.Result, prepared.Context, diff) + "\n",
                OutputFormat.Sarif => RenderSarif(prepared),
                _ when diff is not null => AnalysisDiffSerializer.SerializeText(diff),
                _ => await RenderResultAsync(prepared, options).ConfigureAwait(false),
            };
            await output.WriteAsync(rendered).ConfigureAwait(false);
        }
        else
        {
            if (options.Format == OutputFormat.Json)
            {
                await WriteJsonFileAsync(options.OutputPath, prepared, diff, cancellationToken).ConfigureAwait(false);
            }
            else if (options.Format == OutputFormat.Sarif)
            {
                await WriteSarifFileAsync(options.OutputPath, prepared, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var rendered = diff is null
                    ? await RenderResultAsync(prepared, options).ConfigureAwait(false)
                    : AnalysisDiffSerializer.SerializeText(diff);
                await AtomicOutputFile.WriteAsync(options.OutputPath, rendered, cancellationToken).ConfigureAwait(false);
            }

            if (options.Verbosity != OutputVerbosity.Quiet)
            {
                await error.WriteLineAsync($"Wrote {options.Format.ToString().ToLowerInvariant()} report to {options.OutputPath}").ConfigureAwait(false);
            }
        }
    }

    private static Task WriteJsonFileAsync(
        string path,
        PreparedAnalysis prepared,
        AnalysisDiffReport? diff,
        CancellationToken cancellationToken) => AtomicOutputFile.WriteAsync(
        path,
        async (stream, token) =>
        {
            await ResultJsonSerializer.SerializeAsync(
                stream,
                prepared.Result,
                prepared.Context,
                diff,
                token).ConfigureAwait(false);
            await stream.WriteAsync(NewLineUtf8, token).ConfigureAwait(false);
        },
        cancellationToken);

    private static Task WriteSarifFileAsync(
        string path,
        PreparedAnalysis prepared,
        CancellationToken cancellationToken) => AtomicOutputFile.WriteAsync(
        path,
        async (stream, token) =>
        {
            await SarifResultSerializer.SerializeAsync(
                stream,
                prepared.Result,
                prepared.Context.RepositoryRoot,
                prepared.Context.Baseline,
                token).ConfigureAwait(false);
            await stream.WriteAsync(NewLineUtf8, token).ConfigureAwait(false);
        },
        cancellationToken);

    private static async Task<int> WriteBaselineAsync(
        CliOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        string? createDestination = null;
        if (options.Command == CliCommand.BaselineCreate)
        {
            createDestination = Path.GetFullPath(options.OutputPath!);
            if (File.Exists(createDestination) && !options.Force)
            {
                throw new InvalidOperationException($"Baseline '{createDestination}' already exists; use --force to replace it.");
            }
        }

        var prepared = await AnalyzeAsync(options, error, cancellationToken).ConfigureAwait(false);
        if (prepared.HasOperationalError)
        {
            return 2;
        }

        PackageMedicBaseline baseline;
        string destination;
        if (options.Command == CliCommand.BaselineCreate)
        {
            destination = createDestination!;
            baseline = BaselineSerializer.Create(prepared.Result, prepared.Context.RepositoryRoot);
        }
        else
        {
            var source = options.BaselinePath is not null
                ? Path.GetFullPath(options.BaselinePath)
                : prepared.Context.Policy.BaselinePath;
            if (source is null)
            {
                throw new InvalidOperationException("'baseline update' requires --baseline or a configured baseline.");
            }

            var previous = BaselineSerializer.Load(source);
            baseline = BaselineSerializer.Update(previous, prepared.Result, prepared.Context.RepositoryRoot);
            destination = Path.GetFullPath(options.OutputPath ?? source);
        }

        await AtomicOutputFile.WriteAsync(
            destination,
            BaselineSerializer.Serialize(baseline) + "\n",
            cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(
            $"Wrote baseline with {baseline.Entries.Count} diagnostics to {destination}").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> WriteCleanPlanAsync(
        CliOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var prepared = await AnalyzeAsync(options, error, cancellationToken).ConfigureAwait(false);
        if (prepared.HasOperationalError)
        {
            return 2;
        }

        var candidates = prepared.Result.Diagnostics.Where(item => item.Code == "PM001").ToArray();
        await output.WriteLineAsync("PackageMedic clean --dry-run").ConfigureAwait(false);
        await output.WriteLineAsync("No dependency files were modified.").ConfigureAwait(false);
        if (candidates.Length == 0)
        {
            await output.WriteLineAsync("No safe unused central-version candidates were found.").ConfigureAwait(false);
            return 0;
        }

        foreach (var diagnostic in candidates)
        {
            var location = diagnostic.File is null
                ? "unknown location"
                : $"{diagnostic.File}{(diagnostic.Line is null ? string.Empty : $":{diagnostic.Line}")}";
            await output.WriteLineAsync($"Would review/remove: {diagnostic.Evidence} ({location})").ConfigureAwait(false);
        }

        await output.WriteLineAsync($"Plan: {candidates.Length} candidate(s); apply is intentionally unavailable in 0.5.").ConfigureAwait(false);
        return 0;
    }

    private static async Task<PreparedAnalysis> AnalyzeAsync(
        CliOptions options,
        TextWriter error,
        CancellationToken cancellationToken,
        string? repositoryRootOverride = null,
        bool forceEvaluateRestore = false,
        string? packagesDirectory = null,
        RestoreExecutionResult? preparedRestore = null,
        IProcessRunner? processRunnerOverride = null)
    {
        var repositoryRoot = repositoryRootOverride ?? FindRepositoryRoot(options.Path);
        var (configuration, configurationPath, configurationDirectory) = LoadConfiguration(options, repositoryRoot);
        var baselineOverride = options.BaselinePath is null ? null : Path.GetFullPath(options.BaselinePath);
        var policy = AnalysisPolicyResolver.Resolve(
            configuration,
            configurationDirectory,
            new AnalysisPolicyOverrides(
                options.FailOn,
                options.FailOnNew,
                baselineOverride,
                options.RestoreTimeoutSeconds,
                options.EvaluationTimeoutSeconds));
        var executionOptions = new AnalysisExecutionOptions(
            policy.Timeouts.Restore,
            policy.Timeouts.Evaluation,
            options.MaxParallelism ?? configuration.MaxParallelism);
        var processRunner = processRunnerOverride ?? new EnvironmentScopedProcessRunner(
            new ProcessRunner(),
            ProcessEnvironment.CreateOverrides(
                new Dictionary<string, string?>(),
                untrustedExecutableRoots: [repositoryRoot]));
        var analyzer = new PackageMedicAnalyzer(processRunner, executionOptions);
        Action<string>? progress = options.Verbosity == OutputVerbosity.Quiet
            ? null
            : message => error.WriteLine(message);
        var discovered = new ProjectDiscovery().Discover(options.Path, repositoryRoot);
        var outcome = await analyzer.AnalyzeAsync(
            discovered,
            options.NoRestore,
            progress,
            cancellationToken,
            repositoryRoot,
            forceEvaluateRestore,
            packagesDirectory,
            preparedRestore).ConfigureAwait(false);

        if (options.AuditVulnerabilities)
        {
            var audit = await new VulnerabilityAuditRunner(
                    processRunner,
                    policy.Timeouts.Restore,
                    executionOptions.MaxDegreeOfParallelism)
                .AuditAsync(
                    discovered,
                    options.IncludeTransitive,
                    progress,
                    cancellationToken,
                    outcome.Result.Packages)
                .ConfigureAwait(false);
            var restoreDiagnostics = VulnerabilityAuditParser.CoalesceRestoreDiagnostics(
                outcome.Result.Diagnostics,
                audit.Vulnerabilities);
            var auditDiagnostics = MergeDiagnostics(restoreDiagnostics, audit.Diagnostics);
            var auditErrors = outcome.Result.AnalysisErrors.Concat(audit.Errors)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            outcome = new AnalysisOutcome(
                outcome.Result with
                {
                    Diagnostics = auditDiagnostics,
                    Summary = Recount(outcome.Result.Summary, auditDiagnostics),
                    AnalysisErrors = auditErrors,
                    Vulnerabilities = audit.Vulnerabilities,
                },
                outcome.HasOperationalError || audit.HasOperationalError);
        }

        if (options.AuditDeprecatedPackages)
        {
            var audit = await new DeprecationAuditRunner(
                    processRunner,
                    policy.Timeouts.Restore,
                    executionOptions.MaxDegreeOfParallelism)
                .AuditAsync(
                    discovered,
                    options.IncludeTransitiveDeprecatedPackages,
                    outcome.Result.Packages,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            var auditDiagnostics = MergeDiagnostics(outcome.Result.Diagnostics, audit.Diagnostics);
            var auditErrors = outcome.Result.AnalysisErrors.Concat(audit.Errors)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            outcome = new AnalysisOutcome(
                outcome.Result with
                {
                    Diagnostics = auditDiagnostics,
                    Summary = Recount(outcome.Result.Summary, auditDiagnostics),
                    AnalysisErrors = auditErrors,
                    DeprecatedPackages = audit.DeprecatedPackages,
                },
                outcome.HasOperationalError || audit.HasOperationalError);
        }

        repositoryRoot = repositoryRootOverride ?? FindRepositoryRoot(outcome.Result.Target);
        var application = policy.Apply(outcome.Result.Diagnostics, repositoryRoot);
        var result = outcome.Result with
        {
            Diagnostics = application.Diagnostics,
            Summary = Recount(outcome.Result.Summary, application.Diagnostics),
        };
        var baseline = policy.BaselinePath is null
            ? new PackageMedicBaseline(PackageMedicBaseline.CurrentSchemaVersion, result.Version, [])
            : BaselineSerializer.Load(policy.BaselinePath);
        var comparison = BaselineMatcher.Compare(result, baseline, repositoryRoot);
        var context = new AnalysisReportContext(
            repositoryRoot,
            ToPortableDisplayPath(configurationPath, repositoryRoot),
            policy,
            application,
            comparison,
            ToPortableDisplayPath(policy.BaselinePath, repositoryRoot));
        return new PreparedAnalysis(result, outcome.HasOperationalError, context);
    }

    private static Diagnostic[] MergeDiagnostics(
        IEnumerable<Diagnostic> existing,
        IEnumerable<Diagnostic> additional) => existing.Concat(additional)
        .DistinctBy(item => $"{item.Code}|{item.OriginalCode}|{item.Project}|{item.File}|{item.Line}|{item.Evidence}", StringComparer.Ordinal)
        .OrderByDescending(item => item.Severity)
        .ThenBy(item => item.Code, StringComparer.Ordinal)
        .ThenBy(item => item.Project, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.File, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.Line)
        .ThenBy(item => item.Evidence, StringComparer.Ordinal)
        .ToArray();

    private static string? MapSimulationConfiguration(
        string? configurationPath,
        string repositoryRoot,
        string snapshotRoot)
    {
        if (configurationPath is null)
        {
            return null;
        }

        var source = Path.GetFullPath(configurationPath);
        EnsureWithinRepository(source, repositoryRoot, "The simulation configuration");
        var mapped = Path.GetFullPath(Path.Combine(snapshotRoot, Path.GetRelativePath(repositoryRoot, source)));
        EnsureWithinRepository(mapped, snapshotRoot, "The snapshot simulation configuration");
        if (!File.Exists(mapped))
        {
            throw new InvalidOperationException(
                "The explicit simulation configuration must be a tracked file in the current HEAD commit.");
        }

        return mapped;
    }

    private static string PackageContext(PackageInventoryItem item, string repositoryRoot) => string.Join(
        '\n',
        DiagnosticFingerprint.GetRelativePath(item.Project, repositoryRoot) ?? Path.GetFileName(item.Project),
        item.Framework.ToUpperInvariant(),
        item.RuntimeIdentifier?.ToUpperInvariant() ?? string.Empty,
        item.Id.ToUpperInvariant());

    private static (PackageMedicConfiguration Configuration, string? Path, string Directory) LoadConfiguration(
        CliOptions options,
        string repositoryRoot)
    {
        if (options.NoConfiguration)
        {
            return (PackageMedicConfiguration.Default, null, repositoryRoot);
        }

        var configurationPath = options.ConfigurationPath is null
            ? FindAutomaticConfiguration(options.Path, repositoryRoot)
            : Path.GetFullPath(options.ConfigurationPath);
        if (configurationPath is null)
        {
            return (PackageMedicConfiguration.Default, null, repositoryRoot);
        }

        return (
            PackageMedicConfigurationLoader.Load(configurationPath),
            configurationPath,
            Path.GetDirectoryName(configurationPath) ?? repositoryRoot);
    }

    private static string? FindAutomaticConfiguration(string? requestedPath, string repositoryRoot)
    {
        var fullPath = Path.GetFullPath(requestedPath ?? Directory.GetCurrentDirectory());
        var start = File.Exists(fullPath) ? Path.GetDirectoryName(fullPath)! : fullPath;
        if (!Directory.Exists(start))
        {
            start = Path.GetDirectoryName(start) ?? repositoryRoot;
        }

        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".packagemedic.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (PathsEqual(directory.FullName, repositoryRoot))
            {
                break;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static async Task<int> InitializeConfigurationAsync(
        CliOptions options,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var requested = Path.GetFullPath(options.Path ?? Directory.GetCurrentDirectory());
        var destination = Directory.Exists(requested)
            ? Path.Combine(requested, ".packagemedic.json")
            : File.Exists(requested) || Path.GetExtension(requested).Equals(".json", StringComparison.OrdinalIgnoreCase)
                ? requested
                : Path.Combine(requested, ".packagemedic.json");
        if (File.Exists(destination) && !options.Force)
        {
            throw new InvalidOperationException($"Configuration '{destination}' already exists; use --force to replace it.");
        }

        await AtomicOutputFile.WriteAsync(destination, DefaultConfiguration, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"Created {destination}").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> WriteVersionAsync(TextWriter output)
    {
        await output.WriteLineAsync(PackageMedicAnalyzer.Version).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> WriteRulesAsync(TextWriter output)
    {
        await output.WriteLineAsync("Code   Severity     Rule").ConfigureAwait(false);
        foreach (var rule in DiagnosticRuleCatalog.All.OrderBy(item => item.Code, StringComparer.Ordinal))
        {
            await output.WriteLineAsync(
                $"{rule.Code,-6} {rule.DefaultSeverity.ToString().ToLowerInvariant(),-12} {rule.Name}").ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task<int> ExplainRuleAsync(string code, TextWriter output)
    {
        if (!DiagnosticRuleCatalog.TryGet(code, out var rule) || rule is null)
        {
            throw new UsageException($"Unknown diagnostic code '{code}'. Run 'package-medic rules' to list rules.");
        }

        await output.WriteLineAsync($"{rule.Code} — {rule.Name}").ConfigureAwait(false);
        await output.WriteLineAsync($"Default severity: {rule.DefaultSeverity.ToString().ToLowerInvariant()}").ConfigureAwait(false);
        await output.WriteLineAsync(rule.FullDescription).ConfigureAwait(false);
        await output.WriteLineAsync($"Documentation: {rule.HelpUri}").ConfigureAwait(false);
        return 0;
    }

    private static ScanSummary Recount(ScanSummary original, IReadOnlyList<Diagnostic> diagnostics) => original with
    {
        Errors = diagnostics.Count(item => item.Severity == DiagnosticSeverity.Error),
        Warnings = diagnostics.Count(item => item.Severity == DiagnosticSeverity.Warning),
        Information = diagnostics.Count(item => item.Severity == DiagnosticSeverity.Information),
    };

    private static bool ReachesThreshold(IEnumerable<Diagnostic> diagnostics, PolicyFailureLevel failOn) => failOn switch
    {
        PolicyFailureLevel.None => false,
        PolicyFailureLevel.Warning => diagnostics.Any(item => item.Severity >= DiagnosticSeverity.Warning),
        PolicyFailureLevel.Error => diagnostics.Any(item => item.Severity >= DiagnosticSeverity.Error),
        _ => false,
    };

    private static async Task<string> RenderResultAsync(PreparedAnalysis prepared, CliOptions options)
    {
        if (options.Format == OutputFormat.Json)
        {
            return ResultJsonSerializer.Serialize(prepared.Result, prepared.Context) + "\n";
        }

        if (options.Format == OutputFormat.Sarif)
        {
            return RenderSarif(prepared);
        }

        using var writer = new StringWriter();
        await TextResultWriter.WriteAsync(prepared.Result, options.Verbosity, writer, prepared.Context).ConfigureAwait(false);
        return writer.ToString();
    }

    private static string RenderSarif(PreparedAnalysis prepared) =>
        SarifResultSerializer.Serialize(
            prepared.Result,
            prepared.Context.RepositoryRoot,
            prepared.Context.Baseline) + "\n";

    private static void ValidateOutputPaths(CliOptions options)
    {
        if (options.OutputPath is null || options.SarifOutputPath is null)
        {
            return;
        }

        if (PathsEqual(Path.GetFullPath(options.OutputPath), Path.GetFullPath(options.SarifOutputPath)))
        {
            throw new ArgumentException("--output and --sarif-output must use different paths.");
        }
    }

    private static string FindRepositoryRoot(string? target)
    {
        var fullTarget = Path.GetFullPath(target ?? Directory.GetCurrentDirectory());
        var startingDirectory = File.Exists(fullTarget)
            ? Path.GetDirectoryName(fullTarget)
            : Directory.Exists(fullTarget)
                ? fullTarget
                : Path.GetDirectoryName(fullTarget);
        var directory = new DirectoryInfo(startingDirectory ?? Directory.GetCurrentDirectory());
        var fallback = directory.FullName;

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return fallback;
    }

    private static string? ToPortableDisplayPath(string? path, string repositoryRoot)
    {
        if (path is null)
        {
            return null;
        }

        var relative = Path.GetRelativePath(repositoryRoot, Path.GetFullPath(path));
        return relative.StartsWith("..", StringComparison.Ordinal)
            ? Path.GetFileName(path)
            : relative.Replace('\\', '/');
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void EnsureWithinRepository(string path, string repositoryRoot, string description)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(repositoryRoot), Path.GetFullPath(path));
        if (Path.IsPathFullyQualified(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{description} must be inside the Git repository.");
        }
    }

    private static string DefaultConfiguration =>
        """
        {
          "$schema": "https://raw.githubusercontent.com/GonzMeza/package-medic/main/schemas/packagemedic.schema.json",
          "schemaVersion": 1,
          "failOn": "warning",
          "exclude": [
            "**/bin/**",
            "**/obj/**"
          ],
          "rules": {
            "PM006": {
              "enabled": true,
              "severity": "warning"
            }
          },
          "suppressions": [],
          "maxParallelism": 4,
          "timeouts": {
            "restoreSeconds": 300,
            "evaluationSeconds": 60
          },
          "impact": {
            "failOnDowngrade": true,
            "failOnDirectToTransitive": true,
            "failOnSourceChange": true,
            "failOnContentChange": true,
            "maxAddedPackages": null,
            "maxAddedTransitivePackages": null,
            "requirePackageSourceMapping": false,
            "requireLockedMode": false,
            "allowedSources": []
          }
        }
        """ + "\n";

    private static string HelpText => $"""
        PackageMedic {PackageMedicAnalyzer.Version}

        Usage:
          package-medic doctor [path] [options]
          package-medic audit [path] [options]
          package-medic diff <git-ref> [path] [options]
          package-medic simulate <package-id> --to <exact-version> [path] [options]
          package-medic init [directory|file] [--force]
          package-medic baseline create [path] --output <file> [options]
          package-medic baseline update [path] [--baseline <file>] [--output <file>] [options]
          package-medic rules
          package-medic explain <PM code>
          package-medic clean [path] --dry-run [options]
          package-medic --version

        Common scan options:
          --config <path>              Use this configuration file
          --no-config                  Do not auto-load .packagemedic.json
          --baseline <path>            Compare against this baseline
          --no-restore                 Use existing project.assets.json files
          --restore-timeout <seconds>  Restore timeout, 1-3600
          --evaluation-timeout <seconds>
                                       Per-MSBuild evaluation timeout, 1-3600
          --max-parallelism <1-32>     Maximum concurrent restore, audit, and MSBuild processes
          --verbosity quiet|normal|detailed

        Doctor report and gate options:
          --format text|json|sarif     Output format (default: text)
          --output, -o <path>          Write the selected report
          --sarif-output <path>        Also write SARIF from the same analysis
          --fail-on none|warning|error Fail on any effective diagnostic
          --fail-on-new none|warning|error
                                       Fail only on diagnostics absent from baseline
          --audit                      Include official NuGet vulnerability audit data
          --deprecated                 Include official NuGet package deprecation data
          --include-transitive         Include transitive packages in every enabled audit
          --include-transitive-audit   Include transitive vulnerability evidence only
          --include-transitive-deprecated
                                       Include transitive deprecation evidence only

        Diff behavior:
          Compares the working tree with a safely materialized Git reference.
          Reports findings, upgrades/downgrades, dependency-risk deltas, and CPM changes.
          --baseline and --fail-on-new are intentionally not accepted by diff.
          --no-restore requires usable assets files tracked in both compared trees.

        Dependency Time Machine:
          Restore-validates one exact package version in two independent snapshots of HEAD.
          Requires a clean Git worktree and one unambiguous direct/central declaration.
          --format text|json           Human or schema-versioned simulation report
          --output, -o <path>          Write the simulation report atomically
          --audit / --deprecated       Include matching risk deltas when both analyses complete
          --credential-env <name>      Explicitly inherit and redact one private-feed variable;
                                       repeat for additional variables
          Build, tests, and runtime compatibility are not evaluated. Restore still evaluates
          repository-controlled MSBuild content and may contact configured package sources.
          --no-restore, baselines, --fail-on-new, and SARIF are intentionally unavailable.

        Exit codes:
          0  Analysis completed below the configured thresholds
          1  A configured gate was reached or a simulated candidate was rejected
          2  Usage, restore, configuration, or analysis error

        PackageMedic 0.5 remains read-only. clean only supports --dry-run.
        """;

    private sealed record PreparedAnalysis(
        AnalysisResult Result,
        bool HasOperationalError,
        AnalysisReportContext Context);
}
