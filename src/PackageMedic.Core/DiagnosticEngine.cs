namespace PackageMedic.Core;

public sealed class DiagnosticEngine
{
    public IReadOnlyList<Diagnostic> Analyze(IReadOnlyList<ProjectAnalysis> projects, IEnumerable<Diagnostic>? initialDiagnostics = null)
    {
        var diagnostics = new List<Diagnostic>();
        if (initialDiagnostics is not null)
        {
            diagnostics.AddRange(initialDiagnostics);
        }

        diagnostics.AddRange(FindUnusedCentralVersions(projects));
        diagnostics.AddRange(FindVersionDrift(projects));
        diagnostics.AddRange(FindCentralBypasses(projects));
        diagnostics.AddRange(FindDuplicateCentralVersions(projects));
        diagnostics.AddRange(FindFloatingPackageVersions(projects));
        diagnostics.AddRange(projects.SelectMany(project => project.AssetDiagnostics));

        return diagnostics
            .DistinctBy(DiagnosticIdentity)
            .OrderByDescending(item => item.Severity)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ThenBy(item => item.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Line)
            .ThenBy(item => item.Project, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<Diagnostic> FindUnusedCentralVersions(IReadOnlyList<ProjectAnalysis> projects)
    {
        var directPackageIds = projects.ToDictionary(
            project => project,
            project => project.DirectPackages.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase));
        var occurrences = projects.SelectMany(project => project.CentralVersions.Select(version => (Project: project, Version: version)));
        foreach (var group in occurrences.GroupBy(
                     item => (File: item.Version.SourceFile.ToUpperInvariant(), item.Version.Line, Id: item.Version.Id.ToUpperInvariant(), item.Version.Version)))
        {
            var affectedProjects = group.Select(item => item.Project).DistinctBy(project => project.ProjectPath).ToArray();
            var id = group.First().Version.Id;
            var isDirectlyUsed = affectedProjects.Any(project => directPackageIds[project].Contains(id));
            var isTransitivelyPinned = affectedProjects.Any(project =>
                project.CentralPackageTransitivePinningEnabled && project.ResolvedPackages.Contains(id));
            if (isDirectlyUsed || isTransitivelyPinned)
            {
                continue;
            }

            var item = group.First().Version;
            var projectNames = string.Join(", ", affectedProjects.Select(project => project.Name).Order(StringComparer.OrdinalIgnoreCase));
            yield return new Diagnostic(
                "PM001",
                DiagnosticSeverity.Warning,
                "Central package version is not used",
                $"No PackageReference uses '{id}' in the projects affected by this evaluated central item.",
                projectNames,
                item.SourceFile,
                item.Line,
                $"PackageVersion {id} {item.Version}; affected projects: {projectNames}.",
                "Remove this PackageVersion after reviewing the affected projects and all relevant target-framework conditions.",
                DiagnosticConfidence.High,
                PackageId: id);
        }
    }

    private static IEnumerable<Diagnostic> FindVersionDrift(IReadOnlyList<ProjectAnalysis> projects)
    {
        var explicitReferences = projects
            .Where(project => !project.ManagePackageVersionsCentrally)
            .SelectMany(project => project.DirectPackages
                .Where(package => !string.IsNullOrWhiteSpace(package.ExplicitVersion))
                .Select(package => (Project: project, Package: package)))
            .GroupBy(item => item.Package.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var packageGroup in explicitReferences)
        {
            var references = packageGroup.ToArray();
            var conflictingReferences = new HashSet<DirectPackageReference>();
            for (var leftIndex = 0; leftIndex < references.Length; leftIndex++)
            {
                var left = references[leftIndex];
                for (var rightIndex = leftIndex + 1; rightIndex < references.Length; rightIndex++)
                {
                    var right = references[rightIndex];
                    if (left.Project.ProjectPath.Equals(right.Project.ProjectPath, StringComparison.OrdinalIgnoreCase) ||
                        !FrameworkScopesOverlap(left.Project, left.Package, right.Project, right.Package) ||
                        VersionsAreEquivalent(left.Package.ExplicitVersion!, right.Package.ExplicitVersion!))
                    {
                        continue;
                    }

                    conflictingReferences.Add(left.Package);
                    conflictingReferences.Add(right.Package);
                }
            }

            var perProject = references
                .Where(item => conflictingReferences.Contains(item.Package))
                .GroupBy(item => item.Project.ProjectPath, StringComparer.OrdinalIgnoreCase)
                .Select(group => new
                {
                    Project = group.First().Project,
                    Versions = DistinctSemanticVersions(group.Select(item => item.Package.ExplicitVersion!)),
                    Reference = group.First().Package,
                })
                .ToArray();
            if (perProject.Length < 2)
            {
                continue;
            }

            var evidence = string.Join(
                "; ",
                perProject.OrderBy(item => item.Project.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(item => $"{item.Project.Name}: {string.Join(", ", item.Versions)}"));
            foreach (var item in perProject)
            {
                yield return new Diagnostic(
                    "PM002",
                    DiagnosticSeverity.Warning,
                    "Package version drift",
                    $"'{packageGroup.Key}' has different explicit versions across compatible target-framework scopes in projects that are not centrally managed.",
                    item.Project.ProjectPath,
                    item.Reference.SourceFile,
                    item.Reference.Line,
                    evidence,
                    "Align the explicit versions or adopt Central Package Management for this package.",
                    DiagnosticConfidence.High,
                    PackageId: packageGroup.Key);
            }
        }
    }

    private static bool FrameworkScopesOverlap(
        ProjectAnalysis leftProject,
        DirectPackageReference leftPackage,
        ProjectAnalysis rightProject,
        DirectPackageReference rightPackage)
    {
        var leftScopes = GetFrameworkScopes(leftProject, leftPackage);
        var rightScopes = GetFrameworkScopes(rightProject, rightPackage);
        return leftScopes.Count == 0 || rightScopes.Count == 0 ||
            leftScopes.Any(left => rightScopes.Contains(left, StringComparer.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> GetFrameworkScopes(
        ProjectAnalysis project,
        DirectPackageReference package)
    {
        var frameworks = string.IsNullOrWhiteSpace(package.TargetFramework)
            ? project.TargetFrameworks
            : [package.TargetFramework];
        return frameworks
            .Where(framework => !string.IsNullOrWhiteSpace(framework))
            .Select(framework => framework.Trim().Split('/', 2)[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool VersionsAreEquivalent(string left, string right) =>
        left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
        AnalysisDiffComparer.TryCompareResolvedVersions(left, right, out var comparison) && comparison == 0;

    private static IReadOnlyList<string> DistinctSemanticVersions(IEnumerable<string> versions)
    {
        var distinct = new List<string>();
        foreach (var version in versions.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!distinct.Any(existing => VersionsAreEquivalent(existing, version)))
            {
                distinct.Add(version);
            }
        }

        return distinct;
    }

    private static IEnumerable<Diagnostic> FindCentralBypasses(IReadOnlyList<ProjectAnalysis> projects)
    {
        foreach (var project in projects.Where(project => project.ManagePackageVersionsCentrally))
        {
            foreach (var package in project.DirectPackages
                         .Where(package => !string.IsNullOrWhiteSpace(package.Version) && string.IsNullOrWhiteSpace(package.VersionOverride))
                         .DistinctBy(package => (package.Id.ToUpperInvariant(), package.SourceFile.ToUpperInvariant(), package.Line, package.Version)))
            {
                yield return new Diagnostic(
                    "PM003",
                    DiagnosticSeverity.Warning,
                    "PackageReference bypasses Central Package Management",
                    $"'{package.Id}' declares Version='{package.Version}' while Central Package Management is enabled.",
                    project.ProjectPath,
                    package.SourceFile,
                    package.Line,
                    $"PackageReference {package.Id} has an explicit Version. VersionOverride was not used.",
                    "Move the version to Directory.Packages.props, or use VersionOverride when the exception is intentional.",
                    DiagnosticConfidence.High,
                    PackageId: package.Id);
            }
        }
    }

    private static IEnumerable<Diagnostic> FindDuplicateCentralVersions(IReadOnlyList<ProjectAnalysis> projects)
    {
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            foreach (var frameworkGroup in project.CentralVersions.GroupBy(item => item.TargetFramework ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var group in frameworkGroup.GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase))
                {
                    var entries = group
                        .DistinctBy(item => (item.SourceFile.ToUpperInvariant(), item.Line, item.Version))
                        .ToArray();
                    if (entries.Length < 2)
                    {
                        continue;
                    }

                    var signature = string.Join('|', entries
                        .OrderBy(item => item.SourceFile, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(item => item.Line)
                        .Select(item => $"{item.SourceFile}:{item.Line}:{item.Version}"));
                    if (!emitted.Add($"{group.Key}|{signature}"))
                    {
                        continue;
                    }

                    var first = entries[0];
                    var evidence = string.Join(
                        "; ",
                        entries.Select(item => $"{Path.GetFileName(item.SourceFile)}:{item.Line?.ToString() ?? "?"} = {item.Version}"));
                    yield return new Diagnostic(
                        "PM004",
                        DiagnosticSeverity.Error,
                        "Duplicate central package version",
                        $"Multiple effective PackageVersion items define '{group.Key}' for project '{project.Name}'.",
                        project.ProjectPath,
                        first.SourceFile,
                        first.Line,
                        evidence,
                        "Keep one unambiguous PackageVersion for this package in the effective central package scope.",
                        DiagnosticConfidence.High,
                        PackageId: group.Key);
                }
            }
        }
    }

    private static IEnumerable<Diagnostic> FindFloatingPackageVersions(IReadOnlyList<ProjectAnalysis> projects)
    {
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            foreach (var package in project.DirectPackages)
            {
                var usesOverride = !string.IsNullOrWhiteSpace(package.VersionOverride);
                var version = usesOverride ? package.VersionOverride : package.Version;
                if (!FloatingVersionDetector.IsFloating(version))
                {
                    continue;
                }

                var metadataName = usesOverride ? "VersionOverride" : "Version";
                var signature = $"PackageReference|{package.SourceFile}|{package.Line}|{package.Id}|{metadataName}|{version}";
                if (!emitted.Add(signature))
                {
                    continue;
                }

                yield return CreateFloatingVersionDiagnostic(
                    project.ProjectPath,
                    package.SourceFile,
                    package.Line,
                    "PackageReference",
                    package.Id,
                    metadataName,
                    version!);
            }

            foreach (var package in project.CentralVersions)
            {
                if (!FloatingVersionDetector.IsFloating(package.Version))
                {
                    continue;
                }

                var signature = $"PackageVersion|{package.SourceFile}|{package.Line}|{package.Id}|Version|{package.Version}";
                if (!emitted.Add(signature))
                {
                    continue;
                }

                yield return CreateFloatingVersionDiagnostic(
                    project.ProjectPath,
                    package.SourceFile,
                    package.Line,
                    "PackageVersion",
                    package.Id,
                    "Version",
                    package.Version);
            }
        }
    }

    private static Diagnostic CreateFloatingVersionDiagnostic(
        string project,
        string sourceFile,
        int? line,
        string itemName,
        string packageId,
        string metadataName,
        string version) => new(
            "PM006",
            DiagnosticSeverity.Warning,
            "Package uses a floating NuGet version",
            $"'{packageId}' uses {metadataName}='{version}', which can resolve to a different package as new versions are published.",
            project,
            sourceFile,
            line,
            $"{itemName} {packageId} has floating {metadataName}='{version}'.",
            "Pin an exact version or an intentionally bounded non-floating range, then review the dependency update policy.",
            DiagnosticConfidence.High,
            PackageId: packageId);

    private static string DiagnosticIdentity(Diagnostic item) =>
        $"{item.Code}|{item.OriginalCode}|{item.Project}|{item.File}|{item.Line}|{item.Evidence}";
}
