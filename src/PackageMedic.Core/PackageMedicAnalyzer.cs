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

    public PackageMedicAnalyzer()
        : this(new ProcessRunner())
    {
    }

    public PackageMedicAnalyzer(IProcessRunner processRunner)
    {
        discovery = new ProjectDiscovery();
        restoreRunner = new RestoreRunner(processRunner);
        evaluator = new MsBuildProjectEvaluator(processRunner);
        assetsReader = new AssetsFileReader();
        diagnosticEngine = new DiagnosticEngine();
    }

    public async Task<AnalysisOutcome> AnalyzeAsync(
        string? requestedPath,
        bool noRestore,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var discovered = discovery.Discover(requestedPath);
        var initialDiagnostics = new List<Diagnostic>();
        var analysisErrors = new List<string>();

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

        var projects = new List<ProjectAnalysis>();
        foreach (var projectPath in discovered.Projects)
        {
            progress?.Invoke($"Evaluating {Path.GetFileName(projectPath)}...");
            try
            {
                var evaluated = await evaluator.EvaluateAsync(projectPath, cancellationToken).ConfigureAwait(false);
                var assets = assetsReader.Read(evaluated.AssetsFile, projectPath);
                projects.Add(new ProjectAnalysis
                {
                    ProjectPath = evaluated.ProjectPath,
                    ManagePackageVersionsCentrally = evaluated.ManagePackageVersionsCentrally,
                    CentralPackageTransitivePinningEnabled = evaluated.CentralPackageTransitivePinningEnabled,
                    TargetFrameworks = evaluated.TargetFrameworks,
                    DirectPackages = evaluated.DirectPackages,
                    CentralVersions = evaluated.CentralVersions,
                    ResolvedPackages = assets.ResolvedPackages,
                    TransitivePackages = assets.TransitivePackages,
                    AssetDiagnostics = assets.Diagnostics,
                });
            }
            catch (Exception exception) when (exception is InvalidOperationException or FileNotFoundException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                analysisErrors.Add(exception.Message);
            }
        }

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
            analysisErrors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        return new AnalysisOutcome(result, analysisErrors.Count > 0);
    }
}
