using PackageMedic.Core;

namespace PackageMedic.Core.Tests;

public sealed class GitCommitInspectorTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string ChangedCommit = "89abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task ResolvesAReferenceToACanonicalCommitWithTheExpectedGitInvocation()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var runner = new RecordingProcessRunner((_, _, _, _) => Task.FromResult(
                new ProcessResult(0, Commit.ToUpperInvariant() + Environment.NewLine, string.Empty)));
            var inspector = new GitCommitInspector(runner, TimeSpan.FromSeconds(5));

            var resolved = await inspector.ResolveCommitAsync(
                repository,
                "main~1",
                TestContext.Current.CancellationToken);

            Assert.Equal(Commit, resolved);
            var invocation = Assert.Single(runner.Invocations);
            Assert.Equal("git", invocation.FileName);
            Assert.Equal(repository, invocation.WorkingDirectory);
            Assert.Equal(
                ["rev-parse", "--verify", "--end-of-options", "main~1^{commit}"],
                invocation.Arguments);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    [Fact]
    public async Task AcceptsTheExpectedHeadCommit()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var runner = SuccessfulRunner(Commit);
            var inspector = new GitCommitInspector(runner);

            await inspector.EnsureHeadEqualsAsync(
                repository,
                Commit,
                TestContext.Current.CancellationToken);

            Assert.Equal(
                ["rev-parse", "--verify", "--end-of-options", "HEAD^{commit}"],
                Assert.Single(runner.Invocations).Arguments);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsAHeadThatChangedDuringTheOperation()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var inspector = new GitCommitInspector(SuccessfulRunner(ChangedCommit));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                inspector.EnsureHeadEqualsAsync(
                    repository,
                    Commit,
                    TestContext.Current.CancellationToken));

            Assert.Contains("HEAD changed", exception.Message, StringComparison.Ordinal);
            Assert.Contains(Commit, exception.Message, StringComparison.Ordinal);
            Assert.Contains(ChangedCommit, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-commit")]
    [InlineData("0123456789abcdef0123456789abcdef0123456g")]
    [InlineData("0123456789abcdef0123456789abcdef01234567\n89abcdef0123456789abcdef0123456789abcdef")]
    public async Task RejectsInvalidGitOutput(string output)
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var runner = new RecordingProcessRunner((_, _, _, _) => Task.FromResult(
                new ProcessResult(0, output, string.Empty)));
            var inspector = new GitCommitInspector(runner);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                inspector.ResolveHeadAsync(repository, TestContext.Current.CancellationToken));

            Assert.Contains("invalid commit identifier", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsOutputBeyondTheCommitInspectionLimit()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var runner = new RecordingProcessRunner((_, _, _, _) => Task.FromResult(
                new ProcessResult(
                    0,
                    new string('a', GitCommitInspector.MaximumOutputCharacters + 1),
                    string.Empty)));
            var inspector = new GitCommitInspector(runner);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                inspector.ResolveHeadAsync(repository, TestContext.Current.CancellationToken));

            Assert.Contains("safety limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    [Fact]
    public async Task ReportsTimeoutWithoutTurningCallerCancellationIntoATimeout()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var runner = new RecordingProcessRunner(async (_, _, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            });
            var inspector = new GitCommitInspector(runner, TimeSpan.FromMilliseconds(25));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                inspector.ResolveHeadAsync(repository, CancellationToken.None));

            Assert.Contains("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    [Fact]
    public async Task PropagatesCallerCancellation()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var runner = new RecordingProcessRunner(async (_, _, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            });
            var inspector = new GitCommitInspector(runner, TimeSpan.FromSeconds(5));
            using var cancellationSource = new CancellationTokenSource();
            cancellationSource.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                inspector.ResolveHeadAsync(repository, cancellationSource.Token));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    [Fact]
    public async Task RedactsAndBoundsGitFailureDetails()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var secret = "token=do-not-print";
            var runner = new RecordingProcessRunner((_, _, _, _) => Task.FromResult(
                new ProcessResult(128, string.Empty, $"failure {secret}\u0001")));
            var inspector = new GitCommitInspector(runner);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                inspector.ResolveHeadAsync(repository, TestContext.Current.CancellationToken));

            Assert.DoesNotContain("do-not-print", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain('\u0001', exception.Message);
            Assert.Contains("token=[REDACTED]", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    [Theory]
    [InlineData("120000 blob\n")]
    [InlineData("160000 commit\n")]
    public async Task RefusesTreeEntriesThatCannotBeReproducedByVerifiedExecution(string tree)
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var inspector = new GitCommitInspector(new RecordingProcessRunner(
                (_, arguments, _, _) => Task.FromResult(arguments[0] == "ls-tree"
                    ? new ProcessResult(0, tree, string.Empty)
                    : new ProcessResult(0, Commit + Environment.NewLine, string.Empty))));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                inspector.EnsureVerificationTreeSupportedAsync(
                    repository,
                    Commit,
                    TestContext.Current.CancellationToken));

            Assert.Contains("cannot reproduce", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    [Fact]
    public async Task AcceptsRegularAndExecutableBlobsForVerifiedExecution()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var tree = "100644 blob\n" +
                       "100755 blob\n";
            var inspector = new GitCommitInspector(new RecordingProcessRunner(
                (_, _, _, _) => Task.FromResult(new ProcessResult(0, tree, string.Empty))));

            await inspector.EnsureVerificationTreeSupportedAsync(
                repository,
                Commit,
                TestContext.Current.CancellationToken);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    private static RecordingProcessRunner SuccessfulRunner(string commit) =>
        new((_, _, _, _) => Task.FromResult(
            new ProcessResult(0, commit + Environment.NewLine, string.Empty)));

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"PackageMedic.Tests.git-commit.{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class RecordingProcessRunner(
        Func<string, IReadOnlyList<string>, string, CancellationToken, Task<ProcessResult>> handler) : IProcessRunner
    {
        public List<(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory)> Invocations { get; } = [];

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            Invocations.Add((fileName, arguments.ToArray(), workingDirectory));
            return handler(fileName, arguments, workingDirectory, cancellationToken);
        }
    }
}
