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
        Assert.Contains(report.PackageChanges, item => item.Kind == PackageChangeKind.VersionChanged && item.After!.Id == "A");
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
        Assert.Equal(PackageChangeKind.Modified, compound.Kind);
        Assert.Contains(PackageAttributeChangeKind.ResolvedVersion, compound.ChangedAttributes);
        Assert.Contains(PackageAttributeChangeKind.DependencyKind, compound.ChangedAttributes);
        var text = AnalysisDiffSerializer.SerializeText(report);
        Assert.Contains("net8.0/win-x64", text, StringComparison.Ordinal);
        Assert.Contains("resolved 1.0.0 -> 1.1.0", text, StringComparison.Ordinal);
        Assert.Contains("kind transitive -> direct", text, StringComparison.Ordinal);
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
