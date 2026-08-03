using PackageMedic.Core;

namespace PackageMedic.Core.Tests;

public sealed class FloatingVersionDetectorTests
{
    [Theory]
    [InlineData("*")]
    [InlineData("1.*")]
    [InlineData("1.2.*")]
    [InlineData("1.2.3.*")]
    [InlineData("*-*")]
    [InlineData("1.*-*")]
    [InlineData("1.2.3-*")]
    [InlineData("1.2.3-rc.*")]
    [InlineData("  1.2.*  ")]
    public void RecognizesSupportedNuGetFloatingVersions(string version)
    {
        Assert.True(FloatingVersionDetector.IsFloating(version));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.2.3")]
    [InlineData("[1.0,2.0)")]
    [InlineData("(,2.0]")]
    [InlineData("[1.2.3]")]
    [InlineData("$(PackageVersion)")]
    [InlineData("1.$(Minor).*")]
    [InlineData("1.*.3")]
    [InlineData("[1.*,2.0)")]
    [InlineData("1.2.3-alpha*")]
    [InlineData("1.2.3-rc.*.1")]
    public void IgnoresFixedInvalidAndUnevaluatedVersions(string? version)
    {
        Assert.False(FloatingVersionDetector.IsFloating(version));
    }

    [Fact]
    public void ReportsPackageVersionPackageReferenceAndVersionOverride()
    {
        var project = CreateProject(
            direct:
            [
                new("Direct.Float", "2.*", null, "App.csproj", 7, null),
                new("Override.Float", null, "3.0.0-rc.*", "App.csproj", 8, null),
                new("Fixed.Range", "[1.0,2.0)", null, "App.csproj", 9, null),
                new("Unresolved", "$(UnresolvedVersion)", null, "App.csproj", 10, null),
            ],
            central:
            [
                new("Central.Float", "1.*", "Directory.Packages.props", 6, null),
                new("Fixed.Central", "[4.0,5.0)", "Directory.Packages.props", 7, null),
            ]);

        var diagnostics = new DiagnosticEngine().Analyze([project])
            .Where(item => item.Code == "PM006")
            .OrderBy(item => item.Line)
            .ToArray();

        Assert.Equal(3, diagnostics.Length);
        Assert.All(diagnostics, item => Assert.Equal(DiagnosticConfidence.High, item.Confidence));
        Assert.Contains(diagnostics, item => item.Evidence.Contains("PackageVersion Central.Float", StringComparison.Ordinal));
        Assert.Contains(diagnostics, item => item.Evidence.Contains("Version='2.*'", StringComparison.Ordinal));
        Assert.Contains(diagnostics, item => item.Evidence.Contains("VersionOverride='3.0.0-rc.*'", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, item => item.Evidence.Contains("Fixed", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, item => item.Evidence.Contains("Unresolved", StringComparison.Ordinal));
    }

    [Fact]
    public void DeduplicatesTheSameEvaluatedItemAcrossFrameworksAndProjects()
    {
        var direct = new DirectPackageReference("Direct.Float", "2.*", null, "Shared.props", 4, "net8.0");
        var central = new CentralPackageVersion("Central.Float", "1.*", "Directory.Packages.props", 6, "net8.0");
        var first = CreateProject(path: "One.csproj", direct: [direct], central: [central]);
        var second = CreateProject(
            path: "Two.csproj",
            direct: [direct with { TargetFramework = "net9.0" }],
            central: [central with { TargetFramework = "net9.0" }]);

        var diagnostics = new DiagnosticEngine().Analyze([first, second])
            .Where(item => item.Code == "PM006")
            .ToArray();

        Assert.Equal(2, diagnostics.Length);
    }

    private static ProjectAnalysis CreateProject(
        string path = "App.csproj",
        IReadOnlyList<DirectPackageReference>? direct = null,
        IReadOnlyList<CentralPackageVersion>? central = null) => new()
        {
            ProjectPath = Path.GetFullPath(path),
            ManagePackageVersionsCentrally = false,
            CentralPackageTransitivePinningEnabled = false,
            TargetFrameworks = ["net8.0"],
            DirectPackages = direct ?? [],
            CentralVersions = central ?? [],
            ResolvedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            TransitivePackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AssetDiagnostics = [],
        };
}
