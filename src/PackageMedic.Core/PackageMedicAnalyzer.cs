using System.Reflection;

namespace PackageMedic.Core;

public sealed class PackageMedicAnalyzer
{
    public static string Version { get; } =
        typeof(PackageMedicAnalyzer).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+', 2)[0]
        ?? throw new InvalidOperationException("PackageMedic version metadata is missing.");

    private readonly ProjectDiscovery discovery;
    private readonly RestoreRunner restoreRunner;
    private readonly MsBuildProjectEvaluator evaluator;
    private readonly AssetsFileReader assetsReader;
    private readonly DiagnosticEngine diagnosticEngine;
    private readonly int maxDegreeOfParallelism;

    public PackageMedicAnalyzer()
        : this(new ProcessRunner(), AnalysisExecutionOptions.Default)
    {
    }

    public PackageMedicAnalyzer(IProcessRunner processRunner)
        : this(processRunner, AnalysisExecutionOptions.Default)
    {
    }

    public PackageMedicAnalyzer(IProcessRunner processRunner, AnalysisExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(executionOptions);
        executionOptions.Validate();
        discovery = new ProjectDiscovery();
        restoreRunner = new RestoreRunner(
            processRunner,
            executionOptions.RestoreTimeout,
            executionOptions.MaxDegreeOfParallelism);
        evaluator = new MsBuildProjectEvaluator(
            processRunner,
            executionOptions.MsBuildEvaluationTimeout,
            executionOptions.MaxDegreeOfParallelism);
        assetsReader = new AssetsFileReader();
        diagnosticEngine = new DiagnosticEngine();
        maxDegreeOfParallelism = executionOptions.MaxDegreeOfParallelism;
    }

    public async Task<AnalysisOutcome> AnalyzeAsync(
        string? requestedPath,
        bool noRestore,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default,
        string? containmentRoot = null,
        bool forceEvaluateRestore = false,
        string? packagesDirectory = null,
        RestoreExecutionResult? preparedRestore = null,
        string? verificationConfiguration = null)
    {
        var discovered = discovery.Discover(requestedPath, containmentRoot);
        return await AnalyzeAsync(
            discovered,
            noRestore,
            progress,
            cancellationToken,
            containmentRoot,
            forceEvaluateRestore,
            packagesDirectory,
            preparedRestore,
            verificationConfiguration).ConfigureAwait(false);
    }

    public async Task<AnalysisOutcome> AnalyzeAsync(
        DiscoveryResult discovered,
        bool noRestore,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default,
        string? containmentRoot = null,
        bool forceEvaluateRestore = false,
        string? packagesDirectory = null,
        RestoreExecutionResult? preparedRestore = null,
        string? verificationConfiguration = null)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        var trustedRoot = Path.GetFullPath(containmentRoot ??
            (Directory.Exists(discovered.Target)
                ? discovered.Target
                : Path.GetDirectoryName(discovered.Target)!));
        var initialDiagnostics = new List<Diagnostic>();
        var analysisErrors = new List<string>(discovered.Errors);
        RestoreExecutionResult? restoreEvidence = preparedRestore;

        if (preparedRestore is not null)
        {
            initialDiagnostics.AddRange(preparedRestore.Diagnostics);
            analysisErrors.AddRange(preparedRestore.Errors);
        }
        else if (!noRestore)
        {
            restoreEvidence = await restoreRunner.RestoreDetailedAsync(
                discovered,
                progress,
                cancellationToken,
                forceEvaluateRestore,
                packagesDirectory,
                verificationConfiguration).ConfigureAwait(false);
            initialDiagnostics.AddRange(restoreEvidence.Diagnostics);
            analysisErrors.AddRange(restoreEvidence.Errors);
        }
        else
        {
            progress?.Invoke("Skipping restore because --no-restore was specified.");
        }

        var progressGate = new object();
        var lineLocator = new XmlItemLineLocator();
        var evaluatedProjects = new ProjectEvaluationOutcome?[discovered.Projects.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, discovered.Projects.Count),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = maxDegreeOfParallelism,
            },
            async (index, token) =>
            {
                evaluatedProjects[index] = await EvaluateProjectAsync(
                    discovered.Projects[index],
                    lineLocator,
                    progress,
                    progressGate,
                    trustedRoot,
                    packagesDirectory,
                    verificationConfiguration,
                    token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        var projects = evaluatedProjects
            .Where(item => item?.Project is not null)
            .Select(item => item!.Project!)
            .ToList();
        analysisErrors.AddRange(evaluatedProjects
            .Where(item => item?.Error is not null)
            .Select(item => item!.Error!));

        var diagnostics = diagnosticEngine.Analyze(projects, initialDiagnostics);
        var summary = new ScanSummary(
            discovered.Solutions.Count,
            discovered.Projects.Count,
            projects.Sum(project => project.DirectPackages.Select(package => package.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()),
            projects.Sum(project => project.TransitivePackages.Count),
            diagnostics.Count(item => item.Severity == DiagnosticSeverity.Error),
            diagnostics.Count(item => item.Severity == DiagnosticSeverity.Warning),
            diagnostics.Count(item => item.Severity == DiagnosticSeverity.Information));
        var result = new AnalysisResult(
            Version,
            discovered.Target,
            summary,
            diagnostics,
            analysisErrors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())
        {
            Packages = projects
                .SelectMany(project => project.PackageInventory)
                .OrderBy(item => item.Project, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Framework, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.RuntimeIdentifier, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.DependencyKind)
                .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.ResolvedVersion, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ProjectSettings = projects
                .Select(project => new ProjectPackageSettings(
                    project.ProjectPath,
                    project.ManagePackageVersionsCentrally,
                    project.CentralPackageTransitivePinningEnabled)
                {
                    PackageSourceCount = project.PackageSourceCount,
                    PackageSourceMappingEnabled = project.PackageSourceMappingEnabled,
                    RestorePackagesWithLockFile = project.RestorePackagesWithLockFile,
                    RestoreLockedMode = project.RestoreLockedMode,
                    LockFileAvailable = project.LockFileAvailable,
                })
                .OrderBy(item => item.Project, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            DependencyPaths = projects
                .SelectMany(project => project.DependencyPaths)
                .OrderBy(item => item.Project, OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal)
                .ThenBy(item => item.Framework, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.RuntimeIdentifier, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.PackageId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };
        return new AnalysisOutcome(result, analysisErrors.Count > 0)
        {
            Discovery = discovered,
            EvaluatedProjects = evaluatedProjects
                .Where(item => item?.Evaluation is not null)
                .Select(item => item!.Evaluation!)
                .ToArray(),
            Restore = restoreEvidence,
        };
    }

    internal static IReadOnlyList<PackageInventoryItem> EnrichPackageInventory(
        IReadOnlyList<PackageInventoryItem> inventory,
        EvaluatedProject project)
    {
        var directById = project.DirectPackages
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var centralById = project.CentralVersions
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        return inventory.Select(item =>
        {
            if (item.DependencyKind == PackageDependencyKind.Transitive)
            {
                return item;
            }

            var direct = SelectFrameworkScopedItem(
                directById.GetValueOrDefault(item.Id),
                item.Framework,
                reference => reference.TargetFramework);
            var central = SelectFrameworkScopedItem(
                centralById.GetValueOrDefault(item.Id),
                item.Framework,
                version => version.TargetFramework);
            if (!string.IsNullOrWhiteSpace(direct?.VersionOverride))
            {
                return item with
                {
                    RequestedVersion = direct.VersionOverride,
                    VersionSource = "override",
                    SourceFile = direct.SourceFile,
                    SourceLine = direct.Line,
                };
            }

            if (!string.IsNullOrWhiteSpace(direct?.Version))
            {
                return item with
                {
                    RequestedVersion = direct.Version,
                    VersionSource = "project",
                    SourceFile = direct.SourceFile,
                    SourceLine = direct.Line,
                };
            }

            if (!string.IsNullOrWhiteSpace(central?.Version))
            {
                return item with
                {
                    RequestedVersion = central.Version,
                    VersionSource = "central",
                    SourceFile = central.SourceFile,
                    SourceLine = central.Line,
                };
            }

            return item with
            {
                VersionSource = "implicit",
                SourceFile = direct?.SourceFile,
                SourceLine = direct?.Line,
            };
        }).ToArray();
    }

    private async Task<ProjectEvaluationOutcome> EvaluateProjectAsync(
        string projectPath,
        XmlItemLineLocator lineLocator,
        Action<string>? progress,
        object progressGate,
        string trustedRoot,
        string? trustedPackagesDirectory,
        string? verificationConfiguration,
        CancellationToken cancellationToken)
    {
        lock (progressGate)
        {
            progress?.Invoke($"Evaluating {Path.GetFileName(projectPath)}...");
        }

        try
        {
            var evaluated = await evaluator.EvaluateAsync(
                projectPath,
                lineLocator,
                verificationConfiguration,
                cancellationToken).ConfigureAwait(false);
            var assets = assetsReader.Read(
                evaluated.AssetsFile,
                projectPath,
                trustedRoot,
                trustedPackagesDirectory);
            var inventory = EnrichPackageInventory(assets.PackageInventory, evaluated);
            return new ProjectEvaluationOutcome(new ProjectAnalysis
            {
                ProjectPath = evaluated.ProjectPath,
                ManagePackageVersionsCentrally = evaluated.ManagePackageVersionsCentrally,
                CentralPackageTransitivePinningEnabled = evaluated.CentralPackageTransitivePinningEnabled,
                TargetFrameworks = evaluated.TargetFrameworks,
                DirectPackages = evaluated.DirectPackages,
                CentralVersions = evaluated.CentralVersions,
                ResolvedPackages = assets.ResolvedPackages,
                TransitivePackages = assets.TransitivePackages,
                PackageInventory = inventory,
                DependencyPaths = DependencyGraphBuilder.BuildPaths(inventory, assets.DependencyEdges),
                PackageSourceCount = assets.PackageSourceCount,
                PackageSourceMappingEnabled = assets.PackageSourceMappingEnabled,
                RestorePackagesWithLockFile = evaluated.RestorePackagesWithLockFile,
                RestoreLockedMode = evaluated.RestoreLockedMode,
                LockFileAvailable = NuGetLockFileValidator.IsTrustedAndValid(evaluated.LockFilePath, trustedRoot),
                AssetDiagnostics = assets.Diagnostics,
            }, evaluated, null);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return new ProjectEvaluationOutcome(null, null, exception.Message);
        }
    }

    private static T? SelectFrameworkScopedItem<T>(
        IReadOnlyList<T>? candidates,
        string assetsFramework,
        Func<T, string?> frameworkSelector)
        where T : class
    {
        if (candidates is null || candidates.Count == 0)
        {
            return null;
        }

        var exact = candidates
            .Where(candidate => string.Equals(
                frameworkSelector(candidate)?.Trim(),
                assetsFramework.Trim(),
                StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (exact.Length != 0)
        {
            return exact.Length == 1 ? exact[0] : null;
        }

        var normalizedAssetsFramework = NormalizeFrameworkScope(assetsFramework);
        var normalized = candidates
            .Where(candidate => frameworkSelector(candidate) is { } framework &&
                NormalizeFrameworkScope(framework).Equals(normalizedAssetsFramework, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (normalized.Length != 0)
        {
            return normalized.Length == 1 ? normalized[0] : null;
        }

        var unscoped = candidates
            .Where(candidate => string.IsNullOrWhiteSpace(frameworkSelector(candidate)))
            .Take(2)
            .ToArray();
        return unscoped.Length == 1 ? unscoped[0] : null;
    }

    internal static string NormalizeFrameworkScope(string framework)
    {
        var normalized = framework.Trim().Split('/', 2)[0].ToLowerInvariant();
        var platformSeparator = normalized.IndexOf('-');
        if (platformSeparator < 0 || platformSeparator == normalized.Length - 1)
        {
            return normalized;
        }

        var platform = normalized[(platformSeparator + 1)..];
        var platformVersionStart = -1;
        for (var index = 0; index < platform.Length; index++)
        {
            if (char.IsAsciiDigit(platform[index]))
            {
                platformVersionStart = index;
                break;
            }
        }

        return platformVersionStart > 0
            ? $"{normalized[..(platformSeparator + 1)]}{platform[..platformVersionStart]}"
            : normalized;
    }

    private sealed record ProjectEvaluationOutcome(
        ProjectAnalysis? Project,
        EvaluatedProject? Evaluation,
        string? Error);
}
