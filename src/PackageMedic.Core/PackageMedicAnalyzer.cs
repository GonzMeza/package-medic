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
        string? containmentRoot = null)
    {
        var discovered = discovery.Discover(requestedPath, containmentRoot);
        var initialDiagnostics = new List<Diagnostic>();
        var analysisErrors = new List<string>(discovered.Errors);

        if (!noRestore)
        {
            var restore = await restoreRunner.RestoreAsync(discovered, progress, cancellationToken).ConfigureAwait(false);
            initialDiagnostics.AddRange(restore.Diagnostics);
            analysisErrors.AddRange(restore.Errors);
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
                    project.CentralPackageTransitivePinningEnabled))
                .OrderBy(item => item.Project, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };
        return new AnalysisOutcome(result, analysisErrors.Count > 0);
    }

    private static IReadOnlyList<PackageInventoryItem> EnrichPackageInventory(
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

            var direct = directById.GetValueOrDefault(item.Id)?.FirstOrDefault(reference =>
                reference.TargetFramework is null || FrameworkMatches(reference.TargetFramework, item.Framework));
            var central = centralById.GetValueOrDefault(item.Id)?.FirstOrDefault(version =>
                version.TargetFramework is null || FrameworkMatches(version.TargetFramework, item.Framework));
            if (!string.IsNullOrWhiteSpace(direct?.VersionOverride))
            {
                return item with { RequestedVersion = direct.VersionOverride, VersionSource = "override" };
            }

            if (!string.IsNullOrWhiteSpace(direct?.Version))
            {
                return item with { RequestedVersion = direct.Version, VersionSource = "project" };
            }

            if (!string.IsNullOrWhiteSpace(central?.Version))
            {
                return item with { RequestedVersion = central.Version, VersionSource = "central" };
            }

            return item with { VersionSource = "implicit" };
        }).ToArray();
    }

    private async Task<ProjectEvaluationOutcome> EvaluateProjectAsync(
        string projectPath,
        XmlItemLineLocator lineLocator,
        Action<string>? progress,
        object progressGate,
        CancellationToken cancellationToken)
    {
        lock (progressGate)
        {
            progress?.Invoke($"Evaluating {Path.GetFileName(projectPath)}...");
        }

        try
        {
            var evaluated = await evaluator.EvaluateAsync(projectPath, lineLocator, cancellationToken).ConfigureAwait(false);
            var assets = assetsReader.Read(evaluated.AssetsFile, projectPath);
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
                PackageInventory = EnrichPackageInventory(assets.PackageInventory, evaluated),
                AssetDiagnostics = assets.Diagnostics,
            }, null);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return new ProjectEvaluationOutcome(null, exception.Message);
        }
    }

    private static bool FrameworkMatches(string evaluatedFramework, string assetsFramework) =>
        evaluatedFramework.Equals(assetsFramework, StringComparison.OrdinalIgnoreCase) ||
        assetsFramework.Contains(evaluatedFramework, StringComparison.OrdinalIgnoreCase);

    private sealed record ProjectEvaluationOutcome(ProjectAnalysis? Project, string? Error);
}
