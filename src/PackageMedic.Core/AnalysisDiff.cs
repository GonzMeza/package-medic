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
    string? OriginalCode);

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
}

public enum PackageAttributeChangeKind
{
    ResolvedVersion,
    RequestedVersion,
    VersionSource,
    DependencyKind,
}

public sealed record DiffPackage(
    string Project,
    string Framework,
    string Id,
    string ResolvedVersion,
    PackageDependencyKind DependencyKind,
    string? RequestedVersion,
    string VersionSource,
    string? RuntimeIdentifier);

public sealed record PackageChange(PackageChangeKind Kind, DiffPackage? Before, DiffPackage? After)
{
    public IReadOnlyList<PackageAttributeChangeKind> ChangedAttributes { get; init; } = [];
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
    public const int CurrentSchemaVersion = 1;

    public IReadOnlyList<PackageChange> PackageChanges { get; init; } = [];

    public IReadOnlyList<ProjectSettingsChange> ProjectSettingsChanges { get; init; } = [];

    public bool IsComplete { get; init; } = true;

    public IReadOnlyList<string> BaselineAnalysisErrors { get; init; } = [];

    public IReadOnlyList<string> CurrentAnalysisErrors { get; init; } = [];
}

/// <summary>
/// Compares diagnostics by the same repository-portable identity used by baselines and SARIF.
/// </summary>
public static class AnalysisDiffComparer
{
    public static AnalysisDiffReport Compare(
        AnalysisResult baseline,
        string baselineRepositoryRoot,
        AnalysisResult current,
        string currentRepositoryRoot,
        string baseReference,
        string baseCommit)
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

        var before = Index(baseline.Diagnostics, baselineRepositoryRoot);
        var after = Index(current.Diagnostics, currentRepositoryRoot);
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
            ProjectSettingsChanges = projectSettingsChanges,
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
        foreach (var key in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
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

                var kind = ClassifyPackageChange(attributes);
                changes.Add(new PackageChange(kind, previous, next)
                {
                    ChangedAttributes = attributes,
                });
            }
        }

        return changes
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.After?.Project ?? item.Before?.Project, StringComparer.OrdinalIgnoreCase)
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

        return changes;
    }

    private static PackageChangeKind ClassifyPackageChange(
        IReadOnlyList<PackageAttributeChangeKind> attributes)
    {
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
            item.RuntimeIdentifier))
        .OrderBy(item => item.Project, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.Framework, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.RuntimeIdentifier, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
        .GroupBy(
            item => $"{item.Project}|{item.Framework}|{item.RuntimeIdentifier}|{item.Id}",
            StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<ProjectSettingsChange> CompareProjectSettings(
        IReadOnlyList<ProjectPackageSettings> baseline,
        string baselineRepositoryRoot,
        IReadOnlyList<ProjectPackageSettings> current,
        string currentRepositoryRoot)
    {
        var before = IndexProjectSettings(baseline, baselineRepositoryRoot);
        var after = IndexProjectSettings(current, currentRepositoryRoot);
        var changes = new List<ProjectSettingsChange>();
        foreach (var key in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
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
                     previous.CentralPackageTransitivePinningEnabled != next.CentralPackageTransitivePinningEnabled)
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
        .OrderBy(item => item.Project, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(item => item.Project, item => item, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, DiffDiagnostic> Index(
        IReadOnlyList<Diagnostic> diagnostics,
        string repositoryRoot)
    {
        var root = RepositoryRoot.Parse(repositoryRoot);
        return diagnostics
            .Select(diagnostic => new
            {
                Identity = DiagnosticFingerprint.Create(diagnostic, root),
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
            diagnostic.OriginalCode);
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
            builder.Append(marker).Append(" [CPM] ").Append(change.Project);
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
                builder.Append(": central management ")
                    .Append(change.Before!.ManagePackageVersionsCentrally ? "on" : "off")
                    .Append(" -> ")
                    .Append(change.After!.ManagePackageVersionsCentrally ? "on" : "off")
                    .Append(", transitive pinning ")
                    .Append(change.Before.CentralPackageTransitivePinningEnabled ? "on" : "off")
                    .Append(" -> ")
                    .Append(change.After.CentralPackageTransitivePinningEnabled ? "on" : "off");
            }

            builder.AppendLine();
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
                default:
                    throw new ArgumentOutOfRangeException(nameof(change));
            }
        }
    }

    private static void AppendProjectSettings(StringBuilder builder, ProjectPackageSettings settings) => builder
        .Append("central management ").Append(settings.ManagePackageVersionsCentrally ? "on" : "off")
        .Append(", transitive pinning ").Append(settings.CentralPackageTransitivePinningEnabled ? "on" : "off");
}
