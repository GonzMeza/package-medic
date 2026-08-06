using System.ComponentModel;

namespace PackageMedic.Core.Tests;

public sealed class BuildVerificationRunnerTests
{
    [Fact]
    public async Task BuildsTargetsSequentiallyWithTheOnlySupportedCommandShape()
    {
        using var snapshot = new TestSnapshot();
        var solution = snapshot.File("Repository.slnx");
        var omitted = snapshot.File("src/Omitted.csproj");
        var runner = new RecordingProcessRunner(async (_, _, token) =>
        {
            await Task.Delay(20, token);
            return Success;
        });
        var plan = Plan(
            new VerificationBuildTarget(solution, VerificationBuildTargetKind.Solution),
            new VerificationBuildTarget(omitted, VerificationBuildTargetKind.Project));

        var result = await new BuildVerificationRunner(runner).RunAsync(
            plan,
            snapshot.Root,
            "Custom Config",
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(VerificationStageStatus.Passed, result.Evidence.Status);
        Assert.Empty(result.Errors);
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(1, runner.MaximumConcurrency);
        Assert.Equal(
            [
                "build", solution, "--no-restore", "--nologo", "--verbosity", "minimal",
                "--configuration", "Custom Config", "--property:UseSharedCompilation=false",
            ],
            runner.Calls[0].Arguments);
        Assert.Equal(
            [
                "build", omitted, "--no-restore", "--nologo", "--verbosity", "minimal",
                "--configuration", "Custom Config", "--property:UseSharedCompilation=false",
            ],
            runner.Calls[1].Arguments);
        Assert.All(runner.Calls, call => Assert.Equal("dotnet", call.FileName));
        Assert.Equal(Path.GetDirectoryName(solution), runner.Calls[0].WorkingDirectory);
        Assert.Equal(Path.GetDirectoryName(omitted), runner.Calls[1].WorkingDirectory);
        Assert.Equal(
            [solution, omitted],
            result.Targets.Select(target => target.Target));
        Assert.All(result.Targets, target => Assert.Equal(VerificationStageStatus.Passed, target.Status));
    }

    [Fact]
    public async Task DeterministicBuildFailureStopsThePlanAndSanitizesItsError()
    {
        using var snapshot = new TestSnapshot();
        var first = snapshot.File("A.csproj");
        var failing = snapshot.File("B.csproj");
        var neverReached = snapshot.File("C.csproj");
        var runner = new RecordingProcessRunner((index, _, _) => Task.FromResult(index == 0
            ? Success
            : new ProcessResult(
                1,
                string.Empty,
                "Program.cs(1,1): error CS1002: token=secret\u001b[31m")));

        var result = await new BuildVerificationRunner(runner).RunAsync(
            Plan(
                new VerificationBuildTarget(first, VerificationBuildTargetKind.Project),
                new VerificationBuildTarget(failing, VerificationBuildTargetKind.Project),
                new VerificationBuildTarget(neverReached, VerificationBuildTargetKind.Project)),
            snapshot.Root,
            "Release",
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(VerificationStageStatus.Failed, result.Evidence.Status);
        Assert.Equal(VerificationFailureKind.BuildFailed, result.Evidence.FailureKind);
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(2, result.Targets.Count);
        var blocked = result.Targets[1];
        Assert.Equal(VerificationStageStatus.Failed, blocked.Status);
        Assert.Equal(VerificationFailureKind.BuildFailed, blocked.FailureKind);
        Assert.Equal(1, blocked.ExitCode);
        var error = Assert.Single(result.Errors);
        Assert.Equal(error, blocked.Error);
        Assert.Contains("compiler failure", error, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', error);
    }

    [Fact]
    public async Task TruncatedOutputIsIncompleteAndTakesPrecedenceOverTheExitCode()
    {
        using var snapshot = new TestSnapshot();
        var target = snapshot.File("App.csproj");
        using var reader = new StringReader(new string('x', 64));
        var truncated = await ProcessRunner.ReadBoundedAsync(
            reader,
            16,
            TestContext.Current.CancellationToken);
        var runner = new RecordingProcessRunner(
            (_, _, _) => Task.FromResult(new ProcessResult(1, string.Empty, truncated)));

        var result = await new BuildVerificationRunner(runner).RunAsync(
            Plan(new VerificationBuildTarget(target, VerificationBuildTargetKind.Project)),
            snapshot.Root,
            "Release",
            TestContext.Current.CancellationToken);

        Assert.Equal(VerificationStageStatus.Incomplete, result.Evidence.Status);
        Assert.Equal(VerificationFailureKind.OutputLimitExceeded, result.Evidence.FailureKind);
        Assert.Equal(VerificationFailureKind.OutputLimitExceeded, Assert.Single(result.Targets).FailureKind);
    }

    [Theory]
    [InlineData("error MSB4236: The SDK could not be resolved")]
    [InlineData("error NETSDK1147: A workload must be installed")]
    [InlineData("No space left on device; error CS1002")]
    [InlineData("Build exited without a compiler diagnostic")]
    public async Task OperationalOrUnclassifiedNonzeroExitsRemainIncomplete(string output)
    {
        using var snapshot = new TestSnapshot();
        var target = snapshot.File("App.csproj");
        var runner = new RecordingProcessRunner(
            (_, _, _) => Task.FromResult(new ProcessResult(1, string.Empty, output)));

        var result = await new BuildVerificationRunner(runner).RunAsync(
            Plan(new VerificationBuildTarget(target, VerificationBuildTargetKind.Project)),
            snapshot.Root,
            "Release",
            TestContext.Current.CancellationToken);

        Assert.Equal(VerificationStageStatus.Incomplete, result.Evidence.Status);
        Assert.Equal(VerificationFailureKind.Unknown, result.Evidence.FailureKind);
        Assert.DoesNotContain(output, Assert.Single(result.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PerTargetAndTotalTimeoutsAreClassifiedAsIncomplete()
    {
        using var snapshot = new TestSnapshot();
        var target = snapshot.File("App.csproj");
        var plan = Plan(new VerificationBuildTarget(target, VerificationBuildTargetKind.Project));

        var perTarget = await new BuildVerificationRunner(
            new RecordingProcessRunner(NeverCompletes),
            TimeSpan.FromMilliseconds(25),
            TimeSpan.FromSeconds(1)).RunAsync(
                plan,
                snapshot.Root,
                "Release",
                CancellationToken.None);
        var total = await new BuildVerificationRunner(
            new RecordingProcessRunner(NeverCompletes),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(25)).RunAsync(
                plan,
                snapshot.Root,
                "Release",
                CancellationToken.None);

        Assert.Equal(VerificationFailureKind.TimedOut, perTarget.Evidence.FailureKind);
        Assert.Equal(VerificationFailureKind.TimedOut, total.Evidence.FailureKind);
        Assert.Equal(VerificationStageStatus.Incomplete, perTarget.Evidence.Status);
        Assert.Equal(VerificationStageStatus.Incomplete, total.Evidence.Status);

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
    public async Task CallerCancellationIsDistinguishedFromTimeout()
    {
        using var snapshot = new TestSnapshot();
        var target = snapshot.File("App.csproj");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runner = new RecordingProcessRunner(async (_, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Success;
        });

        var result = await new BuildVerificationRunner(runner).RunAsync(
            Plan(new VerificationBuildTarget(target, VerificationBuildTargetKind.Project)),
            snapshot.Root,
            "Release",
            cancellation.Token);

        Assert.Equal(VerificationStageStatus.Incomplete, result.Evidence.Status);
        Assert.Equal(VerificationFailureKind.Cancelled, result.Evidence.FailureKind);
    }

    [Fact]
    public async Task ProcessStartAndUnexpectedFailuresRemainOperational()
    {
        using var snapshot = new TestSnapshot();
        var target = snapshot.File("App.csproj");
        var plan = Plan(new VerificationBuildTarget(target, VerificationBuildTargetKind.Project));

        var startFailure = await new BuildVerificationRunner(new RecordingProcessRunner(
            (_, _, _) => throw new Win32Exception("token=start-secret"))).RunAsync(
                plan,
                snapshot.Root,
                "Release",
                TestContext.Current.CancellationToken);
        var unknown = await new BuildVerificationRunner(new RecordingProcessRunner(
            (_, _, _) => throw new ApplicationException("password=unknown-secret"))).RunAsync(
                plan,
                snapshot.Root,
                "Release",
                TestContext.Current.CancellationToken);

        Assert.Equal(VerificationFailureKind.ProcessStartFailed, startFailure.Evidence.FailureKind);
        Assert.Equal(VerificationFailureKind.Unknown, unknown.Evidence.FailureKind);
        Assert.DoesNotContain("start-secret", Assert.Single(startFailure.Errors), StringComparison.Ordinal);
        Assert.DoesNotContain("unknown-secret", Assert.Single(unknown.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailureDetailsAreBoundedAfterSanitization()
    {
        using var snapshot = new TestSnapshot();
        var target = snapshot.File("App.csproj");
        var rawError = "Program.cs(1,1): error CS1002: token=top-secret " +
            new string('x', BuildVerificationRunner.MaximumErrorSourceCharacters * 2);
        var runner = new RecordingProcessRunner(
            (_, _, _) => Task.FromResult(new ProcessResult(1, string.Empty, rawError)));

        var result = await new BuildVerificationRunner(runner).RunAsync(
            Plan(new VerificationBuildTarget(target, VerificationBuildTargetKind.Project)),
            snapshot.Root,
            "Release",
            TestContext.Current.CancellationToken);

        var error = Assert.Single(result.Errors);
        Assert.InRange(error.Length, 1, BuildVerificationRunner.MaximumErrorCharacters);
        Assert.DoesNotContain("top-secret", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorRejectsUnboundedTimeoutsButAcceptsTheOneHourBoundary()
    {
        var runner = new RecordingProcessRunner((_, _, _) => Task.FromResult(Success));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BuildVerificationRunner(runner, TimeSpan.Zero, TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BuildVerificationRunner(runner, TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BuildVerificationRunner(runner, TimeSpan.FromHours(1) + TimeSpan.FromTicks(1), TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BuildVerificationRunner(runner, TimeSpan.FromHours(1), TimeSpan.FromHours(1) + TimeSpan.FromTicks(1)));

        _ = new BuildVerificationRunner(runner, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task EmptyPlanIsIncompleteWithoutStartingAProcess()
    {
        using var snapshot = new TestSnapshot();
        var runner = new RecordingProcessRunner((_, _, _) => Task.FromResult(Success));

        var result = await new BuildVerificationRunner(runner).RunAsync(
            new VerificationPlan([], []),
            snapshot.Root,
            "Release",
            TestContext.Current.CancellationToken);

        Assert.Equal(VerificationStageStatus.Incomplete, result.Evidence.Status);
        Assert.Equal(VerificationFailureKind.Unknown, result.Evidence.FailureKind);
        Assert.Empty(result.Targets);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task InvalidConfigurationAndTargetsAreRejectedBeforeExecution()
    {
        using var snapshot = new TestSnapshot();
        using var outside = new TestSnapshot();
        var inside = snapshot.File("App.csproj");
        var outsideTarget = outside.File("Outside.csproj");
        var runner = new RecordingProcessRunner((_, _, _) => Task.FromResult(Success));
        var verifier = new BuildVerificationRunner(runner);

        await Assert.ThrowsAsync<ArgumentException>(() => verifier.RunAsync(
            Plan(new VerificationBuildTarget(inside, VerificationBuildTargetKind.Project)),
            snapshot.Root,
            "Release\nInjected",
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => verifier.RunAsync(
            Plan(new VerificationBuildTarget(inside, VerificationBuildTargetKind.Project)),
            snapshot.Root,
            new string('x', BuildVerificationRunner.MaximumConfigurationCharacters + 1),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => verifier.RunAsync(
            Plan(new VerificationBuildTarget(outsideTarget, VerificationBuildTargetKind.Project)),
            snapshot.Root,
            "Release",
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => verifier.RunAsync(
            Plan(
                new VerificationBuildTarget(inside, VerificationBuildTargetKind.Project),
                new VerificationBuildTarget(inside, VerificationBuildTargetKind.Project)),
            snapshot.Root,
            "Release",
            TestContext.Current.CancellationToken));

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task TargetCountLimitIsCheckedBeforeInspectingIndividualPaths()
    {
        using var snapshot = new TestSnapshot();
        var target = snapshot.File("App.csproj");
        var targets = Enumerable.Range(0, VerificationPlanBuilder.MaximumBuildTargets + 1)
            .Select(_ => new VerificationBuildTarget(target, VerificationBuildTargetKind.Project))
            .ToArray();
        var runner = new RecordingProcessRunner((_, _, _) => Task.FromResult(Success));

        await Assert.ThrowsAsync<InvalidDataException>(() => new BuildVerificationRunner(runner).RunAsync(
            new VerificationPlan(targets, []),
            snapshot.Root,
            "Release",
            TestContext.Current.CancellationToken));

        Assert.Empty(runner.Calls);
    }

    private static ProcessResult Success { get; } = new(0, string.Empty, string.Empty);

    private static VerificationPlan Plan(params VerificationBuildTarget[] targets) => new(targets, []);

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
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("PackageMedic.BuildVerification.");

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
