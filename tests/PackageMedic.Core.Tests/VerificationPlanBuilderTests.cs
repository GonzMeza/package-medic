namespace PackageMedic.Core.Tests;

public sealed class VerificationPlanBuilderTests
{
    [Fact]
    public async Task EvaluationQueriesAndMapsAllVerificationMetadata()
    {
        const string output = """
            {
              "Properties": {
                "TargetFramework": "net8.0",
                "ProjectAssetsFile": "obj/project.assets.json",
                "IsTestProject": "true",
                "IsTestingPlatformApplication": "true",
                "OutputType": "Exe",
                "TargetPath": "bin/Debug/net8.0/App.Tests.dll"
              },
              "Items": {
                "PackageReference": [],
                "PackageVersion": []
              }
            }
            """;
        var runner = new RecordingProcessRunner(new ProcessResult(0, output, string.Empty));
        var project = Absolute("evaluation", "App.Tests.csproj");

        var evaluated = await new MsBuildProjectEvaluator(runner).EvaluateAsync(
            project,
            TestContext.Current.CancellationToken);

        var call = Assert.Single(runner.Calls);
        Assert.Equal("dotnet", call.FileName);
        Assert.Equal(Path.GetDirectoryName(project), call.WorkingDirectory);
        Assert.Equal(
            [
                "msbuild",
                project,
                "-nologo",
                "-verbosity:quiet",
                "-getProperty:ManagePackageVersionsCentrally,CentralPackageTransitivePinningEnabled," +
                "TargetFramework,TargetFrameworks,ProjectAssetsFile,BaseIntermediateOutputPath," +
                "MSBuildProjectDirectory,RestorePackagesWithLockFile,RestoreLockedMode,NuGetLockFilePath," +
                "IsTestProject,IsTestingPlatformApplication,OutputType,TargetPath",
                "-getItem:PackageReference;PackageVersion",
            ],
            call.Arguments);
        Assert.True(evaluated.IsTestProject);
        Assert.True(evaluated.IsTestingPlatformApplication);
        Assert.Equal("Exe", evaluated.OutputType);
        Assert.Equal(
            Path.Combine(Path.GetDirectoryName(project)!, "bin", "Debug", "net8.0", "App.Tests.dll"),
            evaluated.TargetPath);
    }

    [Fact]
    public async Task MissingVerificationPropertiesKeepBackwardCompatibleDefaults()
    {
        const string output = """
            {
              "Properties": {
                "TargetFramework": "net8.0",
                "ProjectAssetsFile": "obj/project.assets.json"
              },
              "Items": {
                "PackageReference": [],
                "PackageVersion": []
              }
            }
            """;

        var evaluated = await new MsBuildProjectEvaluator(
            new RecordingProcessRunner(new ProcessResult(0, output, string.Empty))).EvaluateAsync(
                Absolute("evaluation-defaults", "App.csproj"),
                TestContext.Current.CancellationToken);

        Assert.False(evaluated.IsTestProject);
        Assert.False(evaluated.IsTestingPlatformApplication);
        Assert.Null(evaluated.OutputType);
        Assert.Null(evaluated.TargetPath);
    }

    [Fact]
    public async Task EvaluationUsesTheRequestedVerificationConfigurationForEveryFramework()
    {
        const string output = """
            {
              "Properties": {
                "TargetFrameworks": "net8.0;net9.0",
                "ProjectAssetsFile": "obj/project.assets.json"
              },
              "Items": {
                "PackageReference": [],
                "PackageVersion": []
              }
            }
            """;
        var runner = new RecordingProcessRunner(new ProcessResult(0, output, string.Empty));
        var project = Absolute("evaluation-configuration", "App.csproj");

        await new MsBuildProjectEvaluator(runner).EvaluateAsync(
            project,
            new XmlItemLineLocator(),
            "Release",
            TestContext.Current.CancellationToken);

        Assert.Equal(3, runner.Calls.Count);
        Assert.All(runner.Calls, call =>
            Assert.Contains("-property:Configuration=Release", call.Arguments));
    }

    [Fact]
    public async Task MultiTargetEvaluationQueriesEveryFrameworkWithTheSameBoundedPropertySet()
    {
        const string output = """
            {
              "Properties": {
                "TargetFrameworks": "net8.0;net9.0",
                "ProjectAssetsFile": "obj/project.assets.json"
              },
              "Items": {
                "PackageReference": [],
                "PackageVersion": []
              }
            }
            """;
        var runner = new RecordingProcessRunner(new ProcessResult(0, output, string.Empty));

        await new MsBuildProjectEvaluator(runner).EvaluateAsync(
            Absolute("evaluation-multitarget", "App.csproj"),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, runner.Calls.Count);
        Assert.DoesNotContain(runner.Calls[0].Arguments, argument =>
            argument.StartsWith("-property:TargetFramework=", StringComparison.Ordinal));
        Assert.Contains("-property:TargetFramework=net8.0", runner.Calls[1].Arguments);
        Assert.Contains("-property:TargetFramework=net9.0", runner.Calls[2].Arguments);
        Assert.All(runner.Calls, call =>
        {
            var query = Assert.Single(
                call.Arguments,
                argument => argument.StartsWith("-getProperty:", StringComparison.Ordinal));
            Assert.EndsWith(
                "IsTestProject,IsTestingPlatformApplication,OutputType,TargetPath",
                query,
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task EvaluationRejectsTruncatedStandardErrorEvenWhenTheProcessSucceeds()
    {
        const string output =
            "{\"Properties\":{\"TargetFramework\":\"net8.0\"},\"Items\":{\"PackageReference\":[],\"PackageVersion\":[]}}";
        using var reader = new StringReader(new string('x', 64));
        var truncatedError = await ProcessRunner.ReadBoundedAsync(
            reader,
            maximumCharacters: 16,
            TestContext.Current.CancellationToken);
        var evaluator = new MsBuildProjectEvaluator(
            new RecordingProcessRunner(new ProcessResult(0, output, truncatedError)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => evaluator.EvaluateAsync(
            Absolute("evaluation-truncated", "App.csproj"),
            TestContext.Current.CancellationToken));

        Assert.Contains("safety limit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SingleSolutionIsBuiltFirstWithOnlyItsOmittedProjectsAfterIt()
    {
        var root = Absolute("single-solution");
        var solution = Path.Combine(root, "Repository.slnx");
        var included = Path.Combine(root, "Included.csproj");
        var omittedA = Path.Combine(root, "A.Omitted.csproj");
        var omittedZ = Path.Combine(root, "Z.Omitted.csproj");
        var discovery = new DiscoveryResult(
            root,
            [solution, solution],
            [omittedZ, included, omittedA, omittedA],
            [omittedZ, solution, omittedA, omittedZ]);

        var plan = new VerificationPlanBuilder().Build(
            discovery,
            [Evaluated(omittedZ), Evaluated(included), Evaluated(omittedA)]);

        Assert.Equal(
            [solution, omittedA, omittedZ],
            plan.BuildTargets.Select(target => target.Path));
        Assert.Equal(VerificationBuildTargetKind.Solution, plan.BuildTargets[0].Kind);
        Assert.All(plan.BuildTargets.Skip(1), target => Assert.Equal(VerificationBuildTargetKind.Project, target.Kind));
        Assert.Equal(plan.BuildTargets.Count, plan.BuildTargets.Select(target => target.Path).Distinct(PathComparer).Count());
    }

    [Fact]
    public void MultipleSolutionsBuildEachProjectOnceInsteadOfEitherSolution()
    {
        var root = Absolute("multiple-solutions");
        var app = Path.Combine(root, "App.csproj");
        var library = Path.Combine(root, "Library.csproj");
        var discovery = new DiscoveryResult(
            root,
            [Path.Combine(root, "B.sln"), Path.Combine(root, "A.sln")],
            [library, app, library],
            [Path.Combine(root, "B.sln"), Path.Combine(root, "A.sln")]);

        var plan = new VerificationPlanBuilder().Build(
            discovery,
            [Evaluated(library), Evaluated(app)]);

        Assert.Equal([app, library], plan.BuildTargets.Select(target => target.Path));
        Assert.All(plan.BuildTargets, target => Assert.Equal(VerificationBuildTargetKind.Project, target.Kind));
    }

    [Fact]
    public void ProjectOnlyDiscoveryBuildsProjectsInDeterministicOrderWithoutDuplicates()
    {
        var root = Absolute("project-only");
        var a = Path.Combine(root, "A.csproj");
        var b = Path.Combine(root, "B.csproj");
        var discovery = new DiscoveryResult(root, [], [b, a, b], [b, a, b]);

        var plan = new VerificationPlanBuilder().Build(
            discovery,
            [Evaluated(b), Evaluated(a)]);

        Assert.Equal([a, b], plan.BuildTargets.Select(target => target.Path));
    }

    [Fact]
    public void TestSelectionUsesOnlyExplicitMsBuildMetadata()
    {
        var root = Absolute("explicit-tests");
        var misleadingName = Path.Combine(root, "Definitely.Tests.csproj");
        var actualTest = Path.Combine(root, "Application.csproj");
        var mtpButNotTest = Path.Combine(root, "MtpConfiguredLibrary.csproj");
        var discovery = new DiscoveryResult(
            root,
            [],
            [misleadingName, actualTest, mtpButNotTest],
            [misleadingName, actualTest, mtpButNotTest]);

        var plan = new VerificationPlanBuilder().Build(
            discovery,
            [
                Evaluated(misleadingName, isTestProject: false),
                Evaluated(actualTest, isTestProject: true),
                Evaluated(mtpButNotTest, isTestProject: false, isMtp: true),
            ]);

        var selected = Assert.Single(plan.TestProjects);
        Assert.Equal(actualTest, selected.ProjectPath);
        Assert.Equal(VerificationTestRunnerKind.VSTest, selected.Runner);
    }

    [Fact]
    public void TestRunnerComesExplicitlyFromIsTestingPlatformApplication()
    {
        var root = Absolute("test-runners");
        var vstest = Path.Combine(root, "One.csproj");
        var mtp = Path.Combine(root, "Two.csproj");
        var discovery = new DiscoveryResult(root, [], [mtp, vstest], [mtp, vstest]);

        var plan = new VerificationPlanBuilder().Build(
            discovery,
            [
                Evaluated(mtp, isTestProject: true, isMtp: true),
                Evaluated(vstest, isTestProject: true, isMtp: false),
            ]);

        Assert.Collection(
            plan.TestProjects,
            item =>
            {
                Assert.Equal(vstest, item.ProjectPath);
                Assert.Equal(VerificationTestRunnerKind.VSTest, item.Runner);
            },
            item =>
            {
                Assert.Equal(mtp, item.ProjectPath);
                Assert.Equal(VerificationTestRunnerKind.MicrosoftTestingPlatform, item.Runner);
            });
    }

    [Fact]
    public void NoExplicitTestProjectsProducesAnEmptyTestPlan()
    {
        var project = Absolute("no-tests", "TestNamedLibrary.csproj");
        var discovery = new DiscoveryResult(project, [], [project], [project]);

        var plan = new VerificationPlanBuilder().Build(discovery, [Evaluated(project)]);

        Assert.Empty(plan.TestProjects);
    }

    [Fact]
    public void TestMetadataIsNormalizedDeterministically()
    {
        var project = Absolute("test-metadata", "App.csproj");
        var discovery = new DiscoveryResult(project, [], [project], [project]);
        var evaluation = Evaluated(project, isTestProject: true) with
        {
            TargetFrameworks = ["net9.0", "net8.0", "NET8.0", " "],
            OutputType = "  Exe  ",
            TargetPath = Path.Combine("bin", "Debug", "App.dll"),
        };

        var test = Assert.Single(new VerificationPlanBuilder().Build(discovery, [evaluation]).TestProjects);

        Assert.Equal(["net8.0", "net9.0"], test.TargetFrameworks);
        Assert.Equal("Exe", test.OutputType);
        Assert.Equal(
            Path.Combine(Path.GetDirectoryName(project)!, "bin", "Debug", "App.dll"),
            test.TargetPath);
    }

    [Fact]
    public void MissingDuplicateAndUndiscoveredEvaluationMetadataAreRejected()
    {
        var root = Absolute("invalid-metadata");
        var app = Path.Combine(root, "App.csproj");
        var other = Path.Combine(root, "Other.csproj");
        var discovery = new DiscoveryResult(root, [], [app], [app]);
        var builder = new VerificationPlanBuilder();

        Assert.Throws<InvalidDataException>(() => builder.Build(discovery, []));
        Assert.Throws<InvalidDataException>(() => builder.Build(
            discovery,
            [Evaluated(app), Evaluated(app)]));
        Assert.Throws<InvalidDataException>(() => builder.Build(
            discovery,
            [Evaluated(app), Evaluated(other)]));
    }

    [Fact]
    public void UnsafeSingleSolutionRestoreTargetsAreRejected()
    {
        var root = Absolute("invalid-omitted");
        var project = Path.Combine(root, "App.csproj");
        var solution = Path.Combine(root, "Repository.sln");
        var unknown = Path.Combine(root, "Unknown.csproj");
        var discovery = new DiscoveryResult(root, [solution], [project], [solution, unknown]);

        var exception = Assert.Throws<InvalidDataException>(() =>
            new VerificationPlanBuilder().Build(discovery, [Evaluated(project)]));

        Assert.Contains("not a discovered", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RelativePathsAreRejectedInsteadOfDependingOnTheWorkingDirectory()
    {
        var discovery = new DiscoveryResult(".", [], ["App.csproj"], ["App.csproj"]);

        Assert.Throws<ArgumentException>(() =>
            new VerificationPlanBuilder().Build(discovery, [Evaluated(Absolute("relative", "App.csproj"))]));
    }

    [Fact]
    public void ProjectAndTargetFrameworkSafetyLimitsAreEnforced()
    {
        var root = Absolute("limits");
        var tooManyProjects = Enumerable.Range(0, ProjectDiscovery.MaximumProjects + 1)
            .Select(index => Path.Combine(root, $"Project{index:D5}.csproj"))
            .ToArray();
        var project = Path.Combine(root, "FrameworkHeavy.csproj");
        var excessiveFrameworks = Enumerable.Range(0, MsBuildProjectEvaluator.MaximumTargetFrameworksPerProject + 1)
            .Select(index => $"net8.0-f{index}")
            .ToArray();
        var builder = new VerificationPlanBuilder();

        Assert.Throws<InvalidDataException>(() => builder.Build(
            new DiscoveryResult(root, [], tooManyProjects, tooManyProjects),
            []));
        Assert.Throws<InvalidDataException>(() => builder.Build(
            new DiscoveryResult(root, [], [project], [project]),
            [Evaluated(project) with { TargetFrameworks = excessiveFrameworks }]));
    }

    private static EvaluatedProject Evaluated(
        string projectPath,
        bool isTestProject = false,
        bool isMtp = false) => new(
        projectPath,
        false,
        false,
        ["net8.0"],
        [],
        [],
        Path.Combine(Path.GetDirectoryName(projectPath)!, "obj", "project.assets.json"),
        IsTestProject: isTestProject,
        IsTestingPlatformApplication: isMtp,
        OutputType: "Library",
        TargetPath: Path.Combine(Path.GetDirectoryName(projectPath)!, "bin", "App.dll"));

    private static string Absolute(params string[] segments) =>
        Path.GetFullPath(Path.Combine([Path.GetTempPath(), "PackageMedic.VerificationPlan", .. segments]));

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed class RecordingProcessRunner(ProcessResult result) : IProcessRunner
    {
        public List<ProcessCall> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            Calls.Add(new ProcessCall(fileName, arguments.ToArray(), workingDirectory));
            return Task.FromResult(result);
        }
    }

    private sealed record ProcessCall(
        string FileName,
        IReadOnlyList<string> Arguments,
        string WorkingDirectory);
}
