using PackageMedic.Core;

namespace PackageMedic.Core.Tests;

public sealed class DiagnosticEngineTests
{
    private readonly DiagnosticEngine engine = new();

    [Fact]
    public void ReportsUnusedCentralVersion()
    {
        var project = CreateProject(
            central: [new("Humanizer", "2.14.1", "Directory.Packages.props", 8, null)]);

        var diagnostic = Assert.Single(engine.Analyze([project]));

        Assert.Equal("PM001", diagnostic.Code);
        Assert.Equal(DiagnosticConfidence.High, diagnostic.Confidence);
    }

    [Fact]
    public void DoesNotReportCentralVersionUsedBySeveralProjects()
    {
        var central = new CentralPackageVersion("xunit", "2.9.2", "Directory.Packages.props", 8, null);
        var first = CreateProject(
            path: "One.csproj",
            direct: [new("xunit", null, null, "One.csproj", 6, null)],
            central: [central]);
        var second = CreateProject(
            path: "Two.csproj",
            direct: [new("xunit", null, null, "Two.csproj", 6, null)],
            central: [central]);

        Assert.DoesNotContain(engine.Analyze([first, second]), item => item.Code == "PM001");
    }

    [Fact]
    public void ReportsVersionDriftForEveryAffectedProject()
    {
        var first = CreateProject(
            path: "One.csproj",
            centrallyManaged: false,
            direct: [new("xunit", "2.6.2", null, "One.csproj", 6, null)]);
        var second = CreateProject(
            path: "Two.csproj",
            centrallyManaged: false,
            direct: [new("xunit", "2.9.2", null, "Two.csproj", 6, null)]);

        var diagnostics = engine.Analyze([first, second]).Where(item => item.Code == "PM002").ToArray();

        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, item => Assert.Contains("One: 2.6.2", item.Evidence, StringComparison.Ordinal));
    }

    [Fact]
    public void SemanticallyEquivalentExactVersionsDoNotReportDrift()
    {
        var first = CreateProject(
            path: "One.csproj",
            centrallyManaged: false,
            direct: [new("Example", "1.0", null, "One.csproj", 6, "net8.0")]);
        var second = CreateProject(
            path: "Two.csproj",
            centrallyManaged: false,
            direct: [new("Example", "1.0.0+build.42", null, "Two.csproj", 6, "net8.0")]);

        Assert.DoesNotContain(engine.Analyze([first, second]), item => item.Code == "PM002");
    }

    [Fact]
    public void VersionDifferencesInDisjointFrameworkScopesDoNotReportDrift()
    {
        var first = CreateProject(
            path: "One.csproj",
            centrallyManaged: false,
            frameworks: ["net8.0"],
            direct: [new("Example", "1.0.0", null, "One.csproj", 6, "net8.0")]);
        var second = CreateProject(
            path: "Two.csproj",
            centrallyManaged: false,
            frameworks: ["net9.0"],
            direct: [new("Example", "2.0.0", null, "Two.csproj", 6, "net9.0")]);

        Assert.DoesNotContain(engine.Analyze([first, second]), item => item.Code == "PM002");
    }

    [Fact]
    public void BaseAndPlatformSpecificFrameworkScopesDoNotReportDrift()
    {
        var first = CreateProject(
            path: "One.csproj",
            centrallyManaged: false,
            frameworks: ["net8.0"],
            direct: [new("Example", "1.0.0", null, "One.csproj", 6, "net8.0")]);
        var second = CreateProject(
            path: "Two.csproj",
            centrallyManaged: false,
            frameworks: ["net8.0-windows10.0.19041.0"],
            direct: [new("Example", "2.0.0", null, "Two.csproj", 6, "net8.0-windows10.0.19041.0")]);

        Assert.DoesNotContain(engine.Analyze([first, second]), item => item.Code == "PM002");
    }

    [Fact]
    public void UnscopedReferenceStillReportsDriftForAFrameworkSharedByBothProjects()
    {
        var first = CreateProject(
            path: "One.csproj",
            centrallyManaged: false,
            frameworks: ["net8.0", "net9.0"],
            direct: [new("Example", "1.0.0", null, "One.csproj", 6, null)]);
        var second = CreateProject(
            path: "Two.csproj",
            centrallyManaged: false,
            frameworks: ["net9.0"],
            direct: [new("Example", "2.0.0", null, "Two.csproj", 6, "net9.0")]);

        Assert.Equal(2, engine.Analyze([first, second]).Count(item => item.Code == "PM002"));
    }

    [Fact]
    public void VersionOverrideDoesNotCountAsCentralManagementBypass()
    {
        var project = CreateProject(
            direct:
            [
                new("Allowed", null, "2.0.0", "App.csproj", 6, null),
                new("Bypass", "1.0.0", null, "App.csproj", 7, null),
            ],
            central:
            [
                new("Allowed", "1.0.0", "Directory.Packages.props", 7, null),
                new("Bypass", "1.0.0", "Directory.Packages.props", 8, null),
            ]);

        var bypass = Assert.Single(engine.Analyze([project]), item => item.Code == "PM003");

        Assert.Contains("Bypass", bypass.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsDuplicateCentralVersion()
    {
        var project = CreateProject(
            direct: [new("xunit", null, null, "App.csproj", 6, null)],
            central:
            [
                new("xunit", "2.6.2", "Directory.Packages.props", 7, null),
                new("xunit", "2.9.2", "Directory.Packages.props", 8, null),
            ]);

        var diagnostic = Assert.Single(engine.Analyze([project]), item => item.Code == "PM004");

        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void TransitivelyPinnedPackageIsNotUnused()
    {
        var project = CreateProject(
            pinning: true,
            direct: [new("xunit", null, null, "App.csproj", 6, null)],
            central:
            [
                new("xunit", "2.9.2", "Directory.Packages.props", 7, null),
                new("xunit.assert", "2.9.2", "Directory.Packages.props", 8, null),
            ],
            resolved: new HashSet<string>(["xunit", "xunit.assert"], StringComparer.OrdinalIgnoreCase));

        Assert.Empty(engine.Analyze([project]));
    }

    [Fact]
    public void DisjointTargetFrameworkCentralVersionsAreNotDuplicates()
    {
        var project = CreateProject(
            direct: [new("xunit", null, null, "App.csproj", 6, "net8.0")],
            central:
            [
                new("xunit", "2.6.2", "Directory.Packages.props", 7, "net8.0"),
                new("xunit", "2.9.2", "Directory.Packages.props", 8, "net9.0"),
            ]);

        Assert.DoesNotContain(engine.Analyze([project]), item => item.Code == "PM004");
    }

    private static ProjectAnalysis CreateProject(
        string path = "App.csproj",
        bool centrallyManaged = true,
        bool pinning = false,
        IReadOnlyList<DirectPackageReference>? direct = null,
        IReadOnlyList<CentralPackageVersion>? central = null,
        IReadOnlySet<string>? resolved = null,
        IReadOnlyList<string>? frameworks = null) => new()
        {
            ProjectPath = Path.GetFullPath(path),
            ManagePackageVersionsCentrally = centrallyManaged,
            CentralPackageTransitivePinningEnabled = pinning,
            TargetFrameworks = frameworks ?? ["net8.0"],
            DirectPackages = direct ?? [],
            CentralVersions = central ?? [],
            ResolvedPackages = resolved ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            TransitivePackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AssetDiagnostics = [],
        };
}
