using System.Text.Json;
using PackageMedic.Core;

namespace PackageMedic.Core.Tests;

public sealed class CycloneDxSbomSerializerTests
{
    [Fact]
    public void ProducesDeterministicOutputAcrossInputOrderAndCloneLocation()
    {
        var firstRoot = Path.Combine(Path.GetTempPath(), "packagemedic-sbom-clone-a");
        var secondRoot = Path.Combine(Path.GetTempPath(), "packagemedic-sbom-clone-b");
        var firstPackages = Packages(firstRoot);
        var secondPackages = Packages(secondRoot);
        var firstPaths = Paths(firstRoot);
        var secondPaths = Paths(secondRoot);

        var first = CycloneDxSbomSerializer.Serialize(
            Result(firstRoot, firstPackages, firstPaths),
            firstRoot);
        var second = CycloneDxSbomSerializer.Serialize(
            Result(secondRoot, secondPackages.Reverse().ToArray(), secondPaths.Reverse().ToArray()),
            secondRoot);

        Assert.Equal(first, second);
    }

    [Fact]
    public void CreatesEscapedNuGetPackageUrls()
    {
        var root = Path.Combine(Path.GetTempPath(), "packagemedic-sbom-purl");
        var package = Package(
            root,
            "Contoso Package/β",
            "1.0.0+build/meta",
            PackageDependencyKind.Direct);

        using var document = JsonDocument.Parse(CycloneDxSbomSerializer.Serialize(
            Result(root, [package], []),
            root));
        var component = FindPackageComponent(document.RootElement, package.Id);

        Assert.Equal(
            "pkg:nuget/Contoso%20Package%2F%CE%B2@1.0.0%2Bbuild%2Fmeta",
            component.GetProperty("purl").GetString());
    }

    [Fact]
    public void RepresentsProjectDirectAndCanonicalTransitiveRelationships()
    {
        var root = Path.Combine(Path.GetTempPath(), "packagemedic-sbom-relationships");
        using var document = JsonDocument.Parse(CycloneDxSbomSerializer.Serialize(
            Result(root, Packages(root), Paths(root)),
            root));
        var json = document.RootElement;
        var project = FindComponent(json, "application", "src/App.csproj");
        var direct = FindPackageComponent(json, "Root.Package");
        var middle = FindPackageComponent(json, "Middle.Package");
        var leaf = FindPackageComponent(json, "Leaf.Package");

        AssertDependency(json, Reference(project), Reference(direct));
        AssertDependency(json, Reference(direct), Reference(middle));
        AssertDependency(json, Reference(middle), Reference(leaf));
        Assert.Equal("direct", Property(direct, "packagemedic:dependency-kind"));
        Assert.Equal("transitive", Property(leaf, "packagemedic:dependency-kind"));
        Assert.Equal("net8.0", Property(leaf, "packagemedic:framework"));
        Assert.Equal("win-x64", Property(leaf, "packagemedic:runtime-identifier"));
        Assert.Equal("src/App.csproj", Property(leaf, "packagemedic:project"));
    }

    [Fact]
    public void MarksTheCanonicalNuGetGraphAsIncomplete()
    {
        var root = Path.Combine(Path.GetTempPath(), "packagemedic-sbom-incomplete");
        using var document = JsonDocument.Parse(CycloneDxSbomSerializer.Serialize(
            Result(root, Packages(root), Paths(root), ["restore was incomplete"]),
            root));
        var json = document.RootElement;
        var composition = Assert.Single(json.GetProperty("compositions").EnumerateArray());
        var metadata = json.GetProperty("metadata");

        Assert.Equal(CycloneDxSbomSerializer.SchemaUri, json.GetProperty("$schema").GetString());
        Assert.Equal("CycloneDX", json.GetProperty("bomFormat").GetString());
        Assert.Equal("1.7", json.GetProperty("specVersion").GetString());
        Assert.Equal("incomplete", composition.GetProperty("aggregate").GetString());
        var composedReferences = composition.GetProperty("dependencies")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToHashSet(StringComparer.Ordinal);
        foreach (var component in json.GetProperty("components").EnumerateArray())
        {
            Assert.Contains(Reference(component), composedReferences);
        }

        Assert.Equal("resolved-nuget-dependencies", Property(metadata, "packagemedic:scope"));
        Assert.Equal("incomplete", Property(metadata, "packagemedic:completeness"));
        Assert.Equal("1", Property(metadata, "packagemedic:analysis-error-count"));
        Assert.Contains("canonical", Property(metadata, "packagemedic:completeness-reason"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExcludesMachinePathsCredentialBearingSourcesInvalidHashesAndAnalysisErrors()
    {
        var root = Path.Combine(Path.GetTempPath(), "Users", "alice", "private-repository");
        var package = Package(root, "Safe.Package", "1.2.3", PackageDependencyKind.Direct) with
        {
            SourceFile = Path.Combine(root, "src", "Directory.Packages.props"),
            PackageSource = "https://alice:password@example.test/v3/index.json?token=secret",
            ContentHash = "secret-content-hash",
        };
        var result = Result(
            root,
            [package],
            [],
            [$"Restore failed under {root} with token=analysis-secret"]);

        var json = CycloneDxSbomSerializer.Serialize(result, root);
        var normalizedRoot = root.Replace('\\', '/');

        Assert.DoesNotContain(root, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(normalizedRoot, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-content-hash", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("src/Directory.Packages.props", json, StringComparison.Ordinal);
        Assert.DoesNotContain("analysis-secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("src/App.csproj", json, StringComparison.Ordinal);
    }

    [Fact]
    public void IncludesOnlyPortableAndValidatedNuGetProvenance()
    {
        var root = Path.Combine(Path.GetTempPath(), "packagemedic-sbom-provenance");
        var sha512 = Enumerable.Range(0, 64).Select(index => (byte)index).ToArray();
        var package = Package(root, "Safe.Package", "1.2.3", PackageDependencyKind.Direct) with
        {
            SourceFile = Path.Combine(root, "Directory.Packages.props"),
            PackageSource = "https://api.nuget.org/v3/index.json",
            ContentHash = "sha512-" + Convert.ToBase64String(sha512),
            SignaturePresent = true,
        };

        using var document = JsonDocument.Parse(CycloneDxSbomSerializer.Serialize(
            Result(root, [package], []),
            root));
        var component = FindPackageComponent(document.RootElement, package.Id);
        var hash = Assert.Single(component.GetProperty("hashes").EnumerateArray());

        Assert.Equal("SHA-512", hash.GetProperty("alg").GetString());
        Assert.Equal(Convert.ToHexString(sha512).ToLowerInvariant(), hash.GetProperty("content").GetString());
        Assert.Equal("project", Property(component, "packagemedic:version-source"));
        Assert.Equal("Directory.Packages.props", Property(component, "packagemedic:declaration-file"));
        Assert.Equal("https://api.nuget.org/v3/index.json", Property(component, "packagemedic:package-source"));
        Assert.Equal("true", Property(component, "packagemedic:signature-present"));
    }

    [Fact]
    public async Task EnforcesTheSameExactByteLimitForSyncAndAsyncOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), "packagemedic-sbom-limit");
        var result = Result(root, Packages(root), Paths(root));
        var expected = CycloneDxSbomSerializer.Serialize(result, root);
        var exactBytes = System.Text.Encoding.UTF8.GetByteCount(expected);

        Assert.Equal(expected, CycloneDxSbomSerializer.Serialize(result, root, exactBytes));
        Assert.Throws<InvalidDataException>(() =>
            CycloneDxSbomSerializer.Serialize(result, root, exactBytes - 1));

        await using var exact = new MemoryStream();
        await CycloneDxSbomSerializer.SerializeAsync(
            exact,
            result,
            root,
            exactBytes,
            TestContext.Current.CancellationToken);
        Assert.Equal(expected, System.Text.Encoding.UTF8.GetString(exact.ToArray()));

        await using var over = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CycloneDxSbomSerializer.SerializeAsync(
                over,
                result,
                root,
                exactBytes - 1,
                TestContext.Current.CancellationToken));
    }

    private static AnalysisResult Result(
        string root,
        IReadOnlyList<PackageInventoryItem> packages,
        IReadOnlyList<PackageDependencyPath> paths,
        IReadOnlyList<string>? errors = null) => new(
        "0.6.0",
        root,
        new ScanSummary(0, 1, 1, 2, 0, 0, 0),
        [],
        errors ?? [])
        {
            Packages = packages,
            DependencyPaths = paths,
        };

    private static PackageInventoryItem[] Packages(string root) =>
    [
        Package(root, "Root.Package", "1.0.0", PackageDependencyKind.Direct),
        Package(root, "Middle.Package", "2.0.0", PackageDependencyKind.Transitive),
        Package(root, "Leaf.Package", "3.0.0", PackageDependencyKind.Transitive),
    ];

    private static PackageDependencyPath[] Paths(string root)
    {
        var project = Path.Combine(root, "src", "App.csproj");
        return
        [
            new(
                project,
                "net8.0",
                "win-x64",
                "Root.Package",
                "1.0.0",
                "Root.Package",
                "1.0.0",
                [new("Root.Package", "1.0.0")],
                []),
            new(
                project,
                "net8.0",
                "win-x64",
                "Middle.Package",
                "2.0.0",
                "Root.Package",
                "1.0.0",
                [new("Root.Package", "1.0.0"), new("Middle.Package", "2.0.0")],
                []),
            new(
                project,
                "net8.0",
                "win-x64",
                "Leaf.Package",
                "3.0.0",
                "Root.Package",
                "1.0.0",
                [
                    new("Root.Package", "1.0.0"),
                    new("Middle.Package", "2.0.0"),
                    new("Leaf.Package", "3.0.0"),
                ],
                []),
        ];
    }

    private static PackageInventoryItem Package(
        string root,
        string id,
        string version,
        PackageDependencyKind dependencyKind) => new(
        Path.Combine(root, "src", "App.csproj"),
        "net8.0",
        id,
        version,
        dependencyKind,
        dependencyKind == PackageDependencyKind.Direct ? version : null,
        dependencyKind == PackageDependencyKind.Direct ? "project" : "resolved",
        "win-x64");

    private static JsonElement FindPackageComponent(JsonElement root, string name) =>
        FindComponent(root, "library", name);

    private static JsonElement FindComponent(JsonElement root, string type, string name) =>
        Assert.Single(root.GetProperty("components").EnumerateArray(), item =>
            item.GetProperty("type").GetString() == type &&
            item.GetProperty("name").GetString() == name);

    private static string Reference(JsonElement component) =>
        component.GetProperty("bom-ref").GetString()!;

    private static string Property(JsonElement owner, string name) =>
        Assert.Single(owner.GetProperty("properties").EnumerateArray(), item =>
            item.GetProperty("name").GetString() == name)
        .GetProperty("value")
        .GetString()!;

    private static void AssertDependency(JsonElement root, string parent, string child)
    {
        var dependency = Assert.Single(root.GetProperty("dependencies").EnumerateArray(), item =>
            item.GetProperty("ref").GetString() == parent);
        Assert.Contains(
            dependency.GetProperty("dependsOn").EnumerateArray(),
            item => item.GetString() == child);
    }
}
