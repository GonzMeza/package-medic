using System.Text.Json;
using PackageMedic.Core;

namespace PackageMedic.Core.Tests;

public sealed class AnalysisDiffTests
{
    private const string Commit = "fedcba9876543210fedcba9876543210fedcba98";

    [Fact]
    public void ClassifiesAddedResolvedAndSeverityChangesDeterministically()
    {
        var beforeRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PackageMedic.Diff.Before"));
        var afterRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PackageMedic.Diff.After"));
        var baseline = Result(
            "baseline",
            Diagnostic("PM001", DiagnosticSeverity.Warning, beforeRoot, "shared"),
            Diagnostic("PM003", DiagnosticSeverity.Warning, beforeRoot, "resolved"));
        var current = Result(
            "current",
            Diagnostic("PM002", DiagnosticSeverity.Warning, afterRoot, "added"),
            Diagnostic("PM001", DiagnosticSeverity.Error, afterRoot, "shared"));

        var first = AnalysisDiffComparer.Compare(
            baseline,
            beforeRoot,
            current,
            afterRoot,
            "main",
            Commit);
        var second = AnalysisDiffComparer.Compare(
            baseline with { Diagnostics = baseline.Diagnostics.Reverse().ToArray() },
            beforeRoot,
            current with { Diagnostics = current.Diagnostics.Reverse().ToArray() },
            afterRoot,
            "main",
            Commit);

        Assert.Equal(new AnalysisDiffSummary(1, 1, 1), first.Summary);
        Assert.Equal(
            [DiagnosticChangeKind.Added, DiagnosticChangeKind.SeverityChanged, DiagnosticChangeKind.Resolved],
            first.Changes.Select(change => change.Kind));
        var severityChange = first.Changes.Single(change => change.Kind == DiagnosticChangeKind.SeverityChanged);
        Assert.Equal(DiagnosticSeverity.Warning, severityChange.Before!.Severity);
        Assert.Equal(DiagnosticSeverity.Error, severityChange.After!.Severity);
        Assert.Equal(
            AnalysisDiffSerializer.SerializeJson(first),
            AnalysisDiffSerializer.SerializeJson(second));
    }

    [Fact]
    public void IgnoresLineMovementAndRepositoryRootDifferences()
    {
        var beforeRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PackageMedic.Diff.Before"));
        var afterRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PackageMedic.Diff.After"));
        var before = Diagnostic("PM001", DiagnosticSeverity.Warning, beforeRoot, "same") with { Line = 4 };
        var after = Diagnostic("PM001", DiagnosticSeverity.Warning, afterRoot, "same") with { Line = 99 };

        var comparison = AnalysisDiffComparer.Compare(
            Result("before", before),
            beforeRoot,
            Result("after", after),
            afterRoot,
            "v0.3.0",
            Commit);

        Assert.Empty(comparison.Changes);
        Assert.Equal(new AnalysisDiffSummary(0, 0, 0), comparison.Summary);
    }

    [Fact]
    public void SerializesPortableJsonAndReadableText()
    {
        var beforeRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PackageMedic.Diff.Before"));
        var afterRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PackageMedic.Diff.After"));
        var report = AnalysisDiffComparer.Compare(
            Result("before"),
            beforeRoot,
            Result("after", Diagnostic("PM006", DiagnosticSeverity.Warning, afterRoot, "floating")),
            afterRoot,
            "origin/main",
            Commit);

        var json = AnalysisDiffSerializer.SerializeJson(report);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(AnalysisDiffReport.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("origin/main", root.GetProperty("baseReference").GetString());
        Assert.Equal("added", root.GetProperty("changes")[0].GetProperty("kind").GetString());
        Assert.Equal("src/App.csproj", root.GetProperty("changes")[0].GetProperty("after").GetProperty("file").GetString());
        Assert.DoesNotContain(afterRoot, json, StringComparison.OrdinalIgnoreCase);

        var text = AnalysisDiffSerializer.SerializeText(report);
        Assert.Contains("Added: 1 | Resolved: 0 | Severity changed: 0", text, StringComparison.Ordinal);
        Assert.Contains("+ [warning] PM006", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ComparesPackageVersionsKindsAndCentralManagementSettings()
    {
        var beforeRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PackageMedic.Diff.Before"));
        var afterRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PackageMedic.Diff.After"));
        var baseline = Result("before") with
        {
            Packages =
            [
                Package(beforeRoot, "A", "1.0.0", PackageDependencyKind.Direct),
                Package(beforeRoot, "Removed", "2.0.0", PackageDependencyKind.Direct),
                Package(beforeRoot, "Kind", "3.0.0", PackageDependencyKind.Transitive),
                Package(beforeRoot, "PureKind", "4.0.0", PackageDependencyKind.Transitive) with
                {
                    RequestedVersion = "4.0.0",
                    VersionSource = "project",
                },
            ],
            ProjectSettings =
            [
                Settings(beforeRoot, centrallyManaged: false),
                Settings(beforeRoot, centrallyManaged: true, projectName: "Removed.csproj"),
            ],
        };
        var current = Result("after") with
        {
            Packages =
            [
                Package(afterRoot, "A", "1.1.0", PackageDependencyKind.Direct),
                Package(afterRoot, "Added", "2.0.0", PackageDependencyKind.Direct),
                Package(afterRoot, "Kind", "3.0.0", PackageDependencyKind.Direct),
                Package(afterRoot, "PureKind", "4.0.0", PackageDependencyKind.Direct),
            ],
            ProjectSettings =
            [
                Settings(afterRoot, centrallyManaged: true),
                Settings(afterRoot, centrallyManaged: true, projectName: "Added.csproj"),
            ],
        };

        var report = AnalysisDiffComparer.Compare(
            baseline, beforeRoot, current, afterRoot, "main", Commit);

        Assert.Equal(5, report.PackageChanges.Count);
        Assert.Contains(report.PackageChanges, item => item.Kind == PackageChangeKind.Added && item.After!.Id == "Added");
        Assert.Contains(report.PackageChanges, item => item.Kind == PackageChangeKind.Removed && item.Before!.Id == "Removed");
        Assert.Contains(report.PackageChanges, item => item.Kind == PackageChangeKind.Upgraded && item.After!.Id == "A");
        var kindChange = Assert.Single(report.PackageChanges, item => item.After?.Id == "Kind");
        Assert.Equal(PackageChangeKind.Modified, kindChange.Kind);
        Assert.Contains(PackageAttributeChangeKind.DependencyKind, kindChange.ChangedAttributes);
        Assert.Contains(PackageAttributeChangeKind.VersionSource, kindChange.ChangedAttributes);
        Assert.Contains(
            report.PackageChanges,
            item => item.Kind == PackageChangeKind.DependencyKindChanged && item.After!.Id == "PureKind");
        Assert.Equal(3, report.ProjectSettingsChanges.Count);
        var settings = Assert.Single(report.ProjectSettingsChanges, item => item.Kind == ProjectSettingsChangeKind.Modified);
        Assert.False(settings.Before!.ManagePackageVersionsCentrally);
        Assert.True(settings.After!.ManagePackageVersionsCentrally);
        Assert.Contains(report.ProjectSettingsChanges, item => item.Kind == ProjectSettingsChangeKind.Added);
        Assert.Contains(report.ProjectSettingsChanges, item => item.Kind == ProjectSettingsChangeKind.Removed);
        Assert.Equal(1, report.PackageSummary.Added);
        Assert.Equal(1, report.PackageSummary.Removed);
        Assert.Equal(1, report.PackageSummary.Upgraded);
        var json = AnalysisDiffSerializer.SerializeJson(report);
        Assert.DoesNotContain(beforeRoot, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(afterRoot, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreservesRuntimeSpecificAndCompoundPackageChanges()
    {
        var beforeRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PackageMedic.Diff.Before"));
        var afterRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PackageMedic.Diff.After"));
        var baseline = Result("before") with
        {
            Packages =
            [
                Package(beforeRoot, "Rid.Package", "1.0.0", PackageDependencyKind.Transitive, "win-x64"),
                Package(beforeRoot, "Rid.Package", "2.0.0", PackageDependencyKind.Direct, "linux-x64"),
            ],
        };
        var current = Result("after") with
        {
            Packages =
            [
                Package(afterRoot, "Rid.Package", "1.1.0", PackageDependencyKind.Direct, "win-x64"),
                Package(afterRoot, "Rid.Package", "2.1.0", PackageDependencyKind.Direct, "linux-x64"),
            ],
        };

        var report = AnalysisDiffComparer.Compare(
            baseline, beforeRoot, current, afterRoot, "main", Commit);

        Assert.Equal(2, report.PackageChanges.Count);
        var compound = Assert.Single(report.PackageChanges, item => item.After!.RuntimeIdentifier == "win-x64");
        Assert.Equal(PackageChangeKind.Upgraded, compound.Kind);
        Assert.Contains(PackageAttributeChangeKind.ResolvedVersion, compound.ChangedAttributes);
        Assert.Contains(PackageAttributeChangeKind.DependencyKind, compound.ChangedAttributes);
        var text = AnalysisDiffSerializer.SerializeText(report);
        Assert.Contains("net8.0/win-x64", text, StringComparison.Ordinal);
        Assert.Contains("resolved 1.0.0 -> 1.1.0", text, StringComparison.Ordinal);
        Assert.Contains("kind transitive -> direct", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.0.0", "1.1.0", -1)]
    [InlineData("2.0.0", "1.9.9", 1)]
    [InlineData("1.0.0-preview.2", "1.0.0-preview.10", -1)]
    [InlineData("1.0.0-preview.999999999999999999999", "1.0.0-preview.1000000000000000000000", -1)]
    [InlineData("1.0.0-preview", "1.0.0", -1)]
    [InlineData("1.0", "1.0.0+build.2", 0)]
    public void ComparesResolvedVersionsWithoutNetworkMetadata(string before, string after, int expectedSign)
    {
        Assert.True(AnalysisDiffComparer.TryCompareResolvedVersions(before, after, out var comparison));
        Assert.Equal(expectedSign, Math.Sign(comparison));
    }

    [Fact]
    public void ClassifiesDowngradesAndUncomparableVersionsAndSummarizesDependencyRisk()
    {
        var beforeRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PackageMedic.Diff.Before"));
        var afterRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PackageMedic.Diff.After"));
        var baseline = Result(
            "before",
            Diagnostic("PM007", DiagnosticSeverity.Error, beforeRoot, "resolved vulnerability")) with
        {
            Packages =
            [
                Package(beforeRoot, "Down", "2.0.0", PackageDependencyKind.Direct),
                Package(beforeRoot, "Opaque", "vendor-a", PackageDependencyKind.Direct),
            ],
        };
        var current = Result(
            "after",
            Diagnostic("PM008", DiagnosticSeverity.Warning, afterRoot, "new deprecation")) with
        {
            Packages =
            [
                Package(afterRoot, "Down", "1.5.0", PackageDependencyKind.Direct),
                Package(afterRoot, "Opaque", "vendor-b", PackageDependencyKind.Direct),
            ],
        };

        var report = AnalysisDiffComparer.Compare(
            baseline, beforeRoot, current, afterRoot, "main", Commit);

        Assert.Contains(report.PackageChanges, item => item.Kind == PackageChangeKind.Downgraded);
        Assert.Contains(report.PackageChanges, item => item.Kind == PackageChangeKind.VersionChanged);
        Assert.Equal(1, report.PackageSummary.Downgraded);
        Assert.Equal(1, report.PackageSummary.UncomparableVersionChanges);
        Assert.Equal(1, report.RiskSummary.VulnerabilitiesResolved);
        Assert.Equal(1, report.RiskSummary.DeprecationsIntroduced);
    }

    [Fact]
    public void TreatsTheSameAdvisoryAsPersistentAcrossPackageVersionChanges()
    {
        var beforeRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PackageMedic.Diff.Before"));
        var afterRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PackageMedic.Diff.After"));
        var baseline = VulnerabilityResult(
            beforeRoot,
            "1.0.0",
            VulnerabilitySeverity.High,
            "https://github.com/advisories/GHSA-persistent");
        var current = VulnerabilityResult(
            afterRoot,
            "1.1.0",
            VulnerabilitySeverity.High,
            "https://github.com/advisories/GHSA-persistent");

        var report = AnalysisDiffComparer.Compare(
            baseline, beforeRoot, current, afterRoot, "main", Commit);

        Assert.Empty(report.Changes);
        Assert.Equal(0, report.RiskSummary.VulnerabilitiesIntroduced);
        Assert.Equal(0, report.RiskSummary.VulnerabilitiesResolved);
        Assert.Equal(1, report.RiskSummary.VulnerabilitiesPersistent);
        Assert.Equal(1, report.PackageSummary.Upgraded);
        Assert.Contains("Vulnerabilities +0 -0 =1", AnalysisDiffSerializer.SerializeText(report), StringComparison.Ordinal);
        using var document = JsonDocument.Parse(AnalysisDiffSerializer.SerializeJson(report));
        Assert.Equal(
            1,
            document.RootElement.GetProperty("riskSummary").GetProperty("vulnerabilitiesPersistent").GetInt32());
    }

    [Fact]
    public void SelectsRiskDiagnosticsUsingTheSamePersistentIdentityAsDiff()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PackageMedic.Diff.RiskGate"));
        var current = VulnerabilityResult(
            root,
            "2.0.0",
            VulnerabilitySeverity.High,
            "https://github.com/advisories/GHSA-test-0000-0000");
        var comparison = AnalysisDiffComparer.Compare(Result(root), root, current, root, "main", Commit);
        var added = Assert.Single(comparison.Changes, change => change.After?.Code == "PM007");

        var selected = AnalysisDiffComparer.SelectDiagnosticsByFingerprint(
            current,
            root,
            new HashSet<string>([added.Fingerprint], StringComparer.Ordinal));

        Assert.Equal("PM007", Assert.Single(selected).Code);
        Assert.NotEqual(
            added.Fingerprint,
            DiagnosticFingerprint.Compute(Assert.Single(current.Diagnostics), root));
    }

    [Fact]
    public void SelectsDeprecationsUsingTheSamePersistentIdentityAsDiff()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PackageMedic.Diff.DeprecationGate"));
        var current = DeprecationResult(root, "2.0.0", "Replacement.Package");
        var comparison = AnalysisDiffComparer.Compare(Result(root), root, current, root, "main", Commit);
        var added = Assert.Single(comparison.Changes, change => change.After?.Code == "PM008");

        var selected = AnalysisDiffComparer.SelectDiagnosticsByFingerprint(
            current,
            root,
            new HashSet<string>([added.Fingerprint], StringComparer.Ordinal));

        Assert.Equal("PM008", Assert.Single(selected).Code);
        Assert.NotEqual(
            added.Fingerprint,
            DiagnosticFingerprint.Compute(Assert.Single(current.Diagnostics), root));
    }

    [Fact]
    public void ReportsSeverityChangeWithoutReintroducingTheSameAdvisory()
    {
        var beforeRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PackageMedic.Diff.Before"));
        var afterRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PackageMedic.Diff.After"));
        var baseline = VulnerabilityResult(
            beforeRoot,
            "1.0.0",
            VulnerabilitySeverity.Moderate,
            "https://github.com/advisories/GHSA-severity");
        var current = VulnerabilityResult(
            afterRoot,
            "1.0.1",
            VulnerabilitySeverity.High,
            "https://github.com/advisories/GHSA-severity");

        var report = AnalysisDiffComparer.Compare(
            baseline, beforeRoot, current, afterRoot, "main", Commit);

        var changed = Assert.Single(report.Changes);
        Assert.Equal(DiagnosticChangeKind.SeverityChanged, changed.Kind);
        Assert.Equal(0, report.RiskSummary.VulnerabilitiesIntroduced);
        Assert.Equal(0, report.RiskSummary.VulnerabilitiesResolved);
        Assert.Equal(1, report.RiskSummary.VulnerabilitiesPersistent);
    }

    [Fact]
    public void CountsChangedAdvisoryAsOneResolvedAndOneIntroducedRisk()
    {
        var beforeRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PackageMedic.Diff.Before"));
        var afterRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PackageMedic.Diff.After"));
        var baseline = VulnerabilityResult(
            beforeRoot,
            "1.0.0",
            VulnerabilitySeverity.High,
            "https://github.com/advisories/GHSA-old");
        var current = VulnerabilityResult(
            afterRoot,
            "1.1.0",
            VulnerabilitySeverity.High,
            "https://github.com/advisories/GHSA-new");

        var report = AnalysisDiffComparer.Compare(
            baseline, beforeRoot, current, afterRoot, "main", Commit);

        Assert.Equal(2, report.Changes.Count);
        Assert.Equal(1, report.RiskSummary.VulnerabilitiesIntroduced);
        Assert.Equal(1, report.RiskSummary.VulnerabilitiesResolved);
        Assert.Equal(0, report.RiskSummary.VulnerabilitiesPersistent);
    }

    [Fact]
    public void TreatsDeprecationAsPersistentAcrossVersionAndRecommendationChanges()
    {
        var beforeRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PackageMedic.Diff.Before"));
        var afterRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PackageMedic.Diff.After"));
        var baseline = DeprecationResult(beforeRoot, "1.0.0", "Replacement.One");
        var current = DeprecationResult(afterRoot, "2.0.0", "Replacement.Two");

        var report = AnalysisDiffComparer.Compare(
            baseline, beforeRoot, current, afterRoot, "main", Commit);

        Assert.Empty(report.Changes);
        Assert.Equal(0, report.RiskSummary.DeprecationsIntroduced);
        Assert.Equal(0, report.RiskSummary.DeprecationsResolved);
        Assert.Equal(1, report.RiskSummary.DeprecationsPersistent);
    }

    [Fact]
    public void RefusesToCalculateChangesWhenEitherAnalysisIsIncomplete()
    {
        var beforeRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PackageMedic.Diff.Before"));
        var afterRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PackageMedic.Diff.After"));
        var baseline = Result("before") with
        {
            AnalysisErrors = [$"Restore failed in {beforeRoot}"],
            Packages = [Package(beforeRoot, "Example", "1.0.0", PackageDependencyKind.Direct)],
        };
        var current = Result("after") with
        {
            Packages = [Package(afterRoot, "Example", "2.0.0", PackageDependencyKind.Direct)],
        };

        var report = AnalysisDiffComparer.Compare(
            baseline, beforeRoot, current, afterRoot, "main", Commit);

        Assert.False(report.IsComplete);
        Assert.Empty(report.Changes);
        Assert.Empty(report.PackageChanges);
        Assert.Empty(report.ProjectSettingsChanges);
        Assert.Single(report.BaselineAnalysisErrors);
        Assert.DoesNotContain(beforeRoot, report.BaselineAnalysisErrors[0], StringComparison.OrdinalIgnoreCase);
        var text = AnalysisDiffSerializer.SerializeText(report);
        Assert.Contains("Comparison incomplete", text, StringComparison.Ordinal);
        Assert.Contains("[base analysis]", text, StringComparison.Ordinal);
    }

    private static AnalysisResult Result(string target, params Diagnostic[] diagnostics) => new(
        "0.4.0",
        target,
        new ScanSummary(0, 0, 0, 0, 0, diagnostics.Count(item => item.Severity == DiagnosticSeverity.Warning), 0),
        diagnostics,
        []);

    private static AnalysisResult VulnerabilityResult(
        string root,
        string version,
        VulnerabilitySeverity severity,
        string advisory)
    {
        var project = Path.Combine(root, "src", "App.csproj");
        var vulnerability = new PackageVulnerability(
            "Risk.Package",
            version,
            severity,
            advisory,
            project,
            "net8.0",
            PackageDependencyKind.Direct);
        var package = Package(root, vulnerability.PackageId, version, PackageDependencyKind.Direct);
        var diagnostic = Assert.Single(VulnerabilityAuditParser.ToDiagnostics([vulnerability], [package]));
        return Result(root, diagnostic) with
        {
            Packages = [package],
            Vulnerabilities = [vulnerability],
        };
    }

    private static AnalysisResult DeprecationResult(string root, string version, string replacement)
    {
        var project = Path.Combine(root, "src", "App.csproj");
        var deprecation = new DeprecatedPackage(
            "Deprecated.Package",
            version,
            [PackageDeprecationReason.Legacy],
            replacement,
            "[1.0.0,)",
            project,
            "net8.0",
            PackageDependencyKind.Direct);
        var package = Package(root, deprecation.PackageId, version, PackageDependencyKind.Direct);
        var diagnostic = Assert.Single(DeprecationAuditParser.ToDiagnostics([deprecation], [package]));
        return Result(root, diagnostic) with
        {
            Packages = [package],
            DeprecatedPackages = [deprecation],
        };
    }

    private static Diagnostic Diagnostic(
        string code,
        DiagnosticSeverity severity,
        string repositoryRoot,
        string evidence) => new(
            code,
            severity,
            $"Title {code}",
            "Explanation",
            Path.Combine(repositoryRoot, "src", "App.csproj"),
            Path.Combine(repositoryRoot, "src", "App.csproj"),
            10,
            $"{evidence} at {Path.Combine(repositoryRoot, "src", "App.csproj")}",
            "Review it.",
            DiagnosticConfidence.High);

    private static PackageInventoryItem Package(
        string root,
        string id,
        string version,
        PackageDependencyKind kind,
        string? runtimeIdentifier = null) => new(
        Path.Combine(root, "src", "App.csproj"),
        "net8.0",
        id,
        version,
        kind,
        kind == PackageDependencyKind.Direct ? version : null,
        kind == PackageDependencyKind.Direct ? "project" : "resolved",
        runtimeIdentifier);

    private static ProjectPackageSettings Settings(
        string root,
        bool centrallyManaged,
        string projectName = "App.csproj") => new(
        Path.Combine(root, "src", projectName),
        centrallyManaged,
        false);
}
