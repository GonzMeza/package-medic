namespace PackageMedic.Core;

public sealed record DependencyPathSegment(string PackageId, string ResolvedVersion);

public sealed record PackageDependencyPath(
    string Project,
    string Framework,
    string? RuntimeIdentifier,
    string PackageId,
    string ResolvedVersion,
    string RootPackageId,
    string RootResolvedVersion,
    IReadOnlyList<DependencyPathSegment> Path,
    IReadOnlyList<string> AlternativeRootPackageIds);

public enum DependencyImpactViolationKind
{
    PackageDowngrade,
    DirectToTransitive,
    AddedPackageBudgetExceeded,
    AddedTransitiveBudgetExceeded,
    PackageSourceChanged,
    PackageSourceNotAllowed,
    PackageSourceUnknown,
    PackageContentChanged,
    PackageSourceMappingRequired,
    LockedModeRequired,
}

public sealed record PackageImpact(
    PackageChangeKind Kind,
    string Project,
    string Framework,
    string? RuntimeIdentifier,
    string PackageId,
    string? BeforeVersion,
    string? AfterVersion,
    PackageDependencyKind DependencyKind,
    string? RootPackageId,
    IReadOnlyList<DependencyPathSegment> DependencyPath,
    string? SourceBefore,
    string? SourceAfter,
    string? ContentHashBefore,
    string? ContentHashAfter,
    bool? SignaturePresent);

public sealed record DependencyImpactViolation(
    string Code,
    DependencyImpactViolationKind Kind,
    string Message,
    string SuggestedAction,
    string? Project = null,
    string? Framework = null,
    string? PackageId = null,
    string? RootPackageId = null,
    IReadOnlyList<DependencyPathSegment>? DependencyPath = null);

public sealed record DependencyImpactSummary(
    int ChangedPackages,
    int AddedDirectPackages,
    int AddedTransitivePackages,
    int RemovedPackages,
    int Upgrades,
    int Downgrades,
    int DirectToTransitive,
    int TransitiveToDirect,
    int SourceChanges,
    int ContentChanges,
    int MaximumBlastRadius,
    int Violations);

public sealed record DependencyImpactReport(
    DependencyImpactSummary Summary,
    IReadOnlyList<PackageImpact> Packages,
    IReadOnlyList<DependencyImpactViolation> Violations,
    ConfiguredImpactPolicy Policy)
{
    public bool GatePassed => Violations.Count == 0;
}

public static class DependencyImpactAnalyzer
{
    public static DependencyImpactReport Analyze(
        IReadOnlyList<PackageChange> changes,
        IReadOnlyList<PackageDependencyPath> baselinePaths,
        string baselineRepositoryRoot,
        IReadOnlyList<PackageDependencyPath> currentPaths,
        string currentRepositoryRoot,
        IReadOnlyList<ProjectPackageSettings> currentProjectSettings,
        ConfiguredImpactPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(baselinePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineRepositoryRoot);
        ArgumentNullException.ThrowIfNull(currentPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentRepositoryRoot);
        ArgumentNullException.ThrowIfNull(currentProjectSettings);
        ArgumentNullException.ThrowIfNull(policy);

        var beforePaths = IndexPaths(baselinePaths, baselineRepositoryRoot);
        var afterPaths = IndexPaths(currentPaths, currentRepositoryRoot);
        var impacts = changes.Select(change => CreateImpact(change, beforePaths, afterPaths))
            .OrderBy(item => item.Project, StringComparer.Ordinal)
            .ThenBy(item => item.Framework, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.RuntimeIdentifier, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.RootPackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var directToTransitive = changes
            .Where(change => change.Before?.DependencyKind == PackageDependencyKind.Direct &&
                             change.After?.DependencyKind == PackageDependencyKind.Transitive)
            .Select(ChangeIdentity)
            .ToHashSet(StringComparer.Ordinal);
        var transitiveToDirect = changes
            .Where(change => change.Before?.DependencyKind == PackageDependencyKind.Transitive &&
                             change.After?.DependencyKind == PackageDependencyKind.Direct)
            .Select(ChangeIdentity)
            .ToHashSet(StringComparer.Ordinal);
        var violations = EvaluateViolations(
            impacts,
            directToTransitive,
            currentProjectSettings,
            currentRepositoryRoot,
            policy);
        var blastRadius = impacts
            .Where(item => item.RootPackageId is not null &&
                           !item.RootPackageId.Equals(item.PackageId, StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => $"{item.Project}\n{item.Framework}\n{item.RuntimeIdentifier}\n{item.RootPackageId}", StringComparer.Ordinal)
            .Select(group => group.Count())
            .DefaultIfEmpty(0)
            .Max();
        var summary = new DependencyImpactSummary(
            impacts.Length,
            impacts.Count(item => item.Kind == PackageChangeKind.Added && item.DependencyKind == PackageDependencyKind.Direct),
            impacts.Count(item => item.Kind == PackageChangeKind.Added && item.DependencyKind == PackageDependencyKind.Transitive),
            impacts.Count(item => item.Kind == PackageChangeKind.Removed),
            impacts.Count(item => item.Kind == PackageChangeKind.Upgraded),
            impacts.Count(item => item.Kind == PackageChangeKind.Downgraded),
            impacts.Count(item => directToTransitive.Contains(ImpactIdentity(item))),
            impacts.Count(item => transitiveToDirect.Contains(ImpactIdentity(item))),
            impacts.Count(item => HasSourceChange(item)),
            impacts.Count(item => HasContentChange(item)),
            blastRadius,
            violations.Count);
        return new DependencyImpactReport(summary, impacts, violations, policy);
    }

    private static PackageImpact CreateImpact(
        PackageChange change,
        IReadOnlyDictionary<string, PackageDependencyPath> beforePaths,
        IReadOnlyDictionary<string, PackageDependencyPath> afterPaths)
    {
        var package = change.After ?? change.Before
            ?? throw new InvalidOperationException("A package change must have a before or after value.");
        var selectedPaths = change.After is null ? beforePaths : afterPaths;
        var version = change.After?.ResolvedVersion ?? change.Before!.ResolvedVersion;
        var key = PathKey(package.Project, package.Framework, package.RuntimeIdentifier, package.Id, version);
        selectedPaths.TryGetValue(key, out var dependencyPath);
        IReadOnlyList<DependencyPathSegment> path = dependencyPath?.Path ??
            (package.DependencyKind == PackageDependencyKind.Direct
                ? [new DependencyPathSegment(package.Id, version)]
                : []);
        return new PackageImpact(
            change.Kind,
            package.Project,
            package.Framework,
            package.RuntimeIdentifier,
            package.Id,
            change.Before?.ResolvedVersion,
            change.After?.ResolvedVersion,
            package.DependencyKind,
            dependencyPath?.RootPackageId ??
                (package.DependencyKind == PackageDependencyKind.Direct ? package.Id : null),
            path,
            change.Before?.PackageSource,
            change.After?.PackageSource,
            change.Before?.ContentHash,
            change.After?.ContentHash,
            change.After?.SignaturePresent ?? change.Before?.SignaturePresent);
    }

    private static IReadOnlyList<DependencyImpactViolation> EvaluateViolations(
        IReadOnlyList<PackageImpact> impacts,
        IReadOnlySet<string> directToTransitive,
        IReadOnlyList<ProjectPackageSettings> currentProjectSettings,
        string currentRepositoryRoot,
        ConfiguredImpactPolicy policy)
    {
        var violations = new List<DependencyImpactViolation>();
        if (policy.FailOnDowngrade)
        {
            violations.AddRange(impacts
                .Where(item => item.Kind == PackageChangeKind.Downgraded)
                .Select(item => ViolationForPackage(
                    "PMI001",
                    DependencyImpactViolationKind.PackageDowngrade,
                    item,
                    $"{item.PackageId} was downgraded from {item.BeforeVersion} to {item.AfterVersion}.",
                    "Confirm the downgrade is intentional and does not reintroduce fixed defects or vulnerabilities.")));
        }

        if (policy.FailOnDirectToTransitive)
        {
            violations.AddRange(impacts
                .Where(item => directToTransitive.Contains(ImpactIdentity(item)))
                .Select(item => ViolationForPackage(
                    "PMI002",
                    DependencyImpactViolationKind.DirectToTransitive,
                    item,
                    $"{item.PackageId} is no longer controlled as a direct dependency.",
                    "Review whether the direct reference or central pin was intentionally removed.")));
        }

        var addedPackages = impacts.Count(item => item.Kind == PackageChangeKind.Added);
        if (policy.MaxAddedPackages is { } maxPackages && addedPackages > maxPackages)
        {
            violations.Add(new DependencyImpactViolation(
                "PMI003",
                DependencyImpactViolationKind.AddedPackageBudgetExceeded,
                $"The dependency change adds {addedPackages} packages; the configured limit is {maxPackages}.",
                "Review the dependency blast radius or raise the limit with an explicit policy change."));
        }

        var addedTransitive = impacts.Count(item =>
            item.Kind == PackageChangeKind.Added && item.DependencyKind == PackageDependencyKind.Transitive);
        if (policy.MaxAddedTransitivePackages is { } maxTransitive && addedTransitive > maxTransitive)
        {
            violations.Add(new DependencyImpactViolation(
                "PMI004",
                DependencyImpactViolationKind.AddedTransitiveBudgetExceeded,
                $"The dependency change adds {addedTransitive} transitive packages; the configured limit is {maxTransitive}.",
                "Review the responsible direct package paths or raise the limit with an explicit policy change."));
        }

        if (policy.FailOnSourceChange)
        {
            violations.AddRange(impacts
                .Where(HasSourceChange)
                .Select(item => ViolationForPackage(
                    "PMI005",
                    DependencyImpactViolationKind.PackageSourceChanged,
                    item,
                    $"{item.PackageId} changed package source from '{DisplayEvidence(item.SourceBefore)}' to '{DisplayEvidence(item.SourceAfter)}'.",
                    "Verify the new source is trusted and that Package Source Mapping permits it intentionally.")));
        }

        if (policy.FailOnContentChange)
        {
            violations.AddRange(impacts
                .Where(HasContentChange)
                .Select(item => ViolationForPackage(
                    "PMI010",
                    DependencyImpactViolationKind.PackageContentChanged,
                    item,
                    $"{item.PackageId} kept the same identity but its package content hash changed.",
                    "Treat the package source as potentially compromised; verify the feed and restore the expected immutable artifact.")));
        }

        if (policy.AllowedSources.Count > 0)
        {
            foreach (var item in impacts.Where(item => item.Kind != PackageChangeKind.Removed))
            {
                if (string.IsNullOrWhiteSpace(item.SourceAfter))
                {
                    violations.Add(ViolationForPackage(
                        "PMI006",
                        DependencyImpactViolationKind.PackageSourceUnknown,
                        item,
                        $"The source of {item.PackageId} could not be established.",
                        "Restore from an allowed source with package metadata available, then rerun PackageMedic."));
                }
                else if (!policy.AllowedSources.Contains(NormalizeSource(item.SourceAfter), StringComparer.OrdinalIgnoreCase))
                {
                    violations.Add(ViolationForPackage(
                        "PMI007",
                        DependencyImpactViolationKind.PackageSourceNotAllowed,
                        item,
                        $"{item.PackageId} came from source '{item.SourceAfter}', which is not allowed by policy.",
                        "Use an approved package source or update the allowlist through review."));
                }
            }
        }

        foreach (var settings in currentProjectSettings)
        {
            var project = DiagnosticFingerprint.GetRelativePath(settings.Project, currentRepositoryRoot)
                ?? Path.GetFileName(settings.Project);
            if (policy.RequirePackageSourceMapping &&
                settings.PackageSourceCount > 1 &&
                !settings.PackageSourceMappingEnabled)
            {
                violations.Add(new DependencyImpactViolation(
                    "PMI008",
                    DependencyImpactViolationKind.PackageSourceMappingRequired,
                    $"{project} restores from {settings.PackageSourceCount} package sources without effective repository Package Source Mapping.",
                    "Configure repository-owned package sources and mapping patterns so every direct and transitive package is constrained intentionally.",
                    project));
            }

            if (policy.RequireLockedMode && (!settings.RestoreLockedMode || !settings.LockFileAvailable))
            {
                violations.Add(new DependencyImpactViolation(
                    "PMI009",
                    DependencyImpactViolationKind.LockedModeRequired,
                    $"{project} does not have a valid in-repository NuGet lock file with RestoreLockedMode enabled.",
                    "Commit a valid project lock file inside the analysis root and enable locked restore for deterministic CI.",
                    project));
            }
        }

        return violations
            .DistinctBy(item => $"{item.Code}|{item.Project}|{item.Framework}|{item.PackageId}|{item.Message}", StringComparer.Ordinal)
            .OrderBy(item => item.Code, StringComparer.Ordinal)
            .ThenBy(item => item.Project, StringComparer.Ordinal)
            .ThenBy(item => item.Framework, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static DependencyImpactViolation ViolationForPackage(
        string code,
        DependencyImpactViolationKind kind,
        PackageImpact item,
        string message,
        string suggestedAction) => new(
            code,
            kind,
            message,
            suggestedAction,
            item.Project,
            item.Framework,
            item.PackageId,
            item.RootPackageId,
            item.DependencyPath);

    private static string ChangeIdentity(PackageChange change)
    {
        var package = change.After ?? change.Before!;
        return Identity(
            package.Project,
            package.Framework,
            package.RuntimeIdentifier,
            package.Id);
    }

    private static string ImpactIdentity(PackageImpact impact) => Identity(
        impact.Project,
        impact.Framework,
        impact.RuntimeIdentifier,
        impact.PackageId);

    private static string Identity(
        string project,
        string framework,
        string? runtimeIdentifier,
        string packageId) => string.Join(
            '\n',
            OperatingSystem.IsWindows() ? project.ToUpperInvariant() : project,
            framework.ToUpperInvariant(),
            runtimeIdentifier?.ToUpperInvariant() ?? string.Empty,
            packageId.ToUpperInvariant());

    private static bool HasSourceChange(PackageImpact impact) =>
        impact.BeforeVersion is not null &&
        impact.AfterVersion is not null &&
        !string.Equals(
            NormalizeOptionalSource(impact.SourceBefore),
            NormalizeOptionalSource(impact.SourceAfter),
            StringComparison.OrdinalIgnoreCase);

    private static bool HasContentChange(PackageImpact impact) =>
        !string.IsNullOrWhiteSpace(impact.BeforeVersion) &&
        impact.BeforeVersion.Equals(impact.AfterVersion, StringComparison.OrdinalIgnoreCase) &&
        (!string.IsNullOrWhiteSpace(impact.ContentHashBefore) ||
         !string.IsNullOrWhiteSpace(impact.ContentHashAfter)) &&
        !string.Equals(impact.ContentHashBefore, impact.ContentHashAfter, StringComparison.Ordinal);

    private static string? NormalizeOptionalSource(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : NormalizeSource(value);

    private static string DisplayEvidence(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value;

    private static Dictionary<string, PackageDependencyPath> IndexPaths(
        IReadOnlyList<PackageDependencyPath> paths,
        string repositoryRoot) => paths
        .Select(item => item with
        {
            Project = DiagnosticFingerprint.GetRelativePath(item.Project, repositoryRoot) ?? Path.GetFileName(item.Project),
        })
        .OrderBy(item => item.Path.Count)
        .ThenBy(item => item.RootPackageId, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => string.Join('/', item.Path.Select(segment => segment.PackageId)), StringComparer.OrdinalIgnoreCase)
        .GroupBy(
            item => PathKey(
                item.Project,
                item.Framework,
                item.RuntimeIdentifier,
                item.PackageId,
                item.ResolvedVersion),
            StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

    private static string PathKey(
        string project,
        string framework,
        string? runtimeIdentifier,
        string packageId,
        string resolvedVersion) => string.Join(
            '\n',
            OperatingSystem.IsWindows() ? project.ToUpperInvariant() : project,
            framework.ToUpperInvariant(),
            runtimeIdentifier?.ToUpperInvariant() ?? string.Empty,
            packageId.ToUpperInvariant(),
            resolvedVersion.ToUpperInvariant());

    internal static string NormalizeSource(string value)
    {
        var source = value.Trim();
        if (source.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            return "local";
        }

        return Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
               uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri.TrimEnd('/')
            : source;
    }
}
