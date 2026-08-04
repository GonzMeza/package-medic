using PackageMedic.Core;

namespace PackageMedic.Core.Tests;

public sealed class PackageMedicAnalyzerTests
{
    [Fact]
    public void InventoryEnrichmentPrefersAnExactFrameworkMatch()
    {
        var project = CreateEvaluatedProject(
        [
            new("Example", "1.0.0", null, "broad.props", 10, "net8.0-windows"),
            new("Example", "2.0.0", null, "exact.props", 20, "net8.0-windows7.0"),
        ]);

        var enriched = Assert.Single(PackageMedicAnalyzer.EnrichPackageInventory(
            [Inventory("net8.0-windows7.0")],
            project));

        Assert.Equal("2.0.0", enriched.RequestedVersion);
        Assert.Equal("exact.props", enriched.SourceFile);
        Assert.Equal(20, enriched.SourceLine);
    }

    [Fact]
    public void InventoryEnrichmentAllowsOneUnambiguousPlatformNormalizedMatch()
    {
        var project = CreateEvaluatedProject(
            [new("Example", "1.0.0", null, "App.csproj", 10, "net8.0-windows")]);

        var enriched = Assert.Single(PackageMedicAnalyzer.EnrichPackageInventory(
            [Inventory("net8.0-windows7.0")],
            project));

        Assert.Equal("1.0.0", enriched.RequestedVersion);
        Assert.Equal("project", enriched.VersionSource);
        Assert.Equal("App.csproj", enriched.SourceFile);
    }

    [Fact]
    public void InventoryEnrichmentDoesNotGuessBetweenNormalizedFrameworkMatches()
    {
        var project = CreateEvaluatedProject(
        [
            new("Example", "1.0.0", null, "windows7.props", 10, "net8.0-windows7.0"),
            new("Example", "2.0.0", null, "windows10.props", 20, "net8.0-windows10.0.19041.0"),
        ]);

        var enriched = Assert.Single(PackageMedicAnalyzer.EnrichPackageInventory(
            [Inventory("net8.0-windows")],
            project));

        Assert.Null(enriched.RequestedVersion);
        Assert.Equal("implicit", enriched.VersionSource);
        Assert.Null(enriched.SourceFile);
        Assert.Null(enriched.SourceLine);
    }

    [Fact]
    public void InventoryEnrichmentDoesNotTreatBaseAndPlatformTfmsAsEquivalent()
    {
        var project = CreateEvaluatedProject(
            [new("Example", "1.0.0", null, "App.csproj", 10, "net8.0")]);

        var enriched = Assert.Single(PackageMedicAnalyzer.EnrichPackageInventory(
            [Inventory("net8.0-windows7.0")],
            project));

        Assert.Null(enriched.RequestedVersion);
        Assert.Equal("implicit", enriched.VersionSource);
        Assert.Null(enriched.SourceFile);
    }

    [Fact]
    public void InventoryEnrichmentFallsBackToOneUnscopedDeclaration()
    {
        var project = CreateEvaluatedProject(
        [
            new("Example", "1.0.0", null, "ios.props", 10, "net8.0-ios"),
            new("Example", "2.0.0", null, "global.props", 20, null),
        ]);

        var enriched = Assert.Single(PackageMedicAnalyzer.EnrichPackageInventory(
            [Inventory("net8.0-android")],
            project));

        Assert.Equal("2.0.0", enriched.RequestedVersion);
        Assert.Equal("global.props", enriched.SourceFile);
    }

    [Fact]
    public void InventoryEnrichmentSelectsTheExactCentralFrameworkVersion()
    {
        var project = CreateEvaluatedProject(
            [new("Example", null, null, "App.csproj", 6, "net8.0")],
            [
                new("Example", "1.0.0", "Directory.Packages.props", 10, "net9.0"),
                new("Example", "2.0.0", "Directory.Packages.props", 20, "net8.0"),
            ]);

        var enriched = Assert.Single(PackageMedicAnalyzer.EnrichPackageInventory(
            [Inventory("net8.0")],
            project));

        Assert.Equal("2.0.0", enriched.RequestedVersion);
        Assert.Equal("central", enriched.VersionSource);
        Assert.Equal("Directory.Packages.props", enriched.SourceFile);
        Assert.Equal(20, enriched.SourceLine);
    }

    private static PackageInventoryItem Inventory(string framework) => new(
        "App.csproj",
        framework,
        "Example",
        "2.0.0",
        PackageDependencyKind.Direct,
        null,
        "resolved");

    private static EvaluatedProject CreateEvaluatedProject(
        IReadOnlyList<DirectPackageReference> directPackages,
        IReadOnlyList<CentralPackageVersion>? centralVersions = null) => new(
        "App.csproj",
        false,
        false,
        ["net8.0"],
        directPackages,
        centralVersions ?? [],
        "obj/project.assets.json");
}
