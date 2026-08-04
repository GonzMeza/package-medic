using PackageMedic.Core;

namespace PackageMedic.Core.Tests;

public sealed class DependencyImpactTests
{
    [Fact]
    public void BuildsDeterministicShortestPathsAndAlternativeRoots()
    {
        var project = Path.GetFullPath("App.csproj");
        PackageInventoryItem[] inventory =
        [
            Package(project, "Root.A", "1.0.0", PackageDependencyKind.Direct),
            Package(project, "Root.B", "1.0.0", PackageDependencyKind.Direct),
            Package(project, "Middle", "2.0.0", PackageDependencyKind.Transitive),
            Package(project, "Leaf", "3.0.0", PackageDependencyKind.Transitive),
        ];
        ResolvedPackageDependencyEdge[] edges =
        [
            Edge(project, "Root.B", "1.0.0", "Leaf", "3.0.0"),
            Edge(project, "Root.A", "1.0.0", "Middle", "2.0.0"),
            Edge(project, "Middle", "2.0.0", "Leaf", "3.0.0"),
        ];

        var paths = DependencyGraphBuilder.BuildPaths(inventory, edges);

        var leaf = Assert.Single(paths, item => item.PackageId == "Leaf");
        Assert.Equal("Root.B", leaf.RootPackageId);
        Assert.Equal(["Root.B", "Leaf"], leaf.Path.Select(item => item.PackageId));
        Assert.Equal(["Root.A"], leaf.AlternativeRootPackageIds);
    }

    [Fact]
    public void ChoosesOneCanonicalPathThroughDenseEqualDepthDiamonds()
    {
        var project = Path.GetFullPath("Dense.csproj");
        var inventory = new List<PackageInventoryItem>
        {
            Package(project, "Root", "1.0.0", PackageDependencyKind.Direct),
        };
        var edges = new List<ResolvedPackageDependencyEdge>();
        var parents = new[] { "Root" };
        const int layers = 22;
        for (var layer = 0; layer < layers; layer++)
        {
            var children = new[] { $"Layer.{layer:D2}.A", $"Layer.{layer:D2}.B" };
            inventory.AddRange(children.Select(id =>
                Package(project, id, "1.0.0", PackageDependencyKind.Transitive)));
            foreach (var parent in parents)
            {
                edges.AddRange(children.Select(child => Edge(project, parent, "1.0.0", child, "1.0.0")));
            }

            parents = children;
        }

        var paths = DependencyGraphBuilder.BuildPaths(inventory, edges);

        Assert.Equal(1 + layers * 2, paths.Count);
        var last = Assert.Single(paths, item => item.PackageId == $"Layer.{layers - 1:D2}.A");
        Assert.Equal("Root", last.RootPackageId);
        Assert.Equal(layers + 1, last.Path.Count);
        Assert.Equal("Layer.00.A", last.Path[1].PackageId);
    }

    [Fact]
    public void HandlesDependencyCyclesWithoutRepeatingNodes()
    {
        var project = Path.GetFullPath("Cycle.csproj");
        PackageInventoryItem[] inventory =
        [
            Package(project, "Root", "1.0.0", PackageDependencyKind.Direct),
            Package(project, "A", "1.0.0", PackageDependencyKind.Transitive),
            Package(project, "B", "1.0.0", PackageDependencyKind.Transitive),
        ];
        ResolvedPackageDependencyEdge[] edges =
        [
            Edge(project, "Root", "1.0.0", "A", "1.0.0"),
            Edge(project, "A", "1.0.0", "B", "1.0.0"),
            Edge(project, "B", "1.0.0", "A", "1.0.0"),
        ];

        var paths = DependencyGraphBuilder.BuildPaths(inventory, edges);

        Assert.Equal(["Root", "A", "B"],
            Assert.Single(paths, item => item.PackageId == "B").Path.Select(item => item.PackageId));
    }

    [Fact]
    public void FailsClosedWhenMaterializedPathsExceedTheSafetyBudget()
    {
        var project = Path.GetFullPath("LongChain.csproj");
        const int packages = 1_500;
        var inventory = Enumerable.Range(0, packages)
            .Select(index => Package(
                project,
                $"Package.{index:D4}",
                "1.0.0",
                index == 0 ? PackageDependencyKind.Direct : PackageDependencyKind.Transitive))
            .ToArray();
        var edges = Enumerable.Range(0, packages - 1)
            .Select(index => Edge(
                project,
                $"Package.{index:D4}",
                "1.0.0",
                $"Package.{index + 1:D4}",
                "1.0.0"))
            .ToArray();

        var exception = Assert.Throws<InvalidDataException>(() =>
            DependencyGraphBuilder.BuildPaths(inventory, edges));

        Assert.Contains("path safety limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluatesImpactBudgetsTrustAndDeterministicRestorePolicies()
    {
        var beforeRoot = Path.Combine(Path.GetTempPath(), "impact-before");
        var afterRoot = Path.Combine(Path.GetTempPath(), "impact-after");
        var before = Result(
            beforeRoot,
            Package(beforeRoot, "Root", "2.0.0", PackageDependencyKind.Direct,
                source: "https://old.example/v3", contentHash: "root-old"),
            Package(beforeRoot, "Tampered", "1.0.0", PackageDependencyKind.Direct,
                source: "https://allowed.example/v3", contentHash: "old-hash"));
        var afterPackage = Package(
            afterRoot,
            "Root",
            "1.0.0",
            PackageDependencyKind.Transitive,
            source: "https://new.example/v3",
            contentHash: "new-hash");
        var after = Result(
            afterRoot,
            afterPackage,
            Package(afterRoot, "Tampered", "1.0.0", PackageDependencyKind.Direct,
                source: "https://allowed.example/v3", contentHash: "new-hash")) with
        {
            ProjectSettings =
            [
                new ProjectPackageSettings(afterPackage.Project, false, false)
                {
                    PackageSourceCount = 2,
                    PackageSourceMappingEnabled = false,
                    RestoreLockedMode = false,
                    LockFileAvailable = false,
                },
            ],
        };
        var policy = new ConfiguredImpactPolicy(
            FailOnDowngrade: true,
            FailOnDirectToTransitive: true,
            MaxAddedPackages: 0,
            MaxAddedTransitivePackages: 0,
            FailOnSourceChange: true,
            FailOnContentChange: true,
            RequirePackageSourceMapping: true,
            RequireLockedMode: true,
            AllowedSources: ["https://allowed.example/v3"]);

        var report = AnalysisDiffComparer.Compare(before, beforeRoot, after, afterRoot, "main", new string('a', 40), policy);

        Assert.False(report.Impact!.GatePassed);
        Assert.Contains(report.Impact.Violations, item => item.Code == "PMI001");
        Assert.Contains(report.Impact.Violations, item => item.Code == "PMI002");
        Assert.Contains(report.Impact.Violations, item => item.Code == "PMI005");
        Assert.Contains(report.Impact.Violations, item => item.Code == "PMI007");
        Assert.Contains(report.Impact.Violations, item => item.Code == "PMI008");
        Assert.Contains(report.Impact.Violations, item => item.Code == "PMI009");
        Assert.Contains(report.Impact.Violations, item => item.Code == "PMI010");
        Assert.Equal(1, report.Impact.Summary.Downgrades);
        Assert.Equal(1, report.Impact.Summary.DirectToTransitive);
        Assert.Equal(1, report.Impact.Summary.SourceChanges);
        Assert.Equal(1, report.Impact.Summary.ContentChanges);
    }

    [Fact]
    public void FailsClosedWhenPersistentPackageProvenanceEvidenceDisappears()
    {
        var beforeRoot = Path.Combine(Path.GetTempPath(), "impact-provenance-before");
        var afterRoot = Path.Combine(Path.GetTempPath(), "impact-provenance-after");
        var before = Result(
            beforeRoot,
            Package(
                beforeRoot,
                "Example.Package",
                "1.0.0",
                PackageDependencyKind.Direct,
                source: "https://allowed.example/v3",
                contentHash: "known-hash"));
        var after = Result(
            afterRoot,
            Package(afterRoot, "Example.Package", "1.0.0", PackageDependencyKind.Direct));
        var policy = new ConfiguredImpactPolicy(
            FailOnDowngrade: true,
            FailOnDirectToTransitive: true,
            MaxAddedPackages: null,
            MaxAddedTransitivePackages: null,
            FailOnSourceChange: true,
            FailOnContentChange: true,
            RequirePackageSourceMapping: false,
            RequireLockedMode: false,
            AllowedSources: []);

        var report = AnalysisDiffComparer.Compare(
            before,
            beforeRoot,
            after,
            afterRoot,
            "main",
            new string('a', 40),
            policy);

        var change = Assert.Single(report.PackageChanges);
        Assert.Contains(PackageAttributeChangeKind.PackageSource, change.ChangedAttributes);
        Assert.Contains(PackageAttributeChangeKind.ContentHash, change.ChangedAttributes);
        Assert.Contains(report.Impact!.Violations, item => item.Code == "PMI005");
        Assert.Contains(report.Impact.Violations, item => item.Code == "PMI010");
        Assert.Contains(report.Impact.Violations, item => item.Message.Contains("unknown", StringComparison.Ordinal));
        Assert.Equal(1, report.Impact.Summary.SourceChanges);
        Assert.Equal(1, report.Impact.Summary.ContentChanges);
    }

    private static AnalysisResult Result(string root, params PackageInventoryItem[] packages) => new(
        "0.5.0",
        root,
        new ScanSummary(0, 1, 0, 0, 0, 0, 0),
        [],
        [])
    {
        Packages = packages,
    };

    private static PackageInventoryItem Package(
        string projectOrRoot,
        string id,
        string version,
        PackageDependencyKind kind,
        string? source = null,
        string? contentHash = null) => new(
            projectOrRoot.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                ? projectOrRoot
                : Path.Combine(projectOrRoot, "App.csproj"),
            "net8.0",
            id,
            version,
            kind,
            version,
            "project",
            PackageSource: source,
            ContentHash: contentHash);

    private static ResolvedPackageDependencyEdge Edge(
        string project,
        string parent,
        string parentVersion,
        string child,
        string childVersion) => new(
            project,
            "net8.0",
            null,
            parent,
            parentVersion,
            child,
            childVersion);
}
