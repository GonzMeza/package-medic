using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PackageMedic.Core;

public enum DiagnosticChangeKind
{
    Added,
    Resolved,
    SeverityChanged,
}

public sealed record DiffDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Title,
    string Explanation,
    string? Project,
    string? File,
    int? Line,
    string Evidence,
    string SuggestedAction,
    DiagnosticConfidence? Confidence,
    string? OriginalCode,
    string? PackageId);

public sealed record DiagnosticChange(
    DiagnosticChangeKind Kind,
    string Fingerprint,
    DiffDiagnostic? Before,
    DiffDiagnostic? After);

public sealed record AnalysisDiffSummary(int Added, int Resolved, int SeverityChanged);

public enum PackageChangeKind
{
    Added,
    Removed,
    VersionChanged,
    DependencyKindChanged,
    Modified,
    Upgraded,
    Downgraded,
}

public enum PackageAttributeChangeKind
{
    ResolvedVersion,
    RequestedVersion,
    VersionSource,
    DependencyKind,
    PackageSource,
    ContentHash,
    SignaturePresent,
}

public sealed record DiffPackage(
    string Project,
    string Framework,
    string Id,
    string ResolvedVersion,
    PackageDependencyKind DependencyKind,
    string? RequestedVersion,
    string VersionSource,
    string? RuntimeIdentifier,
    string? PackageSource = null,
    string? ContentHash = null,
    bool? SignaturePresent = null);

public sealed record PackageChange(PackageChangeKind Kind, DiffPackage? Before, DiffPackage? After)
{
    public IReadOnlyList<PackageAttributeChangeKind> ChangedAttributes { get; init; } = [];
}

public sealed record PackageDiffSummary(
    int Added,
    int Removed,
    int Upgraded,
    int Downgraded,
    int UncomparableVersionChanges,
    int DirectToTransitive,
    int TransitiveToDirect,
    int OtherModified);

public sealed record DependencyRiskDiffSummary(
    int VulnerabilitiesIntroduced,
    int VulnerabilitiesResolved,
    int DeprecationsIntroduced,
    int DeprecationsResolved)
{
    public int VulnerabilitiesPersistent { get; init; }

    public int DeprecationsPersistent { get; init; }
}

public enum ProjectSettingsChangeKind
{
    Added,
    Removed,
    Modified,
}

public sealed record ProjectSettingsChange(
    ProjectSettingsChangeKind Kind,
    string Project,
    ProjectPackageSettings? Before,
    ProjectPackageSettings? After);

public sealed record AnalysisDiffReport(
    int SchemaVersion,
    string ToolVersion,
    string BaseReference,
    string BaseCommit,
    string CurrentTarget,
    AnalysisDiffSummary Summary,
    IReadOnlyList<DiagnosticChange> Changes)
{
    public const int CurrentSchemaVersion = 3;

    public IReadOnlyList<PackageChange> PackageChanges { get; init; } = [];

    public PackageDiffSummary PackageSummary { get; init; } = new(0, 0, 0, 0, 0, 0, 0, 0);

    public DependencyRiskDiffSummary RiskSummary { get; init; } = new(0, 0, 0, 0);

    public IReadOnlyList<ProjectSettingsChange> ProjectSettingsChanges { get; init; } = [];

    public DependencyImpactReport? Impact { get; init; }

    public bool IsComplete { get; init; } = true;

    public IReadOnlyList<string> BaselineAnalysisErrors { get; init; } = [];

    public IReadOnlyList<string> CurrentAnalysisErrors { get; init; } = [];

    public VerificationComparisonReport? Verification { get; init; }
}

/// <summary>
/// Compares diagnostics by the same repository-portable identity used by baselines and SARIF.
/// </summary>
public static class AnalysisDiffComparer
{
    /// <summary>
    /// Selects diagnostics using the exact identities used by diff comparison. Risk diagnostics
    /// such as PM007 and PM008 intentionally use package/advisory identities instead of their
    /// rendered evidence, so callers must not recompute the generic diagnostic fingerprint.
    /// </summary>
    public static IReadOnlyList<Diagnostic> SelectDiagnosticsByFingerprint(
        AnalysisResult result,
        string repositoryRoot,
        IReadOnlySet<string> fingerprints)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(fingerprints);

        var root = RepositoryRoot.Parse(repositoryRoot);
        var riskIdentities = BuildRiskIdentityMap(result, root);
        return result.Diagnostics
            .Where(diagnostic => fingerprints.Contains(ResolveIdentity(diagnostic, root, riskIdentities).Fingerprint))
            .ToArray();
    }

    public static AnalysisDiffReport Compare(
        AnalysisResult baseline,
        string baselineRepositoryRoot,
        AnalysisResult current,
        string currentRepositoryRoot,
        string baseReference,
        string baseCommit,
        ConfiguredImpactPolicy? impactPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseCommit);

        var baselineErrors = NormalizeErrors(baseline.AnalysisErrors, baselineRepositoryRoot);
        var currentErrors = NormalizeErrors(current.AnalysisErrors, currentRepositoryRoot);
        if (baselineErrors.Count > 0 || currentErrors.Count > 0)
        {
            return new AnalysisDiffReport(
                AnalysisDiffReport.CurrentSchemaVersion,
                current.Version,
                baseReference,
                baseCommit.ToLowerInvariant(),
                DiagnosticFingerprint.GetRelativePath(current.Target, currentRepositoryRoot) ?? ".",
                new AnalysisDiffSummary(0, 0, 0),
                [])
            {
                IsComplete = false,
                BaselineAnalysisErrors = baselineErrors,
                CurrentAnalysisErrors = currentErrors,
            };
        }

        var before = Index(baseline, baselineRepositoryRoot);
        var after = Index(current, currentRepositoryRoot);
        var changes = new List<DiagnosticChange>();

        foreach (var fingerprint in before.Keys.Union(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var hasBefore = before.TryGetValue(fingerprint, out var previous);
            var hasAfter = after.TryGetValue(fingerprint, out var next);
            if (!hasBefore)
            {
                changes.Add(new DiagnosticChange(DiagnosticChangeKind.Added, fingerprint, null, next));
            }
            else if (!hasAfter)
            {
                changes.Add(new DiagnosticChange(DiagnosticChangeKind.Resolved, fingerprint, previous, null));
            }
            else if (previous!.Severity != next!.Severity)
            {
                changes.Add(new DiagnosticChange(DiagnosticChangeKind.SeverityChanged, fingerprint, previous, next));
            }
        }

        var canonical = changes
            .OrderBy(change => ChangeOrder(change.Kind))
            .ThenBy(change => change.After?.Code ?? change.Before?.Code, StringComparer.Ordinal)
            .ThenBy(change => change.After?.File ?? change.Before?.File, StringComparer.Ordinal)
            .ThenBy(change => change.After?.Line ?? change.Before?.Line)
            .ThenBy(change => change.Fingerprint, StringComparer.Ordinal)
            .ToArray();
        var packageChanges = ComparePackages(
            baseline.Packages,
            baselineRepositoryRoot,
            current.Packages,
            currentRepositoryRoot);
        var projectSettingsChanges = CompareProjectSettings(
            baseline.ProjectSettings,
            baselineRepositoryRoot,
            current.ProjectSettings,
            currentRepositoryRoot);
        var impact = DependencyImpactAnalyzer.Analyze(
            packageChanges,
            baseline.DependencyPaths,
            baselineRepositoryRoot,
            current.DependencyPaths,
            currentRepositoryRoot,
            current.ProjectSettings,
            impactPolicy ?? ConfiguredImpactPolicy.Default);
        return new AnalysisDiffReport(
            AnalysisDiffReport.CurrentSchemaVersion,
            current.Version,
            baseReference,
            baseCommit.ToLowerInvariant(),
            DiagnosticFingerprint.GetRelativePath(current.Target, currentRepositoryRoot) ?? ".",
            new AnalysisDiffSummary(
                canonical.Count(change => change.Kind == DiagnosticChangeKind.Added),
                canonical.Count(change => change.Kind == DiagnosticChangeKind.Resolved),
                canonical.Count(change => change.Kind == DiagnosticChangeKind.SeverityChanged)),
            canonical)
        {
            PackageChanges = packageChanges,
            PackageSummary = SummarizePackages(packageChanges),
            RiskSummary = SummarizeRisks(canonical, before, after),
            ProjectSettingsChanges = projectSettingsChanges,
            Impact = impact,
            BaselineAnalysisErrors = baselineErrors,
            CurrentAnalysisErrors = currentErrors,
        };
    }

    private static IReadOnlyList<PackageChange> ComparePackages(
        IReadOnlyList<PackageInventoryItem> baseline,
        string baselineRepositoryRoot,
        IReadOnlyList<PackageInventoryItem> current,
        string currentRepositoryRoot)
    {
        var before = IndexPackages(baseline, baselineRepositoryRoot);
        var after = IndexPackages(current, currentRepositoryRoot);
        var changes = new List<PackageChange>();
        foreach (var key in before.Keys.Union(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var hasBefore = before.TryGetValue(key, out var previous);
            var hasAfter = after.TryGetValue(key, out var next);
            if (!hasBefore)
            {
                changes.Add(new PackageChange(PackageChangeKind.Added, null, next));
            }
            else if (!hasAfter)
            {
                changes.Add(new PackageChange(PackageChangeKind.Removed, previous, null));
            }
            else
            {
                var attributes = ChangedPackageAttributes(previous!, next!);
                if (attributes.Count == 0)
                {
                    continue;
                }

                var kind = ClassifyPackageChange(previous!, next!, attributes);
                changes.Add(new PackageChange(kind, previous, next)
                {
                    ChangedAttributes = attributes,
                });
            }
        }

        return changes
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.After?.Project ?? item.Before?.Project, PathComparer())
            .ThenBy(item => item.After?.Framework ?? item.Before?.Framework, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.After?.RuntimeIdentifier ?? item.Before?.RuntimeIdentifier, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.After?.Id ?? item.Before?.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<PackageAttributeChangeKind> ChangedPackageAttributes(
        DiffPackage before,
        DiffPackage after)
    {
        var changes = new List<PackageAttributeChangeKind>();
        if (!before.ResolvedVersion.Equals(after.ResolvedVersion, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add(PackageAttributeChangeKind.ResolvedVersion);
        }

        if (!string.Equals(before.RequestedVersion, after.RequestedVersion, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add(PackageAttributeChangeKind.RequestedVersion);
        }

        if (!before.VersionSource.Equals(after.VersionSource, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add(PackageAttributeChangeKind.VersionSource);
        }

        if (before.DependencyKind != after.DependencyKind)
        {
            changes.Add(PackageAttributeChangeKind.DependencyKind);
        }

        if (!string.Equals(before.PackageSource, after.PackageSource, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add(PackageAttributeChangeKind.PackageSource);
        }

        if (!string.Equals(before.ContentHash, after.ContentHash, StringComparison.Ordinal))
        {
            changes.Add(PackageAttributeChangeKind.ContentHash);
        }

        if (before.SignaturePresent != after.SignaturePresent)
        {
            changes.Add(PackageAttributeChangeKind.SignaturePresent);
        }

        return changes;
    }

    private static PackageChangeKind ClassifyPackageChange(
        DiffPackage before,
        DiffPackage after,
        IReadOnlyList<PackageAttributeChangeKind> attributes)
    {
        if (attributes.Contains(PackageAttributeChangeKind.ResolvedVersion))
        {
            return TryCompareResolvedVersions(before.ResolvedVersion, after.ResolvedVersion, out var comparison)
                ? comparison < 0
                    ? PackageChangeKind.Upgraded
                    : comparison > 0
                        ? PackageChangeKind.Downgraded
                        : PackageChangeKind.VersionChanged
                : PackageChangeKind.VersionChanged;
        }

        if (attributes.Count == 1 && attributes[0] == PackageAttributeChangeKind.DependencyKind)
        {
            return PackageChangeKind.DependencyKindChanged;
        }

        if (attributes.All(attribute => attribute is
                PackageAttributeChangeKind.ResolvedVersion or
                PackageAttributeChangeKind.RequestedVersion))
        {
            return PackageChangeKind.VersionChanged;
        }

        return PackageChangeKind.Modified;
    }

    public static bool TryCompareResolvedVersions(string before, string after, out int comparison)
    {
        comparison = 0;
        if (!TryParseResolvedVersion(before, out var left) || !TryParseResolvedVersion(after, out var right))
        {
            return false;
        }

        for (var index = 0; index < left.Core.Length; index++)
        {
            comparison = left.Core[index].CompareTo(right.Core[index]);
            if (comparison != 0)
            {
                return true;
            }
        }

        if (left.Prerelease.Count == 0 || right.Prerelease.Count == 0)
        {
            comparison = left.Prerelease.Count == right.Prerelease.Count
                ? 0
                : left.Prerelease.Count == 0 ? 1 : -1;
            return true;
        }

        var count = Math.Min(left.Prerelease.Count, right.Prerelease.Count);
        for (var index = 0; index < count; index++)
        {
            var leftPart = left.Prerelease[index];
            var rightPart = right.Prerelease[index];
            var leftNumeric = leftPart.All(char.IsAsciiDigit);
            var rightNumeric = rightPart.All(char.IsAsciiDigit);
            comparison = leftNumeric && rightNumeric
                ? CompareNumericIdentifier(leftPart, rightPart)
                : leftNumeric != rightNumeric
                    ? leftNumeric ? -1 : 1
                    : StringComparer.OrdinalIgnoreCase.Compare(leftPart, rightPart);
            if (comparison != 0)
            {
                return true;
            }
        }

        comparison = left.Prerelease.Count.CompareTo(right.Prerelease.Count);
        return true;
    }

    private static int CompareNumericIdentifier(string left, string right)
    {
        var normalizedLeft = left.TrimStart('0');
        var normalizedRight = right.TrimStart('0');
        normalizedLeft = normalizedLeft.Length == 0 ? "0" : normalizedLeft;
        normalizedRight = normalizedRight.Length == 0 ? "0" : normalizedRight;
        var lengthComparison = normalizedLeft.Length.CompareTo(normalizedRight.Length);
        return lengthComparison != 0
            ? lengthComparison
            : StringComparer.Ordinal.Compare(normalizedLeft, normalizedRight);
    }

    private static bool TryParseResolvedVersion(string value, out ComparableVersion version)
    {
        version = default!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var withoutMetadata = value.Trim().Split('+', 2)[0];
        var pieces = withoutMetadata.Split('-', 2);
        var coreParts = pieces[0].Split('.');
        if (coreParts.Length is < 1 or > 4)
        {
            return false;
        }

        var core = new long[4];
        for (var index = 0; index < coreParts.Length; index++)
        {
            if (!long.TryParse(coreParts[index], out core[index]) || core[index] < 0)
            {
                return false;
            }
        }

        IReadOnlyList<string> prerelease = [];
        if (pieces.Length == 2)
        {
            var identifiers = pieces[1].Split('.');
            if (identifiers.Any(identifier => identifier.Length == 0 ||
                    identifier.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')))
            {
                return false;
            }

            prerelease = identifiers;
        }

        version = new ComparableVersion(core, prerelease);
        return true;
    }

    private static PackageDiffSummary SummarizePackages(IReadOnlyList<PackageChange> changes) => new(
        changes.Count(item => item.Kind == PackageChangeKind.Added),
        changes.Count(item => item.Kind == PackageChangeKind.Removed),
        changes.Count(item => item.Kind == PackageChangeKind.Upgraded),
        changes.Count(item => item.Kind == PackageChangeKind.Downgraded),
        changes.Count(item => item.Kind == PackageChangeKind.VersionChanged),
        changes.Count(item => item.Before?.DependencyKind == PackageDependencyKind.Direct &&
                              item.After?.DependencyKind == PackageDependencyKind.Transitive),
        changes.Count(item => item.Before?.DependencyKind == PackageDependencyKind.Transitive &&
                              item.After?.DependencyKind == PackageDependencyKind.Direct),
        changes.Count(item => item.Kind == PackageChangeKind.Modified));

    private static DependencyRiskDiffSummary SummarizeRisks(
        IReadOnlyList<DiagnosticChange> changes,
        IReadOnlyDictionary<string, DiffDiagnostic> before,
        IReadOnlyDictionary<string, DiffDiagnostic> after) => new(
            changes.Count(item => item.Kind == DiagnosticChangeKind.Added && item.After?.Code == "PM007"),
            changes.Count(item => item.Kind == DiagnosticChangeKind.Resolved && item.Before?.Code == "PM007"),
            changes.Count(item => item.Kind == DiagnosticChangeKind.Added && item.After?.Code == "PM008"),
            changes.Count(item => item.Kind == DiagnosticChangeKind.Resolved && item.Before?.Code == "PM008"))
        {
            VulnerabilitiesPersistent = before.Keys.Intersect(after.Keys, StringComparer.Ordinal)
                .Count(key => before[key].Code == "PM007" && after[key].Code == "PM007"),
            DeprecationsPersistent = before.Keys.Intersect(after.Keys, StringComparer.Ordinal)
                .Count(key => before[key].Code == "PM008" && after[key].Code == "PM008"),
        };

    private sealed record ComparableVersion(long[] Core, IReadOnlyList<string> Prerelease);

    private static Dictionary<string, DiffPackage> IndexPackages(
        IReadOnlyList<PackageInventoryItem> packages,
        string repositoryRoot) => packages
        .Select(item => new DiffPackage(
            DiagnosticFingerprint.GetRelativePath(item.Project, repositoryRoot) ?? Path.GetFileName(item.Project),
            item.Framework,
            item.Id,
            item.ResolvedVersion,
            item.DependencyKind,
            item.RequestedVersion,
            item.VersionSource,
            item.RuntimeIdentifier,
            item.PackageSource,
            item.ContentHash,
            item.SignaturePresent))
        .OrderBy(item => item.Project, PathComparer())
        .ThenBy(item => item.Framework, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.RuntimeIdentifier, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
        .GroupBy(
            PackageKey,
            StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

    private static IReadOnlyList<ProjectSettingsChange> CompareProjectSettings(
        IReadOnlyList<ProjectPackageSettings> baseline,
        string baselineRepositoryRoot,
        IReadOnlyList<ProjectPackageSettings> current,
        string currentRepositoryRoot)
    {
        var before = IndexProjectSettings(baseline, baselineRepositoryRoot);
        var after = IndexProjectSettings(current, currentRepositoryRoot);
        var changes = new List<ProjectSettingsChange>();
        foreach (var key in before.Keys.Union(after.Keys, PathComparer()).Order(PathComparer()))
        {
            var hasBefore = before.TryGetValue(key, out var previous);
            var hasAfter = after.TryGetValue(key, out var next);
            if (!hasBefore)
            {
                changes.Add(new ProjectSettingsChange(ProjectSettingsChangeKind.Added, key, null, next));
            }
            else if (!hasAfter)
            {
                changes.Add(new ProjectSettingsChange(ProjectSettingsChangeKind.Removed, key, previous, null));
            }
            else if (previous!.ManagePackageVersionsCentrally != next!.ManagePackageVersionsCentrally ||
                     previous.CentralPackageTransitivePinningEnabled != next.CentralPackageTransitivePinningEnabled ||
                     previous.PackageSourceCount != next.PackageSourceCount ||
                     previous.PackageSourceMappingEnabled != next.PackageSourceMappingEnabled ||
                     previous.RestorePackagesWithLockFile != next.RestorePackagesWithLockFile ||
                     previous.RestoreLockedMode != next.RestoreLockedMode ||
                     previous.LockFileAvailable != next.LockFileAvailable)
            {
                changes.Add(new ProjectSettingsChange(ProjectSettingsChangeKind.Modified, key, previous, next));
            }
        }

        return changes;
    }

    private static Dictionary<string, ProjectPackageSettings> IndexProjectSettings(
        IReadOnlyList<ProjectPackageSettings> settings,
        string repositoryRoot) => settings
        .Select(item => item with
        {
            Project = DiagnosticFingerprint.GetRelativePath(item.Project, repositoryRoot) ?? Path.GetFileName(item.Project),
        })
        .OrderBy(item => item.Project, PathComparer())
        .ToDictionary(item => item.Project, item => item, PathComparer());

    private static string PackageKey(DiffPackage item)
    {
        var project = OperatingSystem.IsWindows() ? item.Project.ToUpperInvariant() : item.Project;
        return string.Join(
            '\n',
            project,
            item.Framework.ToUpperInvariant(),
            item.RuntimeIdentifier?.ToUpperInvariant() ?? string.Empty,
            item.Id.ToUpperInvariant());
    }

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static IReadOnlyDictionary<string, DiffDiagnostic> Index(
        AnalysisResult result,
        string repositoryRoot)
    {
        var root = RepositoryRoot.Parse(repositoryRoot);
        var riskIdentities = BuildRiskIdentityMap(result, root);
        return result.Diagnostics
            .Select(diagnostic => new
            {
                Identity = ResolveIdentity(diagnostic, root, riskIdentities),
                Diagnostic = Normalize(diagnostic, root),
            })
            .OrderByDescending(item => item.Diagnostic.Severity)
            .ThenBy(item => item.Diagnostic.Code, StringComparer.Ordinal)
            .ThenBy(item => item.Diagnostic.File, StringComparer.Ordinal)
            .ThenBy(item => item.Diagnostic.Line)
            .ThenBy(item => item.Diagnostic.Title, StringComparer.Ordinal)
            .GroupBy(item => item.Identity.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Diagnostic, StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, string> BuildRiskIdentityMap(
        AnalysisResult result,
        RepositoryRoot root)
    {
        var identities = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var vulnerability in result.Vulnerabilities)
        {
            var generated = VulnerabilityAuditParser.ToDiagnostics([vulnerability], result.Packages).Single();
            identities[DiagnosticFingerprint.Create(generated, root).Fingerprint] = CreateRiskFingerprint(
                "PM007",
                vulnerability.PackageId,
                vulnerability.AdvisoryUrl,
                RepositoryRoot.TryGetRelativeUri(vulnerability.Project, root) ?? vulnerability.Project,
                vulnerability.Framework);
        }

        foreach (var deprecation in result.DeprecatedPackages)
        {
            var generated = DeprecationAuditParser.ToDiagnostics([deprecation], result.Packages).Single();
            identities[DiagnosticFingerprint.Create(generated, root).Fingerprint] = CreateRiskFingerprint(
                "PM008",
                deprecation.PackageId,
                string.Empty,
                RepositoryRoot.TryGetRelativeUri(deprecation.Project, root) ?? deprecation.Project,
                deprecation.Framework);
        }

        return identities;
    }

    private static DiagnosticIdentity ResolveIdentity(
        Diagnostic diagnostic,
        RepositoryRoot root,
        IReadOnlyDictionary<string, string> riskIdentities)
    {
        var identity = DiagnosticFingerprint.Create(diagnostic, root);
        return riskIdentities.TryGetValue(identity.Fingerprint, out var riskFingerprint)
            ? identity with { Fingerprint = riskFingerprint }
            : identity;
    }

    private static string CreateRiskFingerprint(params string[] components)
    {
        var normalized = string.Join(
            '\n',
            components.Select(component => component.Trim().Replace('\\', '/').ToUpperInvariant()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    private static DiffDiagnostic Normalize(Diagnostic diagnostic, RepositoryRoot root)
    {
        var identity = DiagnosticFingerprint.Create(diagnostic, root);
        return new DiffDiagnostic(
            diagnostic.Code,
            diagnostic.Severity,
            DiagnosticFingerprint.SanitizeText(diagnostic.Title, root),
            DiagnosticFingerprint.SanitizeText(diagnostic.Explanation, root),
            diagnostic.Project is null ? null : DiagnosticFingerprint.SanitizeText(diagnostic.Project, root),
            identity.RelativePath,
            diagnostic.Line,
            DiagnosticFingerprint.SanitizeText(diagnostic.Evidence, root),
            DiagnosticFingerprint.SanitizeText(diagnostic.SuggestedAction, root),
            diagnostic.Confidence,
            diagnostic.OriginalCode,
            diagnostic.PackageId);
    }

    private static int ChangeOrder(DiagnosticChangeKind kind) => kind switch
    {
        DiagnosticChangeKind.Added => 0,
        DiagnosticChangeKind.SeverityChanged => 1,
        DiagnosticChangeKind.Resolved => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static IReadOnlyList<string> NormalizeErrors(
        IReadOnlyList<string> errors,
        string repositoryRoot)
    {
        var root = RepositoryRoot.Parse(repositoryRoot);
        return errors
            .Select(error => DiagnosticFingerprint.SanitizeText(error, root))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}

public static class AnalysisDiffSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string SerializeJson(AnalysisDiffReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    public static string SerializeText(AnalysisDiffReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.Append("PackageMedic diff against ")
            .Append(report.BaseReference)
            .Append(" (")
            .Append(report.BaseCommit.AsSpan(0, Math.Min(12, report.BaseCommit.Length)))
            .AppendLine(")");
        builder.Append("Added: ").Append(report.Summary.Added)
            .Append(" | Resolved: ").Append(report.Summary.Resolved)
            .Append(" | Severity changed: ").Append(report.Summary.SeverityChanged)
            .AppendLine();
        builder.Append("Package changes: ").Append(report.PackageChanges.Count)
            .Append(" | CPM settings changed: ").Append(report.ProjectSettingsChanges.Count)
            .AppendLine();
        builder.Append("Packages +").Append(report.PackageSummary.Added)
            .Append(" -").Append(report.PackageSummary.Removed)
            .Append(" | Upgraded: ").Append(report.PackageSummary.Upgraded)
            .Append(" | Downgraded: ").Append(report.PackageSummary.Downgraded)
            .Append(" | Uncomparable: ").Append(report.PackageSummary.UncomparableVersionChanges)
            .AppendLine();
        builder.Append("Vulnerabilities +").Append(report.RiskSummary.VulnerabilitiesIntroduced)
            .Append(" -").Append(report.RiskSummary.VulnerabilitiesResolved)
            .Append(" =").Append(report.RiskSummary.VulnerabilitiesPersistent)
            .Append(" | Deprecations +").Append(report.RiskSummary.DeprecationsIntroduced)
            .Append(" -").Append(report.RiskSummary.DeprecationsResolved)
            .Append(" =").Append(report.RiskSummary.DeprecationsPersistent)
            .AppendLine();
        if (report.Impact is { } impact)
        {
            builder.Append("Impact gate: ").Append(impact.GatePassed ? "passed" : "failed")
                .Append(" | Violations: ").Append(impact.Summary.Violations)
                .Append(" | Added direct: ").Append(impact.Summary.AddedDirectPackages)
                .Append(" | Added transitive: ").Append(impact.Summary.AddedTransitivePackages)
                .Append(" | Maximum blast radius: ").Append(impact.Summary.MaximumBlastRadius)
                .AppendLine();
        }

        if (report.Verification is { } verification)
        {
            builder.Append("Verification: ")
                .Append(ToVerificationText(verification.Decision.Verdict))
                .Append(" | Requested: ")
                .Append(ToVerificationText(verification.Level))
                .Append(" | Common evidence: ")
                .Append(ToVerificationText(verification.Decision.CommonEvidenceLevel))
                .AppendLine();
            AppendVerificationSnapshot(builder, "Baseline", verification.Baseline);
            AppendVerificationSnapshot(builder, "Candidate", verification.Candidate);
            if (verification.Decision.BlockingSnapshot is { } blockingSnapshot)
            {
                builder.Append("Verification blocked by ")
                    .Append(ToVerificationText(blockingSnapshot));
                if (verification.Decision.BlockingStage is { } blockingStage)
                {
                    builder.Append(' ').Append(ToVerificationText(blockingStage));
                }

                if (verification.Decision.FailureKind is { } failureKind)
                {
                    builder.Append(" (").Append(ToVerificationText(failureKind)).Append(')');
                }

                builder.AppendLine();
            }
        }

        if (!report.IsComplete)
        {
            builder.AppendLine("Comparison incomplete: no graph changes were calculated because an analysis failed.");
            foreach (var analysisError in report.BaselineAnalysisErrors)
            {
                builder.Append("! [base analysis] ").AppendLine(analysisError);
            }

            foreach (var analysisError in report.CurrentAnalysisErrors)
            {
                builder.Append("! [current analysis] ").AppendLine(analysisError);
            }

            return builder.ToString();
        }

        foreach (var change in report.Changes)
        {
            var diagnostic = change.After ?? change.Before!;
            var marker = change.Kind switch
            {
                DiagnosticChangeKind.Added => '+',
                DiagnosticChangeKind.Resolved => '-',
                DiagnosticChangeKind.SeverityChanged => '~',
                _ => throw new ArgumentOutOfRangeException(nameof(report)),
            };
            builder.Append(marker).Append(' ').Append('[');
            if (change.Kind == DiagnosticChangeKind.SeverityChanged)
            {
                builder.Append(change.Before!.Severity.ToString().ToLowerInvariant())
                    .Append(" -> ")
                    .Append(change.After!.Severity.ToString().ToLowerInvariant());
            }
            else
            {
                builder.Append(diagnostic.Severity.ToString().ToLowerInvariant());
            }

            builder.Append("] ").Append(diagnostic.Code).Append(' ').Append(diagnostic.Title);
            if (diagnostic.File is not null)
            {
                builder.Append(" — ").Append(diagnostic.File);
                if (diagnostic.Line is not null)
                {
                    builder.Append(':').Append(diagnostic.Line.Value);
                }
            }

            builder.AppendLine();
        }

        foreach (var change in report.PackageChanges)
        {
            var package = change.After ?? change.Before!;
            var marker = change.Kind is PackageChangeKind.Removed ? '-' : change.Kind is PackageChangeKind.Added ? '+' : '~';
            builder.Append(marker).Append(" [package] ").Append(package.Id)
                .Append(" in ").Append(package.Project).Append(" (").Append(package.Framework);
            if (package.RuntimeIdentifier is not null)
            {
                builder.Append('/').Append(package.RuntimeIdentifier);
            }

            builder.Append(')');
            AppendPackageChangeDetails(builder, change);
            builder.AppendLine();
        }

        foreach (var change in report.ProjectSettingsChanges)
        {
            var marker = change.Kind == ProjectSettingsChangeKind.Added
                ? '+'
                : change.Kind == ProjectSettingsChangeKind.Removed
                    ? '-'
                    : '~';
            builder.Append(marker).Append(" [settings] ").Append(change.Project);
            if (change.Kind == ProjectSettingsChangeKind.Added)
            {
                AppendProjectSettings(builder.Append(": added with "), change.After!);
            }
            else if (change.Kind == ProjectSettingsChangeKind.Removed)
            {
                AppendProjectSettings(builder.Append(": removed with "), change.Before!);
            }
            else
            {
                AppendProjectSettings(builder.Append(": "), change.Before!);
                AppendProjectSettings(builder.Append(" -> "), change.After!);
            }

            builder.AppendLine();
        }

        if (report.Impact is { Violations.Count: > 0 } failedImpact)
        {
            foreach (var violation in failedImpact.Violations)
            {
                builder.Append("! [impact] ").Append(violation.Code).Append(' ').Append(violation.Message);
                if (violation.RootPackageId is not null)
                {
                    builder.Append(" | root: ").Append(violation.RootPackageId);
                }

                if (violation.DependencyPath is { Count: > 1 } path)
                {
                    builder.Append(" | path: ")
                        .AppendJoin(" -> ", path.Select(segment => $"{segment.PackageId} {segment.ResolvedVersion}"));
                }

                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    private static void AppendVerificationSnapshot(
        StringBuilder builder,
        string label,
        VerificationSnapshotReport snapshot)
    {
        builder.Append(label)
            .Append(" stages: restore ").Append(ToVerificationText(snapshot.Restore.Status))
            .Append(" | build ").Append(ToVerificationText(snapshot.Build.Stage.Status))
            .Append(" | tests ").Append(ToVerificationText(snapshot.Tests.Stage.Status));
        if (snapshot.Tests.Stage.Status != VerificationStageStatus.NotRequested)
        {
            builder.Append(" (")
                .Append(snapshot.Tests.Passed).Append(" passed, ")
                .Append(snapshot.Tests.Failed).Append(" failed, ")
                .Append(snapshot.Tests.Skipped).Append(" skipped)");
        }

        builder.AppendLine();
    }

    private static string ToVerificationText<T>(T value) where T : struct, Enum
    {
        var text = value.ToString();
        var builder = new StringBuilder(text.Length + 8);
        for (var index = 0; index < text.Length; index++)
        {
            if (index > 0 && char.IsUpper(text[index]))
            {
                builder.Append(' ');
            }

            builder.Append(char.ToLowerInvariant(text[index]));
        }

        return builder.ToString();
    }

    private static void AppendPackageChangeDetails(StringBuilder builder, PackageChange change)
    {
        if (change.Kind == PackageChangeKind.Added)
        {
            builder.Append(": added ").Append(change.After!.ResolvedVersion)
                .Append(" as ").Append(change.After.DependencyKind.ToString().ToLowerInvariant());
            return;
        }

        if (change.Kind == PackageChangeKind.Removed)
        {
            builder.Append(": removed ").Append(change.Before!.ResolvedVersion)
                .Append(" as ").Append(change.Before.DependencyKind.ToString().ToLowerInvariant());
            return;
        }

        foreach (var attribute in change.ChangedAttributes)
        {
            builder.Append(attribute == change.ChangedAttributes[0] ? ": " : "; ");
            switch (attribute)
            {
                case PackageAttributeChangeKind.ResolvedVersion:
                    builder.Append("resolved ").Append(change.Before!.ResolvedVersion)
                        .Append(" -> ").Append(change.After!.ResolvedVersion);
                    break;
                case PackageAttributeChangeKind.RequestedVersion:
                    builder.Append("requested ").Append(change.Before!.RequestedVersion ?? "none")
                        .Append(" -> ").Append(change.After!.RequestedVersion ?? "none");
                    break;
                case PackageAttributeChangeKind.VersionSource:
                    builder.Append("source ").Append(change.Before!.VersionSource)
                        .Append(" -> ").Append(change.After!.VersionSource);
                    break;
                case PackageAttributeChangeKind.DependencyKind:
                    builder.Append("kind ").Append(change.Before!.DependencyKind.ToString().ToLowerInvariant())
                        .Append(" -> ").Append(change.After!.DependencyKind.ToString().ToLowerInvariant());
                    break;
                case PackageAttributeChangeKind.PackageSource:
                    builder.Append("package source ").Append(change.Before!.PackageSource ?? "unknown")
                        .Append(" -> ").Append(change.After!.PackageSource ?? "unknown");
                    break;
                case PackageAttributeChangeKind.ContentHash:
                    builder.Append("package content hash changed");
                    break;
                case PackageAttributeChangeKind.SignaturePresent:
                    builder.Append("signature present ").Append(change.Before!.SignaturePresent?.ToString().ToLowerInvariant() ?? "unknown")
                        .Append(" -> ").Append(change.After!.SignaturePresent?.ToString().ToLowerInvariant() ?? "unknown");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(change));
            }
        }
    }

    private static void AppendProjectSettings(StringBuilder builder, ProjectPackageSettings settings) => builder
        .Append("central management ").Append(settings.ManagePackageVersionsCentrally ? "on" : "off")
        .Append(", transitive pinning ").Append(settings.CentralPackageTransitivePinningEnabled ? "on" : "off")
        .Append(", package sources ").Append(settings.PackageSourceCount)
        .Append(", source mapping ").Append(settings.PackageSourceMappingEnabled ? "on" : "off")
        .Append(", lock file ").Append(settings.LockFileAvailable ? "available" : "missing")
        .Append(", locked mode ").Append(settings.RestoreLockedMode ? "on" : "off");
}
