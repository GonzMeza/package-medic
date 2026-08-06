namespace PackageMedic.Core;

public enum VerificationBuildTargetKind
{
    Solution,
    Project,
}

public enum VerificationTestRunnerKind
{
    VSTest,
    MicrosoftTestingPlatform,
}

public sealed record VerificationBuildTarget(
    string Path,
    VerificationBuildTargetKind Kind);

public sealed record VerificationTestProject(
    string ProjectPath,
    VerificationTestRunnerKind Runner,
    IReadOnlyList<string> TargetFrameworks,
    string? OutputType,
    string? TargetPath);

public sealed record VerificationPlan(
    IReadOnlyList<VerificationBuildTarget> BuildTargets,
    IReadOnlyList<VerificationTestProject> TestProjects);

/// <summary>
/// Creates a deterministic build and test plan from discovery and evaluated MSBuild metadata.
/// Test projects are selected only from the evaluated IsTestProject property; names, packages,
/// output paths, and SDK declarations are deliberately not used as heuristics.
/// </summary>
public sealed class VerificationPlanBuilder
{
    internal const int MaximumBuildTargets = ProjectDiscovery.MaximumProjects;
    internal const int MaximumTestProjects = ProjectDiscovery.MaximumProjects;

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public VerificationPlan Build(
        DiscoveryResult discovery,
        IReadOnlyList<EvaluatedProject> evaluatedProjects)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(evaluatedProjects);

        var projects = NormalizeUniquePaths(discovery.Projects, nameof(discovery.Projects));
        if (projects.Length == 0)
        {
            throw new ArgumentException("Verification requires at least one discovered project.", nameof(discovery));
        }

        if (projects.Length > ProjectDiscovery.MaximumProjects)
        {
            throw new InvalidDataException(
                $"The verification plan contains more than the {ProjectDiscovery.MaximumProjects}-project safety limit.");
        }

        if (evaluatedProjects.Count > ProjectDiscovery.MaximumProjects)
        {
            throw new InvalidDataException(
                $"The verification plan contains more than the {ProjectDiscovery.MaximumProjects}-project metadata safety limit.");
        }

        var projectSet = projects.ToHashSet(PathComparer);
        var evaluatedByPath = BuildEvaluationMap(evaluatedProjects, projectSet);
        var missingEvaluations = projects
            .Where(project => !evaluatedByPath.ContainsKey(project))
            .ToArray();
        if (missingEvaluations.Length > 0)
        {
            throw new InvalidDataException(
                $"MSBuild metadata is missing for {missingEvaluations.Length} discovered project(s), including '{missingEvaluations[0]}'.");
        }

        var buildTargets = SelectBuildTargets(discovery, projectSet);
        var testProjects = projects
            .Select(project => evaluatedByPath[project])
            .Where(project => project.IsTestProject)
            .Select(project => new VerificationTestProject(
                Path.GetFullPath(project.ProjectPath),
                project.IsTestingPlatformApplication
                    ? VerificationTestRunnerKind.MicrosoftTestingPlatform
                    : VerificationTestRunnerKind.VSTest,
                project.TargetFrameworks
                    .Where(framework => !string.IsNullOrWhiteSpace(framework))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                NullIfWhiteSpace(project.OutputType),
                NormalizeOptionalPath(project.TargetPath, Path.GetDirectoryName(project.ProjectPath)!)))
            .OrderBy(project => project.ProjectPath, PathComparer)
            .ToArray();

        if (testProjects.Length > MaximumTestProjects)
        {
            throw new InvalidDataException(
                $"The verification plan contains more than the {MaximumTestProjects}-test-project safety limit.");
        }

        return new VerificationPlan(buildTargets, testProjects);
    }

    private static IReadOnlyDictionary<string, EvaluatedProject> BuildEvaluationMap(
        IReadOnlyList<EvaluatedProject> evaluatedProjects,
        IReadOnlySet<string> discoveredProjects)
    {
        var result = new Dictionary<string, EvaluatedProject>(PathComparer);
        foreach (var evaluation in evaluatedProjects)
        {
            ArgumentNullException.ThrowIfNull(evaluation);
            var projectPath = NormalizeAbsolutePath(evaluation.ProjectPath, nameof(evaluatedProjects));
            ArgumentNullException.ThrowIfNull(evaluation.TargetFrameworks);
            if (evaluation.TargetFrameworks.Count > MsBuildProjectEvaluator.MaximumTargetFrameworksPerProject)
            {
                throw new InvalidDataException(
                    $"Project '{projectPath}' declares more than the " +
                    $"{MsBuildProjectEvaluator.MaximumTargetFrameworksPerProject}-target-framework safety limit.");
            }

            if (!discoveredProjects.Contains(projectPath))
            {
                throw new InvalidDataException(
                    $"MSBuild metadata was provided for undiscovered project '{projectPath}'.");
            }

            if (!result.TryAdd(projectPath, evaluation with { ProjectPath = projectPath }))
            {
                throw new InvalidDataException(
                    $"MSBuild metadata was provided more than once for project '{projectPath}'.");
            }
        }

        return result;
    }

    private static VerificationBuildTarget[] SelectBuildTargets(
        DiscoveryResult discovery,
        IReadOnlySet<string> discoveredProjects)
    {
        var solutions = NormalizeUniquePaths(discovery.Solutions, nameof(discovery.Solutions));
        IEnumerable<VerificationBuildTarget> selected;
        if (solutions.Length == 1)
        {
            var solution = solutions[0];
            var omittedProjects = NormalizeUniquePaths(discovery.RestoreTargets, nameof(discovery.RestoreTargets))
                .Where(path => !PathComparer.Equals(path, solution))
                .Select(path =>
                {
                    if (!discoveredProjects.Contains(path) || !IsProject(path))
                    {
                        throw new InvalidDataException(
                            $"Single-solution verification target '{path}' is not a discovered C# project.");
                    }

                    return new VerificationBuildTarget(path, VerificationBuildTargetKind.Project);
                })
                .OrderBy(target => target.Path, PathComparer);
            selected = new[]
                {
                    new VerificationBuildTarget(solution, VerificationBuildTargetKind.Solution),
                }
                .Concat(omittedProjects);
        }
        else
        {
            selected = discoveredProjects
                .OrderBy(path => path, PathComparer)
                .Select(path => new VerificationBuildTarget(path, VerificationBuildTargetKind.Project));
        }

        var targets = selected
            .DistinctBy(target => target.Path, PathComparer)
            .ToArray();
        if (targets.Length > MaximumBuildTargets)
        {
            throw new InvalidDataException(
                $"The verification plan contains more than the {MaximumBuildTargets}-build-target safety limit.");
        }

        return targets;
    }

    private static string[] NormalizeUniquePaths(IEnumerable<string> paths, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return paths
            .Select(path => NormalizeAbsolutePath(path, parameterName))
            .Distinct(PathComparer)
            .OrderBy(path => path, PathComparer)
            .ToArray();
    }

    private static string NormalizeAbsolutePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Verification paths must be absolute.", parameterName);
        }

        return Path.GetFullPath(path);
    }

    private static string? NormalizeOptionalPath(string? path, string projectDirectory)
    {
        var value = NullIfWhiteSpace(path);
        return value is null
            ? null
            : Path.IsPathFullyQualified(value)
                ? Path.GetFullPath(value)
                : Path.GetFullPath(value, projectDirectory);
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsProject(string path) =>
        Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase);
}
