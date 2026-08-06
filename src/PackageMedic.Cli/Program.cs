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
                CliCommand.Sbom => await RunSbomAsync(options, error, cancellationToken).ConfigureAwait(false),
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

    private static async Task<int> RunSbomAsync(
        CliOptions options,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ValidateOutputPaths(options);
        var prepared = await AnalyzeAsync(options, error, cancellationToken).ConfigureAwait(false);
        if (prepared.HasOperationalError)
        {
            if (options.Verbosity != OutputVerbosity.Quiet)
            {
                await error.WriteLineAsync(
                    "CycloneDX output was not written because dependency analysis was incomplete.")
                    .ConfigureAwait(false);
            }

            return 2;
        }

        await WriteSbomFileAsync(options.SbomOutputPath!, prepared, cancellationToken).ConfigureAwait(false);
        if (options.Verbosity != OutputVerbosity.Quiet)
        {
            await error.WriteLineAsync($"Wrote CycloneDX 1.7 SBOM to {options.SbomOutputPath}")
                .ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task<int> RunDiffAsync(
        CliOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ValidateOutputPaths(options);
        if (options.Verify is not null)
        {
            return await RunVerifiedDiffAsync(options, output, error, cancellationToken)
                .ConfigureAwait(false);
        }

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

        // A Git comparison must not allow the candidate tree to select or weaken the
        // policy that evaluates that same candidate. Repository-owned configuration is
        // resolved from the immutable base snapshot and then applied to both analyses.
        // An explicit configuration outside the repository remains caller-owned invocation
        // policy and is likewise shared by both sides.
        var trustedOptions = ResolveTrustedDiffConfiguration(
            options,
            currentRepositoryRoot,
            currentTarget,
            snapshot.SnapshotDirectory,
            snapshotTarget);

        using var currentRuntimeRoot = OwnedTemporaryDirectory.Create(currentRepositoryRoot);
        using var baselineRuntimeRoot = OwnedTemporaryDirectory.Create(currentRepositoryRoot);
        var untrustedRoots = new[] { currentRepositoryRoot, snapshot.SnapshotDirectory };
        var currentRuntime = CreateDiffRuntime(currentRuntimeRoot.DirectoryPath, untrustedRoots, processRunner);
        var baselineRuntime = CreateDiffRuntime(baselineRuntimeRoot.DirectoryPath, untrustedRoots, processRunner);
        var current = await AnalyzeAsync(
            trustedOptions,
            error,
            cancellationToken,
            currentRepositoryRoot,
            packagesDirectory: currentRuntime.PackagesDirectory,
            processRunnerOverride: currentRuntime.ProcessRunner).ConfigureAwait(false);
        var baselineOptions = trustedOptions with
        {
            Path = snapshotTarget,
            OutputPath = null,
            SarifOutputPath = null,
            SbomOutputPath = null,
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
            baseline.Context.Policy.Impact);

        return await CompleteDiffAsync(
            options,
            current,
            currentRepositoryRoot,
            baseline,
            comparison,
            output,
            error,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> CompleteDiffAsync(
        CliOptions options,
        PreparedAnalysis current,
        string currentRepositoryRoot,
        PreparedAnalysis baseline,
        AnalysisDiffReport comparison,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
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

        if (comparison.Verification?.Decision.Verdict == VerificationVerdict.Incomplete)
        {
            return 2;
        }

        if (comparison.Verification?.Decision.Verdict == VerificationVerdict.Reject)
        {
            return 1;
        }

        if (!comparison.IsComplete || current.HasOperationalError || baseline.HasOperationalError)
        {
            return 2;
        }

        return comparison.Impact?.GatePassed == false ||
               ReachesThreshold(gateDiagnostics, baseline.Context.Policy.FailOn)
            ? 1
            : 0;
    }

    private static async Task<int> RunVerifiedDiffAsync(
        CliOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = FindRepositoryRoot(options.Path);
        var currentTarget = Path.GetFullPath(options.Path ?? Directory.GetCurrentDirectory());
        EnsureWithinRepository(currentTarget, repositoryRoot, "The verified diff target");
        if (!File.Exists(currentTarget) && !Directory.Exists(currentTarget))
        {
            throw new ArgumentException($"The verified diff target does not exist: {currentTarget}");
        }

        var processRunner = new ProcessRunner();
        var workingTree = new GitWorkingTreeInspector(processRunner);
        var commits = new GitCommitInspector(processRunner);
        await workingTree.EnsureCleanAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        var currentCommit = await commits.ResolveHeadAsync(repositoryRoot, cancellationToken)
            .ConfigureAwait(false);
        var baselineCommit = await commits.ResolveCommitAsync(
                repositoryRoot,
                options.GitReference!,
                cancellationToken)
            .ConfigureAwait(false);
        await commits.EnsureVerificationTreeSupportedAsync(
                repositoryRoot,
                baselineCommit,
                cancellationToken)
            .ConfigureAwait(false);
        await commits.EnsureVerificationTreeSupportedAsync(
                repositoryRoot,
                currentCommit,
                cancellationToken)
            .ConfigureAwait(false);
        await workingTree.EnsureArchiveSemanticsAreReproducibleAsync(
                repositoryRoot,
                baselineCommit,
                cancellationToken)
            .ConfigureAwait(false);
        await workingTree.EnsureArchiveSemanticsAreReproducibleAsync(
                repositoryRoot,
                currentCommit,
                cancellationToken)
            .ConfigureAwait(false);

        PreparedAnalysis current;
        PreparedAnalysis baseline;
        AnalysisDiffReport comparison;
        string? provenance = null;
        Exception? operationError = null;
        try
        {
            using (var baselineSnapshot = await new GitSnapshotProvider(processRunner)
                   .MaterializeAsync(repositoryRoot, baselineCommit, cancellationToken)
                   .ConfigureAwait(false))
            using (var currentSnapshot = await new GitSnapshotProvider(processRunner)
                   .MaterializeAsync(repositoryRoot, currentCommit, cancellationToken)
                   .ConfigureAwait(false))
            using (var baselineRuntimeRoot = OwnedTemporaryDirectory.Create(repositoryRoot))
            using (var currentRuntimeRoot = OwnedTemporaryDirectory.Create(repositoryRoot))
            {
                if (!baselineSnapshot.Commit.Equals(baselineCommit, StringComparison.Ordinal) ||
                    !currentSnapshot.Commit.Equals(currentCommit, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Verified diff snapshots did not preserve their resolved immutable commits.");
                }

                var relativeTarget = Path.GetRelativePath(repositoryRoot, currentTarget);
                var baselineTarget = MapVerifiedDiffTarget(
                    baselineSnapshot.SnapshotDirectory,
                    relativeTarget,
                    "base");
                var snapshotCurrentTarget = MapVerifiedDiffTarget(
                    currentSnapshot.SnapshotDirectory,
                    relativeTarget,
                    "current");
                var trustedOptions = ResolveTrustedDiffConfiguration(
                    options,
                    repositoryRoot,
                    currentTarget,
                    baselineSnapshot.SnapshotDirectory,
                    baselineTarget);
                var baselineOptions = CreateVerifiedDiffOptions(trustedOptions, baselineTarget);
                var currentOptions = CreateVerifiedDiffOptions(trustedOptions, snapshotCurrentTarget);
                var untrustedRoots = new[]
                {
                repositoryRoot,
                baselineSnapshot.SnapshotDirectory,
                currentSnapshot.SnapshotDirectory,
            };
                var baselineRuntime = CreateDiffRuntime(
                    baselineRuntimeRoot.DirectoryPath,
                    untrustedRoots,
                    processRunner);
                var currentRuntime = CreateDiffRuntime(
                    currentRuntimeRoot.DirectoryPath,
                    untrustedRoots,
                    processRunner);

                baseline = await AnalyzeAsync(
                    baselineOptions,
                    error,
                    cancellationToken,
                    baselineSnapshot.SnapshotDirectory,
                    packagesDirectory: baselineRuntime.PackagesDirectory,
                    processRunnerOverride: baselineRuntime.ProcessRunner).ConfigureAwait(false);
                current = await AnalyzeAsync(
                    currentOptions,
                    error,
                    cancellationToken,
                    currentSnapshot.SnapshotDirectory,
                    packagesDirectory: currentRuntime.PackagesDirectory,
                    processRunnerOverride: currentRuntime.ProcessRunner).ConfigureAwait(false);
                baselineSnapshot.EnsureTrackedFilesUnchanged();
                currentSnapshot.EnsureTrackedFilesUnchanged();
                comparison = AnalysisDiffComparer.Compare(
                    baseline.Result,
                    baselineSnapshot.SnapshotDirectory,
                    current.Result,
                    currentSnapshot.SnapshotDirectory,
                    options.GitReference!,
                    baselineCommit,
                    baseline.Context.Policy.Impact);

                comparison = comparison with
                {
                    Verification = await VerifyDiffSnapshotsAsync(
                        options,
                        baseline,
                        baselineSnapshot.SnapshotDirectory,
                        baselineRuntime.ProcessRunner,
                        current,
                        currentSnapshot.SnapshotDirectory,
                        currentRuntime.ProcessRunner,
                        comparison,
                        baselineSnapshot.EnsureTrackedFilesUnchanged,
                        currentSnapshot.EnsureTrackedFilesUnchanged,
                        cancellationToken).ConfigureAwait(false),
                };

                if (options.ProvenanceOutputPath is not null &&
                    comparison.Verification.Decision.Verdict != VerificationVerdict.Incomplete)
                {
                    provenance = await CreateAnalysisProvenanceAsync(
                        baselineCommit,
                        currentCommit,
                        current,
                        comparison,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception)
        {
            operationError = exception;
            throw;
        }
        finally
        {
            await RevalidateCheckoutAsync(
                repositoryRoot,
                currentCommit,
                workingTree,
                commits,
                operationError).ConfigureAwait(false);
        }
        if (options.ProvenanceOutputPath is not null)
        {
            if (provenance is null)
            {
                if (options.Verbosity != OutputVerbosity.Quiet)
                {
                    await error.WriteLineAsync(
                        "Analysis provenance was not written because immutable verification evidence was incomplete.")
                        .ConfigureAwait(false);
                }
            }
            else
            {
                await AtomicOutputFile.WriteAsync(
                    options.ProvenanceOutputPath,
                    provenance + "\n",
                    cancellationToken).ConfigureAwait(false);
                if (options.Verbosity != OutputVerbosity.Quiet)
                {
                    await error.WriteLineAsync(
                        $"Wrote unsigned in-toto analysis evidence to {options.ProvenanceOutputPath}")
                        .ConfigureAwait(false);
                }
            }
        }

        return await CompleteDiffAsync(
            options,
            current,
            current.Context.RepositoryRoot,
            baseline,
            comparison,
            output,
            error,
            cancellationToken).ConfigureAwait(false);
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
        var commitInspector = new GitCommitInspector(processRunner);
        await worktreeInspector.EnsureCleanAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        var expectedCommit = await commitInspector.ResolveHeadAsync(repositoryRoot, cancellationToken)
            .ConfigureAwait(false);
        await commitInspector.EnsureVerificationTreeSupportedAsync(
                repositoryRoot,
                expectedCommit,
                cancellationToken)
            .ConfigureAwait(false);
        await worktreeInspector.EnsureArchiveSemanticsAreReproducibleAsync(
                repositoryRoot,
                expectedCommit,
                cancellationToken)
            .ConfigureAwait(false);
        DependencySimulationReport report;
        int exitCode;
        Exception? operationError = null;
        try
        {
            (report, exitCode) = await CreateSimulationReportAsync(
                options,
                repositoryRoot,
                currentTarget,
                expectedCommit,
                explicitCredentials,
                processRunner,
                error,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            operationError = exception;
            throw;
        }
        finally
        {
            await RevalidateCheckoutAsync(
                repositoryRoot,
                expectedCommit,
                worktreeInspector,
                commitInspector,
                operationError).ConfigureAwait(false);
        }

        // The output file is the only intentional repository write and is produced after
        // both owned snapshots have been deleted and the original checkout revalidated.
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
        string expectedCommit,
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
                    .MaterializeAsync(repositoryRoot, expectedCommit, cancellationToken)
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
            baselineSnapshot.EnsureTrackedFilesUnchanged();
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
            candidateSnapshot.RecordExpectedTrackedFileMutation(edit.File);
            var lockedMode = ResolveSimulationLockedMode(
                baseline.Result.ProjectSettings,
                baselineSnapshot.SnapshotDirectory,
                edit.AffectedProjects);
            var candidateDiscovery = new ProjectDiscovery().Discover(
                candidateTarget,
                candidateSnapshot.SnapshotDirectory);
            var (candidateConfiguration, _, candidateConfigurationDirectory, _) = LoadConfiguration(
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
                candidateRuntime.PackagesDirectory,
                options.Verify is null ? null : options.VerificationConfiguration).ConfigureAwait(false);
            VerifySimulationMutationHash(candidateSnapshot.SnapshotDirectory, edit);
            candidateSnapshot.EnsureTrackedFilesUnchanged();

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
            candidateSnapshot.EnsureTrackedFilesUnchanged();
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

            VerificationComparisonReport? executedVerification = null;
            if (options.Verify is not null)
            {
                executedVerification = await VerifyDiffSnapshotsAsync(
                    options,
                    baseline,
                    baselineSnapshot.SnapshotDirectory,
                    baselineRuntime.ProcessRunner,
                    candidate,
                    candidateSnapshot.SnapshotDirectory,
                    candidateRuntime.ProcessRunner,
                    comparison,
                    baselineSnapshot.EnsureTrackedFilesUnchanged,
                    candidateSnapshot.EnsureTrackedFilesUnchanged,
                    cancellationToken).ConfigureAwait(false);
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
            if (executedVerification?.Decision.Verdict == VerificationVerdict.Reject)
            {
                rejectionReasons = rejectionReasons
                    .Append(DescribeVerificationRejection(executedVerification.Decision))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            }

            var noChange = HasNoObservedSimulationImpact(comparison);
            var verificationIncomplete =
                executedVerification?.Decision.Verdict == VerificationVerdict.Incomplete;
            var verdict = verificationIncomplete
                ? DependencySimulationVerdict.Incomplete
                : rejectionReasons.Count > 0
                    ? DependencySimulationVerdict.Reject
                    : noChange
                        ? DependencySimulationVerdict.NoChange
                        : DependencySimulationVerdict.Pass;
            var simulationVerification = DependencySimulationVerification.RestoreOnly(
                DependencySimulationVerificationStatus.Passed,
                options.AuditVulnerabilities,
                options.AuditDeprecatedPackages,
                lockedMode,
                requestedLevel: options.Verify) with
            {
                Executed = executedVerification,
                Build = ToSimulationStatus(executedVerification?.Candidate.Build.Stage),
                Tests = ToSimulationStatus(executedVerification?.Candidate.Tests.Stage),
                EvidenceLevel = ToSimulationEvidenceLevel(executedVerification?.Decision.CommonEvidenceLevel),
            };
            var completedReport = new DependencySimulationReport(
                DependencySimulationReport.CurrentSchemaVersion,
                PackageMedicAnalyzer.Version,
                new DependencySimulationRepository(
                    baselineSnapshot.Commit,
                    ToPortableSimulationPath(relativeTarget),
                    WorkingTreeRequiredClean: true),
                new DependencySimulationRequest(options.SimulationPackageId!, options.SimulationTargetVersion!),
                DependencySimulationMutation.From(edit),
                simulationVerification,
                CreateSimulationComparison(comparison),
                verdict,
                rejectionReasons,
                verificationIncomplete
                    ? ["The requested build or test verification was incomplete."]
                    : []);
            return (completedReport, verdict switch
            {
                DependencySimulationVerdict.Reject => 1,
                DependencySimulationVerdict.Incomplete => 2,
                _ => 0,
            });
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
        var packagesDirectory = Path.Combine(runtimeRoot, "nuget", "packages");
        var environment = ProcessEnvironment.CreateIsolatedDotNet(
            runtimeRoot,
            packagesDirectory,
            untrustedExecutableRoots: untrustedRoots);
        return new SimulationRuntime(
            new EnvironmentScopedProcessRunner(processRunner, environment),
            packagesDirectory);
    }

    private static CliOptions CreateVerifiedDiffOptions(CliOptions options, string snapshotTarget) =>
        options with
        {
            Path = snapshotTarget,
            OutputPath = null,
            SarifOutputPath = null,
            SbomOutputPath = null,
            ProvenanceOutputPath = null,
            BaselinePath = null,
            FailOnNew = null,
            GitReference = null,
        };

    private static string MapVerifiedDiffTarget(
        string snapshotRoot,
        string relativeTarget,
        string role)
    {
        var target = Path.GetFullPath(Path.Combine(snapshotRoot, relativeTarget));
        EnsureWithinRepository(target, snapshotRoot, $"The verified {role} snapshot target");
        if (!File.Exists(target) && !Directory.Exists(target))
        {
            throw new InvalidOperationException(
                $"The verified diff target does not exist in the {role} commit.");
        }

        if (!ProjectDiscovery.IsSafelyContained(snapshotRoot, target))
        {
            throw new InvalidOperationException(
                $"The verified {role} snapshot target crosses a symbolic-link boundary.");
        }

        return target;
    }

    private static async Task RevalidateCheckoutAsync(
        string repositoryRoot,
        string expectedCommit,
        GitWorkingTreeInspector workingTree,
        GitCommitInspector commits,
        Exception? operationError)
    {
        Exception? integrityError = null;
        using var validation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await workingTree.EnsureCleanAsync(repositoryRoot, validation.Token).ConfigureAwait(false);
            await commits.EnsureHeadEqualsAsync(repositoryRoot, expectedCommit, validation.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            integrityError = new InvalidOperationException(
                "The original Git checkout could not be proven unchanged after verified execution; the environment is unsafe.",
                exception);
        }

        if (integrityError is null)
        {
            return;
        }

        if (operationError is not null)
        {
            throw new AggregateException(
                "Verified execution failed and the original Git checkout also failed integrity revalidation.",
                operationError,
                integrityError);
        }

        throw integrityError;
    }

    private static async Task<VerificationComparisonReport> VerifyDiffSnapshotsAsync(
        CliOptions options,
        PreparedAnalysis baseline,
        string baselineRoot,
        IProcessRunner baselineRunner,
        PreparedAnalysis candidate,
        string candidateRoot,
        IProcessRunner candidateRunner,
        AnalysisDiffReport comparison,
        Action baselineIntegrityCheck,
        Action candidateIntegrityCheck,
        CancellationToken cancellationToken)
    {
        var level = options.Verify ?? VerificationLevel.Restore;
        var baselineRestore = CreateRestoreVerificationEvidence(baseline);
        var candidateRestore = CreateRestoreVerificationEvidence(candidate);
        var baselineBuild = level == VerificationLevel.Restore ||
                            baselineRestore.Status != VerificationStageStatus.Passed
            ? NotRequestedBuild()
            : await ExecuteBuildVerificationAsync(
                options,
                baseline,
                baselineRoot,
                baselineRunner,
                cancellationToken).ConfigureAwait(false);
        var candidateBuild = level == VerificationLevel.Restore ||
                             candidateRestore.Status != VerificationStageStatus.Passed
            ? NotRequestedBuild()
            : await ExecuteBuildVerificationAsync(
                options,
                candidate,
                candidateRoot,
                candidateRunner,
                cancellationToken).ConfigureAwait(false);
        baselineIntegrityCheck();
        candidateIntegrityCheck();

        var baselineTests = level != VerificationLevel.Test ||
                            baselineBuild.Stage.Status != VerificationStageStatus.Passed
            ? NotRequestedTests()
            : await ExecuteTestVerificationAsync(
                options,
                baseline,
                baselineRoot,
                baselineRunner,
                cancellationToken).ConfigureAwait(false);
        var candidateTests = level != VerificationLevel.Test ||
                             candidateBuild.Stage.Status != VerificationStageStatus.Passed
            ? NotRequestedTests()
            : await ExecuteTestVerificationAsync(
                options,
                candidate,
                candidateRoot,
                candidateRunner,
                cancellationToken).ConfigureAwait(false);
        baselineIntegrityCheck();
        candidateIntegrityCheck();
        var baselineSnapshot = new VerificationSnapshotReport(
            baselineRestore,
            baselineBuild,
            baselineTests);
        var candidateSnapshot = new VerificationSnapshotReport(
            candidateRestore,
            candidateBuild,
            candidateTests);
        var decision = VerificationVerdictEngine.Compare(new VerificationComparisonInput(
            level,
            new VerificationSnapshotEvidence(
                baselineRestore,
                baselineBuild.Stage,
                baselineTests.Stage),
            new VerificationSnapshotEvidence(
                candidateRestore,
                candidateBuild.Stage,
                candidateTests.Stage),
            HasObservedDiffChange(comparison)));
        return new VerificationComparisonReport(level, baselineSnapshot, candidateSnapshot, decision);
    }

    private static VerificationStageEvidence CreateRestoreVerificationEvidence(PreparedAnalysis analysis)
        => CreateRestoreVerificationEvidence(
            analysis.Restore,
            analysis.HasOperationalError || analysis.Result.AnalysisErrors.Count > 0,
            analysis.Result.ProjectSettings);

    internal static VerificationStageEvidence CreateRestoreVerificationEvidence(
        RestoreExecutionResult? restore,
        bool analysisIncomplete,
        IReadOnlyList<ProjectPackageSettings> projectSettings)
    {
        ArgumentNullException.ThrowIfNull(projectSettings);
        if (restore is null)
        {
            return VerificationStageEvidence.Incomplete(VerificationFailureKind.AnalysisIncomplete);
        }

        if (!restore.Succeeded)
        {
            var lockedMode = projectSettings.Count == 0
                ? DependencySimulationLockedMode.NotEnabled
                : projectSettings.All(item => item.RestoreLockedMode)
                    ? DependencySimulationLockedMode.Enforced
                    : projectSettings.Any(item => item.RestoreLockedMode)
                        ? DependencySimulationLockedMode.Mixed
                        : DependencySimulationLockedMode.NotEnabled;
            var failure = ClassifySimulationRestoreFailure(restore, lockedMode);
            return failure switch
            {
                DependencySimulationRestoreFailureKind.PackageNotFound =>
                    VerificationStageEvidence.Failed(VerificationFailureKind.PackageNotFound),
                DependencySimulationRestoreFailureKind.VersionNotFound =>
                    VerificationStageEvidence.Failed(VerificationFailureKind.VersionNotFound),
                DependencySimulationRestoreFailureKind.DependencyConflict =>
                    VerificationStageEvidence.Failed(VerificationFailureKind.DependencyConflict),
                DependencySimulationRestoreFailureKind.LockedModeConflict =>
                    VerificationStageEvidence.Failed(VerificationFailureKind.LockedModeConflict),
                DependencySimulationRestoreFailureKind.SourceUnavailable =>
                    VerificationStageEvidence.Incomplete(VerificationFailureKind.SourceUnavailable),
                DependencySimulationRestoreFailureKind.AuthenticationFailed =>
                    VerificationStageEvidence.Incomplete(VerificationFailureKind.AuthenticationFailed),
                DependencySimulationRestoreFailureKind.TimedOut =>
                    VerificationStageEvidence.Incomplete(VerificationFailureKind.TimedOut),
                DependencySimulationRestoreFailureKind.OutputLimitExceeded =>
                    VerificationStageEvidence.Incomplete(VerificationFailureKind.OutputLimitExceeded),
                _ => VerificationStageEvidence.Incomplete(VerificationFailureKind.Unknown),
            };
        }

        return analysisIncomplete
            ? VerificationStageEvidence.Incomplete(VerificationFailureKind.AnalysisIncomplete)
            : VerificationStageEvidence.Passed;
    }

    private static async Task<VerificationBuildReport> ExecuteBuildVerificationAsync(
        CliOptions options,
        PreparedAnalysis analysis,
        string snapshotRoot,
        IProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        try
        {
            var plan = new VerificationPlanBuilder().Build(
                analysis.Discovery,
                analysis.EvaluatedProjects);
            var timeout = TimeSpan.FromSeconds(options.BuildTimeoutSeconds ?? 900);
            var result = await new BuildVerificationRunner(
                    processRunner,
                    timeout,
                    TimeSpan.FromHours(1))
                .RunAsync(
                    plan,
                    snapshotRoot,
                    options.VerificationConfiguration,
                    cancellationToken)
                .ConfigureAwait(false);
            var blocking = result.Targets.LastOrDefault(item =>
                item.Status is VerificationStageStatus.Failed or VerificationStageStatus.Incomplete);
            return new VerificationBuildReport(
                result.Evidence,
                plan.BuildTargets.Count,
                result.Targets.Count(item => item.Status == VerificationStageStatus.Passed),
                blocking is null
                    ? null
                    : ToPortableDisplayPath(blocking.Target, snapshotRoot),
                result.Errors.Count == 0
                    ? null
                    : ToPortableVerificationFailure(result.Errors[0], snapshotRoot));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            IOException or
            UnauthorizedAccessException)
        {
            return new VerificationBuildReport(
                VerificationStageEvidence.Incomplete(VerificationFailureKind.AnalysisIncomplete),
                0,
                0,
                Failure: ToPortableVerificationFailure(exception.Message, snapshotRoot));
        }
    }

    private static VerificationBuildReport NotRequestedBuild() => new(
        VerificationStageEvidence.NotRequested,
        0,
        0);

    private static async Task<VerificationTestReport> ExecuteTestVerificationAsync(
        CliOptions options,
        PreparedAnalysis analysis,
        string snapshotRoot,
        IProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        try
        {
            var plan = new VerificationPlanBuilder().Build(
                analysis.Discovery,
                analysis.EvaluatedProjects);
            var timeout = TimeSpan.FromSeconds(options.TestTimeoutSeconds ?? 1_200);
            var result = await new TestVerificationRunner(
                    processRunner,
                    new TrxTestResultParser(),
                    TrxTestResultLimits.Default,
                    timeout,
                    TimeSpan.FromHours(1))
                .RunAsync(
                    plan,
                    snapshotRoot,
                    options.VerificationConfiguration,
                    cancellationToken)
                .ConfigureAwait(false);
            return new VerificationTestReport(
                result.Evidence,
                plan.TestProjects.Count,
                result.Projects.Count(project => project.Status == VerificationStageStatus.Passed),
                result.Total,
                result.Passed,
                result.Failed,
                result.Skipped,
                result.FailedTestIdentities,
                result.HasAdditionalFailedTests,
                result.Errors.Count == 0
                    ? null
                    : ToPortableVerificationFailure(result.Errors[0], snapshotRoot));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            IOException or
            UnauthorizedAccessException)
        {
            return new VerificationTestReport(
                VerificationStageEvidence.Incomplete(VerificationFailureKind.AnalysisIncomplete),
                0,
                0,
                0,
                0,
                0,
                0,
                [],
                Failure: ToPortableVerificationFailure(exception.Message, snapshotRoot));
        }
    }

    private static VerificationTestReport NotRequestedTests() => new(
        VerificationStageEvidence.NotRequested,
        0,
        0,
        0,
        0,
        0,
        0,
        []);

    private static bool HasObservedDiffChange(AnalysisDiffReport comparison) =>
        comparison.Changes.Count > 0 ||
        comparison.PackageChanges.Count > 0 ||
        comparison.ProjectSettingsChanges.Count > 0 ||
        comparison.RiskSummary.VulnerabilitiesIntroduced > 0 ||
        comparison.RiskSummary.VulnerabilitiesResolved > 0 ||
        comparison.RiskSummary.DeprecationsIntroduced > 0 ||
        comparison.RiskSummary.DeprecationsResolved > 0;

    private static string ToPortableVerificationFailure(string value, string snapshotRoot)
    {
        var sanitized = ProcessRunner.RedactSecrets(value)
            .Replace(Path.GetFullPath(snapshotRoot), "%SNAPSHOT%", StringComparison.OrdinalIgnoreCase)
            .Replace('\\', '/');
        sanitized = string.Join(
            ' ',
            sanitized.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return sanitized.Length <= 4_096 ? sanitized : sanitized[..4_096];
    }

    private static DependencySimulationVerificationStatus ToSimulationStatus(
        VerificationStageEvidence? evidence) => evidence?.Status switch
        {
            VerificationStageStatus.Passed => DependencySimulationVerificationStatus.Passed,
            VerificationStageStatus.Failed => DependencySimulationVerificationStatus.Failed,
            VerificationStageStatus.Incomplete => DependencySimulationVerificationStatus.Incomplete,
            _ => DependencySimulationVerificationStatus.NotRun,
        };

    private static DependencySimulationEvidenceLevel ToSimulationEvidenceLevel(
        VerificationEvidenceLevel? evidenceLevel) => evidenceLevel switch
        {
            VerificationEvidenceLevel.TestVerified => DependencySimulationEvidenceLevel.TestVerified,
            VerificationEvidenceLevel.BuildVerified => DependencySimulationEvidenceLevel.BuildVerified,
            _ => DependencySimulationEvidenceLevel.RestoreOnly,
        };

    private static string DescribeVerificationRejection(VerificationDecision decision) =>
        decision.BlockingStage switch
        {
            VerificationStage.Build =>
                "The candidate introduced a build failure after the baseline built successfully.",
            VerificationStage.Test =>
                "The candidate introduced a test failure after the baseline tests passed.",
            VerificationStage.Restore =>
                "The candidate introduced a deterministic restore failure after the baseline restored successfully.",
            _ => "The candidate failed a requested verification stage that passed for the baseline.",
        };

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
                DependencySimulationVerificationStatus.Incomplete,
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
                restoreFailureKind,
                options.Verify),
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

        if (restore.FailureKind != RestoreProcessFailureKind.Rejected || restore.Errors.Count == 0)
        {
            return DependencySimulationRestoreFailureKind.Unknown;
        }

        if (restore.RejectedTargets.Count > 0)
        {
            var structured = restore.RejectedTargets
                .Select(item => MapRestoreRejectionEvidence(item, lockedMode))
                .Distinct()
                .ToArray();
            return structured.Length == 1
                ? structured[0]
                : DependencySimulationRestoreFailureKind.Unknown;
        }

        var classifiedErrors = restore.Errors
            .Select(item => ClassifyRejectedRestoreError(item, lockedMode))
            .Distinct()
            .ToArray();
        return classifiedErrors.Length == 1
            ? classifiedErrors[0]
            : DependencySimulationRestoreFailureKind.Unknown;
    }

    private static DependencySimulationRestoreFailureKind MapRestoreRejectionEvidence(
        RestoreRejectionEvidenceKind evidence,
        DependencySimulationLockedMode lockedMode) => evidence switch
        {
            RestoreRejectionEvidenceKind.AuthenticationFailed =>
                DependencySimulationRestoreFailureKind.AuthenticationFailed,
            RestoreRejectionEvidenceKind.SourceUnavailable =>
                DependencySimulationRestoreFailureKind.SourceUnavailable,
            RestoreRejectionEvidenceKind.LockFileConflict
                when lockedMode is DependencySimulationLockedMode.Enforced or DependencySimulationLockedMode.Mixed =>
                DependencySimulationRestoreFailureKind.LockedModeConflict,
            RestoreRejectionEvidenceKind.PackageNotFound =>
                DependencySimulationRestoreFailureKind.PackageNotFound,
            RestoreRejectionEvidenceKind.VersionNotFound =>
                DependencySimulationRestoreFailureKind.VersionNotFound,
            RestoreRejectionEvidenceKind.DependencyConflict =>
                DependencySimulationRestoreFailureKind.DependencyConflict,
            _ => DependencySimulationRestoreFailureKind.Unknown,
        };

    private static DependencySimulationRestoreFailureKind ClassifyRejectedRestoreError(
        string error,
        DependencySimulationLockedMode lockedMode)
    {
        var candidates = new HashSet<DependencySimulationRestoreFailureKind>();
        if (error.Contains("401 Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("403 Forbidden", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("forbidden", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(DependencySimulationRestoreFailureKind.AuthenticationFailed);
        }

        if (error.Contains("NU1300", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("NU1301", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(DependencySimulationRestoreFailureKind.SourceUnavailable);
        }

        if (lockedMode is DependencySimulationLockedMode.Enforced or DependencySimulationLockedMode.Mixed &&
            (error.Contains("NU1004", StringComparison.OrdinalIgnoreCase) ||
             error.Contains("NU1005", StringComparison.OrdinalIgnoreCase)))
        {
            candidates.Add(DependencySimulationRestoreFailureKind.LockedModeConflict);
        }

        if (error.Contains("NU1101", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(DependencySimulationRestoreFailureKind.PackageNotFound);
        }

        if (error.Contains("NU1102", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(DependencySimulationRestoreFailureKind.VersionNotFound);
        }

        if (error.Contains("NU1106", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("NU1107", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("NU1605", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(DependencySimulationRestoreFailureKind.DependencyConflict);
        }

        return candidates.Count == 1
            ? candidates.Single()
            : DependencySimulationRestoreFailureKind.Unknown;
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

        if (options.SbomOutputPath is not null)
        {
            if (prepared.HasOperationalError || prepared.Result.AnalysisErrors.Count > 0)
            {
                if (options.Verbosity != OutputVerbosity.Quiet)
                {
                    await error.WriteLineAsync(
                        "CycloneDX output was not written because dependency analysis was incomplete.")
                        .ConfigureAwait(false);
                }
            }
            else
            {
                await WriteSbomFileAsync(options.SbomOutputPath, prepared, cancellationToken).ConfigureAwait(false);
                if (options.Verbosity != OutputVerbosity.Quiet)
                {
                    await error.WriteLineAsync($"Wrote CycloneDX 1.7 SBOM to {options.SbomOutputPath}").ConfigureAwait(false);
                }
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

    private static Task WriteSbomFileAsync(
        string path,
        PreparedAnalysis prepared,
        CancellationToken cancellationToken) => AtomicOutputFile.WriteAsync(
        path,
        async (stream, token) =>
        {
            await CycloneDxSbomSerializer.SerializeAsync(
                stream,
                prepared.Result,
                prepared.Context.RepositoryRoot,
                token).ConfigureAwait(false);
        },
        cancellationToken);

    private static async Task<string> CreateAnalysisProvenanceAsync(
        string baselineGitCommit,
        string gitCommit,
        PreparedAnalysis prepared,
        AnalysisDiffReport comparison,
        CancellationToken cancellationToken)
    {
        var configuration = prepared.ConfigurationSha256 is null
            ? PackageMedicConfigurationFingerprint.None
            : PackageMedicConfigurationFingerprint.FromSha256(
                prepared.ConfigurationSha256);
        byte[]? sbomBytes = null;
        if (!prepared.HasOperationalError && prepared.Result.AnalysisErrors.Count == 0)
        {
            sbomBytes = await SerializeSbomBytesAsync(prepared, cancellationToken).ConfigureAwait(false);
        }

        var comparisonBytes = System.Text.Encoding.UTF8.GetBytes(
            AnalysisDiffSerializer.SerializeJson(comparison));
        var target = ToPortableDisplayPath(prepared.Result.Target, prepared.Context.RepositoryRoot) ?? ".";
        var evidence = new PackageMedicAnalysisEvidence(
            target,
            baselineGitCommit,
            Convert.ToHexString(SHA256.HashData(comparisonBytes)).ToLowerInvariant(),
            PackageMedicAnalyzer.Version,
            comparison.Verification!.Level,
            comparison.Verification.Decision.Verdict,
            PackageMedicAnalysisCompleteness.Complete,
            configuration)
        {
            SbomSha256 = sbomBytes is null
                ? null
                : Convert.ToHexString(SHA256.HashData(sbomBytes)).ToLowerInvariant(),
        };
        return InTotoEvidenceSerializer.SerializePackageMedicAnalysisStatement(gitCommit, evidence);
    }

    private static async Task<byte[]> SerializeSbomBytesAsync(
        PreparedAnalysis prepared,
        CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream();
        await CycloneDxSbomSerializer.SerializeAsync(
            stream,
            prepared.Result,
            prepared.Context.RepositoryRoot,
            cancellationToken).ConfigureAwait(false);
        return stream.ToArray();
    }

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

        await output.WriteLineAsync($"Plan: {candidates.Length} candidate(s); apply is intentionally unavailable in 0.6.").ConfigureAwait(false);
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
        var (configuration, configurationPath, configurationDirectory, configurationSha256) =
            LoadConfiguration(options, repositoryRoot);
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
            preparedRestore,
            options.Verify is null ? null : options.VerificationConfiguration).ConfigureAwait(false);

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
            outcome = outcome with
            {
                Result = outcome.Result with
                {
                    Diagnostics = auditDiagnostics,
                    Summary = Recount(outcome.Result.Summary, auditDiagnostics),
                    AnalysisErrors = auditErrors,
                    Vulnerabilities = audit.Vulnerabilities,
                },
                HasOperationalError = outcome.HasOperationalError || audit.HasOperationalError,
            };
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
            outcome = outcome with
            {
                Result = outcome.Result with
                {
                    Diagnostics = auditDiagnostics,
                    Summary = Recount(outcome.Result.Summary, auditDiagnostics),
                    AnalysisErrors = auditErrors,
                    DeprecatedPackages = audit.DeprecatedPackages,
                },
                HasOperationalError = outcome.HasOperationalError || audit.HasOperationalError,
            };
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
        return new PreparedAnalysis(
            result,
            outcome.HasOperationalError,
            context,
            outcome.Discovery ?? discovered,
            outcome.EvaluatedProjects,
            configurationSha256,
            outcome.Restore);
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

    internal static CliOptions ResolveTrustedDiffConfiguration(
        CliOptions options,
        string currentRepositoryRoot,
        string currentTarget,
        string baselineRepositoryRoot,
        string baselineTarget)
    {
        ArgumentNullException.ThrowIfNull(options);
        var currentRoot = Path.GetFullPath(currentRepositoryRoot);
        var baselineRoot = Path.GetFullPath(baselineRepositoryRoot);
        var normalizedCurrentTarget = Path.GetFullPath(currentTarget);
        var normalizedBaselineTarget = Path.GetFullPath(baselineTarget);
        EnsureWithinRepository(normalizedCurrentTarget, currentRoot, "The current diff target");
        EnsureWithinRepository(normalizedBaselineTarget, baselineRoot, "The baseline diff target");

        if (options.NoConfiguration)
        {
            return options with { ConfigurationPath = null, NoConfiguration = true };
        }

        string? trustedConfiguration;
        if (options.ConfigurationPath is null)
        {
            trustedConfiguration = FindAutomaticConfiguration(normalizedBaselineTarget, baselineRoot);
        }
        else
        {
            var requested = Path.GetFullPath(options.ConfigurationPath);
            var relative = Path.GetRelativePath(currentRoot, requested);
            var isRepositoryOwned = !Path.IsPathFullyQualified(relative) &&
                relative != ".." &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
            if (!isRepositoryOwned)
            {
                // A configuration outside the analyzed repository can only have been
                // selected explicitly by the caller. Treat it as invocation policy.
                trustedConfiguration = requested;
            }
            else
            {
                if (!ProjectDiscovery.IsSafelyContained(currentRoot, requested))
                {
                    throw new InvalidOperationException(
                        "The repository-owned diff configuration resolves through an unsafe symbolic link or junction.");
                }

                trustedConfiguration = Path.GetFullPath(Path.Combine(baselineRoot, relative));
                EnsureWithinRepository(
                    trustedConfiguration,
                    baselineRoot,
                    "The trusted baseline configuration");
                if (!File.Exists(trustedConfiguration) ||
                    !ProjectDiscovery.IsSafelyContained(baselineRoot, trustedConfiguration))
                {
                    throw new InvalidOperationException(
                        "The explicit repository-owned diff configuration must be a tracked regular file in the base revision.");
                }
            }
        }

        return options with
        {
            ConfigurationPath = trustedConfiguration,
            NoConfiguration = trustedConfiguration is null,
        };
    }

    private static string PackageContext(PackageInventoryItem item, string repositoryRoot) => string.Join(
        '\n',
        DiagnosticFingerprint.GetRelativePath(item.Project, repositoryRoot) ?? Path.GetFileName(item.Project),
        item.Framework.ToUpperInvariant(),
        item.RuntimeIdentifier?.ToUpperInvariant() ?? string.Empty,
        item.Id.ToUpperInvariant());

    private static (
        PackageMedicConfiguration Configuration,
        string? Path,
        string Directory,
        string? Sha256) LoadConfiguration(
        CliOptions options,
        string repositoryRoot)
    {
        if (options.NoConfiguration)
        {
            return (PackageMedicConfiguration.Default, null, repositoryRoot, null);
        }

        var configurationPath = options.ConfigurationPath is null
            ? FindAutomaticConfiguration(options.Path, repositoryRoot)
            : Path.GetFullPath(options.ConfigurationPath);
        if (configurationPath is null)
        {
            return (PackageMedicConfiguration.Default, null, repositoryRoot, null);
        }

        var loaded = PackageMedicConfigurationLoader.LoadWithSha256(configurationPath);
        return (
            loaded.Configuration,
            configurationPath,
            Path.GetDirectoryName(configurationPath) ?? repositoryRoot,
            loaded.Sha256);
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
        var paths = new (string Option, string Path)[]
        {
            ("--output", options.OutputPath ?? string.Empty),
            ("--sarif-output", options.SarifOutputPath ?? string.Empty),
            ("--sbom-output", options.SbomOutputPath ?? string.Empty),
            ("--provenance-output", options.ProvenanceOutputPath ?? string.Empty),
        }
        .Where(item => item.Path.Length > 0)
        .Select(item => (item.Option, Path: Path.GetFullPath(item.Path)))
        .ToArray();

        for (var first = 0; first < paths.Length; first++)
        {
            for (var second = first + 1; second < paths.Length; second++)
            {
                if (PathsEqual(paths[first].Path, paths[second].Path))
                {
                    throw new ArgumentException(
                        $"{paths[first].Option} and {paths[second].Option} must use different paths.");
                }
            }
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
          package-medic sbom [path] --output <file> [options]
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
          --sbom-output <path>         Also write a deterministic CycloneDX 1.7 NuGet SBOM
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
          --provenance-output <path>  Write unsigned in-toto evidence for verified immutable diff
          --baseline and --fail-on-new are intentionally not accepted by diff.
          --no-restore requires usable assets files tracked in both compared trees.

        Verified experiments (diff and simulate):
          --verify restore|build|test  Opt into ordered verification; test implies build+restore
          --build-timeout <seconds>   Per-build-target timeout, 1-3600 (default: 900)
          --test-timeout <seconds>    Per-test-project timeout, 1-3600 (default: 1200)
          --verification-configuration <name>
                                       Build configuration (default: Release)
          Build and test verification execute repository-controlled code only in independent,
          disposable commit snapshots with separate caches and a minimal process environment.

        Dependency Time Machine:
          Restore-validates one exact package version in two independent snapshots of HEAD.
          Requires a clean Git worktree and one unambiguous direct/central declaration.
          --format text|json           Human or schema-versioned simulation report
          --output, -o <path>          Write the simulation report atomically
          --audit / --deprecated       Include matching risk deltas when both analyses complete
          --credential-env <name>      Explicitly inherit and redact one private-feed variable;
                                       repeat for additional variables
          Build and tests are not evaluated unless --verify requests them. Runtime compatibility
          is never claimed. Restore may contact configured package sources.
          --no-restore, baselines, --fail-on-new, and SARIF are intentionally unavailable.

        CycloneDX SBOM:
          Generates deterministic CycloneDX 1.7 JSON from one complete dependency analysis.
          `sbom` requires --output and does not write a second human/JSON scan report.

        Exit codes:
          0  Analysis completed below the configured thresholds
          1  A configured gate was reached or a simulated candidate was rejected
          2  Usage, restore, configuration, or analysis error

        PackageMedic 0.6 remains read-only. clean only supports --dry-run.
        """;

    private sealed record PreparedAnalysis(
        AnalysisResult Result,
        bool HasOperationalError,
        AnalysisReportContext Context,
        DiscoveryResult Discovery,
        IReadOnlyList<EvaluatedProject> EvaluatedProjects,
        string? ConfigurationSha256,
        RestoreExecutionResult? Restore);
}
