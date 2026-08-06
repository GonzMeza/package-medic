using System.ComponentModel;
using System.Security;
using System.Text;

namespace PackageMedic.Core.Tests;

public sealed class TestVerificationRunnerTests
{
    [Fact]
    public async Task VSTestUsesItsFixedCommandAndRetainsOnlyPortableEvidence()
    {
        using var snapshot = new TestSnapshot();
        var project = snapshot.File("tests/App.Tests.csproj");
        string? resultsDirectory = null;
        var runner = new RecordingProcessRunner((_, arguments, _) =>
        {
            resultsDirectory = FindResultsDirectory(arguments);
            WriteTrx(resultsDirectory, Passed("Tests.App", "Works"));
            return Task.FromResult(Success);
        });

        var result = await new TestVerificationRunner(runner).RunAsync(
            Plan(TestProject(project, VerificationTestRunnerKind.VSTest)),
            snapshot.Root,
            "Custom Config",
            TestContext.Current.CancellationToken);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(
            [
                "test", project, "--no-build", "--no-restore", "--nologo", "--configuration",
                "Custom Config", "--logger", "trx", "--results-directory", resultsDirectory!,
            ],
            call.Arguments);
        Assert.Equal("dotnet", call.FileName);
        Assert.Equal(Path.GetDirectoryName(project), call.WorkingDirectory);
        Assert.NotNull(resultsDirectory);
        Assert.False(ProjectDiscovery.IsSafelyContained(snapshot.Root, resultsDirectory!));
        Assert.False(Directory.Exists(resultsDirectory));
        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Passed);
        Assert.Equal(0, result.Failed);
        var projectResult = Assert.Single(result.Projects);
        Assert.Equal("tests/App.Tests.csproj", projectResult.Project);
        Assert.False(Path.IsPathFullyQualified(projectResult.Project));
        Assert.Empty(projectResult.FailedTestIdentities);
    }

    [Fact]
    public async Task MicrosoftTestingPlatformUsesTheVSTestBridgeWithoutNativeRunnerSelection()
    {
        using var snapshot = new TestSnapshot();
        var project = snapshot.File("Mtp.Tests.csproj");
        string? resultsDirectory = null;
        var runner = new RecordingProcessRunner((_, arguments, _) =>
        {
            resultsDirectory = FindResultsDirectory(arguments);
            WriteTrx(resultsDirectory, Passed("Tests.Mtp", "Works"), fileName: "results.trx");
            return Task.FromResult(Success);
        });

        var result = await new TestVerificationRunner(runner).RunAsync(
            Plan(TestProject(project, VerificationTestRunnerKind.MicrosoftTestingPlatform)),
            snapshot.Root,
            "Release",
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(
            [
                "test", project, "--no-build", "--no-restore", "--configuration",
                "Release", "--results-directory", resultsDirectory!, "--", "--report-trx",
            ],
            Assert.Single(runner.Calls).Arguments);
    }

    [Fact]
    public async Task NativeMicrosoftTestingPlatformRequiresRepositorySelectionAndDotNet10()
    {
        using var snapshot = new TestSnapshot();
        var project = snapshot.File("Mtp.Tests.csproj");
        File.WriteAllText(
            snapshot.File("global.json"),
            """{"test":{"runner":"Microsoft.Testing.Platform"}}""");
        string? resultsDirectory = null;
        var runner = new RecordingProcessRunner((_, arguments, _) =>
        {
            if (arguments.SequenceEqual(["--version"]))
            {
                return Task.FromResult(new ProcessResult(0, "10.0.100\n", string.Empty));
            }

            resultsDirectory = FindResultsDirectory(arguments);
            WriteTrx(resultsDirectory, Passed("Tests.Mtp", "Works"));
            return Task.FromResult(Success);
        });

        var result = await new TestVerificationRunner(runner).RunAsync(
            Plan(TestProject(project, VerificationTestRunnerKind.MicrosoftTestingPlatform)),
            snapshot.Root,
            "Release",
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(
            [
                "test", "--project", project, "--no-build", "--no-restore", "--configuration",
                "Release", "--results-directory", resultsDirectory!, "--", "--report-trx",
            ],
            runner.Calls[1].Arguments);
    }

    [Fact]
    public async Task NativeMicrosoftTestingPlatformRefusesMixedOrPreDotNet10Execution()
    {
        using var mixedSnapshot = new TestSnapshot();
        var vstest = mixedSnapshot.File("Legacy.Tests.csproj");
        File.WriteAllText(
            mixedSnapshot.File("global.json"),
            """{"test":{"runner":"Microsoft.Testing.Platform"}}""");
        var mixedRunner = new RecordingProcessRunner((_, _, _) => Task.FromResult(Success));

        var mixed = await new TestVerificationRunner(mixedRunner).RunAsync(
            Plan(TestProject(vstest, VerificationTestRunnerKind.VSTest)),
            mixedSnapshot.Root,
            "Release",
            TestContext.Current.CancellationToken);

        Assert.Equal(VerificationFailureKind.UnsupportedRunner, mixed.Evidence.FailureKind);
        Assert.Empty(mixedRunner.Calls);

        using var oldSdkSnapshot = new TestSnapshot();
        var mtp = oldSdkSnapshot.File("Mtp.Tests.csproj");
        File.WriteAllText(
            oldSdkSnapshot.File("global.json"),
            """{"test":{"runner":"Microsoft.Testing.Platform"}}""");
        var oldSdkRunner = new RecordingProcessRunner(
            (_, _, _) => Task.FromResult(new ProcessResult(0, "9.0.300\n", string.Empty)));

        var oldSdk = await new TestVerificationRunner(oldSdkRunner).RunAsync(
            Plan(TestProject(mtp, VerificationTestRunnerKind.MicrosoftTestingPlatform)),
            oldSdkSnapshot.Root,
            "Release",
            TestContext.Current.CancellationToken);

        Assert.Equal(VerificationFailureKind.UnsupportedRunner, oldSdk.Evidence.FailureKind);
        Assert.Single(oldSdkRunner.Calls);
    }

    [Fact]
    public async Task NestedGlobalJsonSelectsTheRunnerFromTheProjectWorkingDirectory()
    {
        using var snapshot = new TestSnapshot();
        var project = snapshot.File("sub/tests/Mtp.Tests.csproj");
        File.WriteAllText(snapshot.File("global.json"), """{"test":{"runner":"VSTest"}}""");
        File.WriteAllText(
            snapshot.File("sub/global.json"),
            """{"test":{"runner":"Microsoft.Testing.Platform"}}""");
        var runner = new RecordingProcessRunner((_, arguments, _) =>
        {
            if (arguments.SequenceEqual(["--version"]))
            {
                return Task.FromResult(new ProcessResult(0, "10.0.100\n", string.Empty));
            }

            WriteTrx(FindResultsDirectory(arguments), Passed("Tests.Mtp", "Works"));
            return Task.FromResult(Success);
        });

        var result = await new TestVerificationRunner(runner).RunAsync(
            Plan(TestProject(project, VerificationTestRunnerKind.MicrosoftTestingPlatform)),
            snapshot.Root,
            "Release",
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.All(runner.Calls, call =>
            Assert.Equal(Path.GetDirectoryName(project), call.WorkingDirectory));
        Assert.Equal("--project", runner.Calls[1].Arguments[1]);
    }

    [Fact]
    public async Task ProjectsRunSequentiallyAndStopAtTheFirstCoherentFailure()
    {
        using var snapshot = new TestSnapshot();
        var first = snapshot.File("A.Tests.csproj");
        var failing = snapshot.File("B.Tests.csproj");
        var neverReached = snapshot.File("C.Tests.csproj");
        var resultsRoots = new List<string>();
        var runner = new RecordingProcessRunner(async (index, arguments, token) =>
        {
            var results = FindResultsDirectory(arguments);
            resultsRoots.Add(results);
            await Task.Delay(20, token);
            if (index == 0)
            {
                WriteTrx(results, Passed("Tests.First", "Passes"));
                return Success;
            }

            WriteTrx(results, Failed("Tests.Second", "Fails"));
            return new ProcessResult(1, string.Empty, string.Empty);
        });

        var result = await new TestVerificationRunner(runner).RunAsync(
            Plan(
                TestProject(neverReached, VerificationTestRunnerKind.VSTest),
                TestProject(failing, VerificationTestRunnerKind.VSTest),
                TestProject(first, VerificationTestRunnerKind.VSTest)),
            snapshot.Root,
            "Release",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, runner.MaximumConcurrency);
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(2, resultsRoots.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(resultsRoots, root => Assert.False(Directory.Exists(root)));
        Assert.Equal(VerificationStageStatus.Failed, result.Evidence.Status);
        Assert.Equal(VerificationFailureKind.TestsFailed, result.Evidence.FailureKind);
        Assert.Equal(2, result.Total);
        Assert.Equal(1, result.Passed);
        Assert.Equal(1, result.Failed);
        Assert.Equal(["A.Tests.csproj", "B.Tests.csproj"], result.Projects.Select(item => item.Project));
        Assert.Equal(
            ["B.Tests.csproj::Tests.Second.Fails"],
            result.FailedTestIdentities);
        Assert.All(result.FailedTestIdentities, identity => Assert.False(Path.IsPathFullyQualified(identity)));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task ExitCodeAndTrxContradictionsAreIncomplete(int exitCode, bool failingTrx)
    {
        using var snapshot = new TestSnapshot();
        var project = snapshot.File("Contradiction.Tests.csproj");
        var runner = new RecordingProcessRunner((_, arguments, _) =>
        {
            WriteTrx(
                FindResultsDirectory(arguments),
                failingTrx ? Failed("Tests.Sample", "Contradiction") : Passed("Tests.Sample", "Passes"));
            return Task.FromResult(new ProcessResult(exitCode, string.Empty, string.Empty));
        });

        var result = await new TestVerificationRunner(runner).RunAsync(
            Plan(TestProject(project, VerificationTestRunnerKind.VSTest)),
            snapshot.Root,
            "Release",
            TestContext.Current.CancellationToken);

        Assert.Equal(VerificationStageStatus.Incomplete, result.Evidence.Status);
        Assert.Equal(VerificationFailureKind.TestResultsUnavailable, result.Evidence.FailureKind);
        Assert.Contains("contradicted", Assert.Single(result.Errors), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingMtpExtensionOrTrxNeverBecomesATestFailure()
    {
        using var snapshot = new TestSnapshot();
        var project = snapshot.File("Mtp.Tests.csproj");
        var runner = new RecordingProcessRunner(
            (_, _, _) => Task.FromResult(new ProcessResult(1, string.Empty, "Unknown option --report-trx")));

        var result = await new TestVerificationRunner(runner).RunAsync(
            Plan(TestProject(project, VerificationTestRunnerKind.MicrosoftTestingPlatform)),
            snapshot.Root,
            "Release",
            TestContext.Current.CancellationToken);

        Assert.Equal(VerificationStageStatus.Incomplete, result.Evidence.Status);
        Assert.Equal(VerificationFailureKind.TestResultsUnavailable, result.Evidence.FailureKind);
        Assert.Equal(
            TrxTestEvidenceErrorKind.NoResultFiles,
            Assert.Single(result.Projects).EvidenceError?.Kind);
        Assert.DoesNotContain(runner.Calls.SelectMany(call => call.Arguments), argument =>
            argument.Equals("add", StringComparison.OrdinalIgnoreCase) ||
            argument.Equals("install", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IncompleteEvidenceStopsBeforeLaterProjects()
    {
        using var snapshot = new TestSnapshot();
        var first = snapshot.File("A.Tests.csproj");
        var incomplete = snapshot.File("B.Tests.csproj");
        var neverReached = snapshot.File("C.Tests.csproj");
        var runner = new RecordingProcessRunner((index, arguments, _) =>
        {
            if (index == 0)
            {
                WriteTrx(FindResultsDirectory(arguments), Passed("Tests.First", "Passes"));
            }

            return Task.FromResult(index == 0 ? Success : new ProcessResult(1, string.Empty, string.Empty));
        });

        var result = await new TestVerificationRunner(runner).RunAsync(
            Plan(
                TestProject(first, VerificationTestRunnerKind.VSTest),
                TestProject(incomplete, VerificationTestRunnerKind.VSTest),
                TestProject(neverReached, VerificationTestRunnerKind.VSTest)),
            snapshot.Root,
            "Release",
            TestContext.Current.CancellationToken);

        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(VerificationFailureKind.TestResultsUnavailable, result.Evidence.FailureKind);
        Assert.Equal(["A.Tests.csproj", "B.Tests.csproj"], result.Projects.Select(item => item.Project));
    }

    [Fact]
    public async Task ZeroExecutedTestsIsIncompleteInsteadOfPassing()
    {
        using var snapshot = new TestSnapshot();
        var project = snapshot.File("Empty.Tests.csproj");
        var runner = new RecordingProcessRunner((_, arguments, _) =>
        {
            WriteTrx(FindResultsDirectory(arguments));
            return Task.FromResult(Success);
        });

        var result = await new TestVerificationRunner(runner).RunAsync(
            Plan(TestProject(project, VerificationTestRunnerKind.VSTest)),
            snapshot.Root,
            "Release",
            TestContext.Current.CancellationToken);

        Assert.Equal(VerificationStageStatus.Incomplete, result.Evidence.Status);
        Assert.Equal(VerificationFailureKind.NoTestsDiscovered, result.Evidence.FailureKind);
    }

    [Fact]
    public async Task TruncatedOutputStopsBeforeTrustingAProducedTrx()
    {
        using var snapshot = new TestSnapshot();
        var project = snapshot.File("Truncated.Tests.csproj");
        using var reader = new StringReader(new string('x', 64));
        var truncated = await ProcessRunner.ReadBoundedAsync(
            reader,
            16,
            TestContext.Current.CancellationToken);
        var runner = new RecordingProcessRunner((_, arguments, _) =>
        {
            WriteTrx(FindResultsDirectory(arguments), Passed("Tests.Sample", "Passes"));
            return Task.FromResult(new ProcessResult(0, truncated, string.Empty));
        });

        var result = await new TestVerificationRunner(runner).RunAsync(
            Plan(TestProject(project, VerificationTestRunnerKind.VSTest)),
            snapshot.Root,
            "Release",
            TestContext.Current.CancellationToken);

        Assert.Equal(VerificationFailureKind.OutputLimitExceeded, result.Evidence.FailureKind);
        Assert.Equal(VerificationStageStatus.Incomplete, result.Evidence.Status);
    }

    [Fact]
    public async Task TimeoutAndCallerCancellationAreDistinguished()
    {
        using var snapshot = new TestSnapshot();
        var project = snapshot.File("Slow.Tests.csproj");
        var plan = Plan(TestProject(project, VerificationTestRunnerKind.VSTest));
        var timeout = await RunnerWithTimeouts(
            new RecordingProcessRunner(NeverCompletes),
            TimeSpan.FromMilliseconds(25),
            TimeSpan.FromSeconds(1)).RunAsync(
                plan,
                snapshot.Root,
                "Release",
                CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await RunnerWithTimeouts(
            new RecordingProcessRunner(NeverCompletes),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1)).RunAsync(
                plan,
                snapshot.Root,
                "Release",
                cancellation.Token);

        Assert.Equal(VerificationFailureKind.TimedOut, timeout.Evidence.FailureKind);
        Assert.Equal(VerificationFailureKind.Cancelled, cancelled.Evidence.FailureKind);

        static async Task<ProcessResult> NeverCompletes(
            int _,
            IReadOnlyList<string> __,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    [Fact]
    public async Task ProcessStartAndUnknownFailuresAreIncompleteAndDoNotLeakMessages()
    {
        using var snapshot = new TestSnapshot();
        var project = snapshot.File("Failures.Tests.csproj");
        var plan = Plan(TestProject(project, VerificationTestRunnerKind.VSTest));

        var start = await new TestVerificationRunner(new RecordingProcessRunner(
            (_, _, _) => throw new Win32Exception("token=secret-start C:\\private"))).RunAsync(
                plan,
                snapshot.Root,
                "Release",
                TestContext.Current.CancellationToken);
        var unknown = await new TestVerificationRunner(new RecordingProcessRunner(
            (_, _, _) => throw new ApplicationException("password=secret-unknown /private"))).RunAsync(
                plan,
                snapshot.Root,
                "Release",
                TestContext.Current.CancellationToken);

        Assert.Equal(VerificationFailureKind.ProcessStartFailed, start.Evidence.FailureKind);
        Assert.Equal(VerificationFailureKind.Unknown, unknown.Evidence.FailureKind);
        Assert.DoesNotContain("secret", Assert.Single(start.Errors), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", Assert.Single(unknown.Errors), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(snapshot.Root, Assert.Single(start.Errors), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmptyPlanIsNoTestsDiscoveredWithoutLaunchingAProcess()
    {
        using var snapshot = new TestSnapshot();
        var runner = new RecordingProcessRunner((_, _, _) => Task.FromResult(Success));

        var result = await new TestVerificationRunner(runner).RunAsync(
            new VerificationPlan([], []),
            snapshot.Root,
            "Release",
            TestContext.Current.CancellationToken);

        Assert.Equal(VerificationFailureKind.NoTestsDiscovered, result.Evidence.FailureKind);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task InvalidConfigurationRunnerPathsDuplicatesAndLimitsAreRejectedBeforeExecution()
    {
        using var snapshot = new TestSnapshot();
        using var outside = new TestSnapshot();
        var project = snapshot.File("App.Tests.csproj");
        var outsideProject = outside.File("Outside.Tests.csproj");
        var valid = TestProject(project, VerificationTestRunnerKind.VSTest);
        var runner = new RecordingProcessRunner((_, _, _) => Task.FromResult(Success));
        var verifier = new TestVerificationRunner(runner);

        await Assert.ThrowsAsync<ArgumentException>(() => verifier.RunAsync(
            Plan(valid), snapshot.Root, "Release\nInjected", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => verifier.RunAsync(
            Plan(valid), snapshot.Root, new string('x', TestVerificationRunner.MaximumConfigurationCharacters + 1),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => verifier.RunAsync(
            Plan(TestProject(outsideProject, VerificationTestRunnerKind.VSTest)),
            snapshot.Root, "Release", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => verifier.RunAsync(
            Plan(valid, valid), snapshot.Root, "Release", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => verifier.RunAsync(
            Plan(TestProject(project, (VerificationTestRunnerKind)999)),
            snapshot.Root, "Release", TestContext.Current.CancellationToken));

        var excessive = Enumerable.Range(0, VerificationPlanBuilder.MaximumTestProjects + 1)
            .Select(_ => valid)
            .ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(() => verifier.RunAsync(
            new VerificationPlan([], excessive), snapshot.Root, "Release", TestContext.Current.CancellationToken));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void ConstructorEnforcesOneHourTimeoutBoundaryAndParserLimits()
    {
        var runner = new RecordingProcessRunner((_, _, _) => Task.FromResult(Success));
        var parser = new TrxTestResultParser();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TestVerificationRunner(runner, parser, TrxTestResultLimits.Default, TimeSpan.Zero, TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TestVerificationRunner(runner, parser, TrxTestResultLimits.Default, TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TestVerificationRunner(
                runner,
                parser,
                TrxTestResultLimits.Default,
                TimeSpan.FromHours(1) + TimeSpan.FromTicks(1),
                TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TestVerificationRunner(
                runner,
                parser,
                new TrxTestResultLimits(MaximumFiles: 0),
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(1)));

        _ = RunnerWithTimeouts(runner, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
    }

    private static TestVerificationRunner RunnerWithTimeouts(
        IProcessRunner runner,
        TimeSpan projectTimeout,
        TimeSpan totalTimeout) => new(
        runner,
        new TrxTestResultParser(),
        TrxTestResultLimits.Default,
        projectTimeout,
        totalTimeout);

    private static VerificationPlan Plan(params VerificationTestProject[] projects) => new([], projects);

    private static VerificationTestProject TestProject(
        string project,
        VerificationTestRunnerKind runner) => new(
        project,
        runner,
        ["net8.0"],
        "Library",
        null);

    private static string FindResultsDirectory(IReadOnlyList<string> arguments)
    {
        var index = arguments.ToList().FindIndex(argument => argument == "--results-directory");
        Assert.InRange(index, 0, arguments.Count - 2);
        return arguments[index + 1];
    }

    private static void WriteTrx(string resultsDirectory, TestCase? test = null, string fileName = "results.trx")
    {
        Directory.CreateDirectory(resultsDirectory);
        File.WriteAllText(
            Path.Combine(resultsDirectory, fileName),
            CreateTrx(test is null ? [] : [test]),
            Encoding.UTF8);
    }

    private static string CreateTrx(params TestCase[] tests)
    {
        var passed = tests.Count(test => test.Outcome == "Passed");
        var failed = tests.Count(test => test.Outcome == "Failed");
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>")
            .Append("<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>");
        foreach (var test in tests)
        {
            builder.Append("<UnitTestResult testId=\"")
                .Append(test.Id.ToString("D"))
                .Append("\" testName=\"")
                .Append(SecurityElement.Escape(test.Method))
                .Append("\" outcome=\"")
                .Append(test.Outcome)
                .Append("\" />");
        }

        builder.Append("</Results><TestDefinitions>");
        foreach (var test in tests)
        {
            builder.Append("<UnitTest id=\"")
                .Append(test.Id.ToString("D"))
                .Append("\" name=\"")
                .Append(SecurityElement.Escape(test.Method))
                .Append("\"><TestMethod className=\"")
                .Append(SecurityElement.Escape(test.ClassName))
                .Append("\" name=\"")
                .Append(SecurityElement.Escape(test.Method))
                .Append("\" /></UnitTest>");
        }

        builder.Append("</TestDefinitions><ResultSummary outcome=\"")
            .Append(failed == 0 ? "Completed" : "Failed")
            .Append("\"><Counters total=\"")
            .Append(tests.Length)
            .Append("\" executed=\"")
            .Append(tests.Length)
            .Append("\" passed=\"")
            .Append(passed)
            .Append("\" failed=\"")
            .Append(failed)
            .Append("\" error=\"0\" timeout=\"0\" aborted=\"0\" inconclusive=\"0\" passedButRunAborted=\"0\" notRunnable=\"0\" notExecuted=\"0\" disconnected=\"0\" warning=\"0\" completed=\"0\" inProgress=\"0\" pending=\"0\" /></ResultSummary></TestRun>");
        return builder.ToString();
    }

    private static TestCase Passed(string className, string method) => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        className,
        method,
        "Passed");

    private static TestCase Failed(string className, string method) => new(
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        className,
        method,
        "Failed");

    private static ProcessResult Success { get; } = new(0, string.Empty, string.Empty);

    private sealed record TestCase(Guid Id, string ClassName, string Method, string Outcome);

    private sealed class RecordingProcessRunner(
        Func<int, IReadOnlyList<string>, CancellationToken, Task<ProcessResult>> handler) : IProcessRunner
    {
        private int active;
        private int maximumConcurrency;

        public List<ProcessCall> Calls { get; } = [];

        public int MaximumConcurrency => Volatile.Read(ref maximumConcurrency);

        public async Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            var callIndex = Calls.Count;
            Calls.Add(new ProcessCall(fileName, arguments.ToArray(), workingDirectory));
            var current = Interlocked.Increment(ref active);
            while (true)
            {
                var observed = Volatile.Read(ref maximumConcurrency);
                if (current <= observed ||
                    Interlocked.CompareExchange(ref maximumConcurrency, current, observed) == observed)
                {
                    break;
                }
            }

            try
            {
                return await handler(callIndex, arguments, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }
    }

    private sealed record ProcessCall(
        string FileName,
        IReadOnlyList<string> Arguments,
        string WorkingDirectory);

    private sealed class TestSnapshot : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("PackageMedic.TestVerification.");

        public string Root => directory.FullName;

        public string File(string relativePath)
        {
            var path = Path.GetFullPath(relativePath, Root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, string.Empty);
            return path;
        }

        public void Dispose() => directory.Delete(recursive: true);
    }
}
