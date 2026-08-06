namespace PackageMedic.Core;

/// <summary>
/// Resolves Git references to immutable commit identifiers and detects when HEAD
/// changes across a multi-step operation.
/// </summary>
public sealed class GitCommitInspector
{
    internal const int MaximumOutputCharacters = 4_096;
    internal const int MaximumTreeOutputCharacters = ProcessRunner.DefaultMaximumOutputCharacters;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly IProcessRunner processRunner;
    private readonly TimeSpan timeout;

    public GitCommitInspector(IProcessRunner processRunner)
        : this(processRunner, DefaultTimeout)
    {
    }

    public GitCommitInspector(IProcessRunner processRunner, TimeSpan timeout)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        if (timeout <= TimeSpan.Zero ||
            timeout == Timeout.InfiniteTimeSpan ||
            timeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The Git commit inspection timeout must be greater than zero and no longer than one hour.");
        }

        this.timeout = timeout;
    }

    public Task<string> ResolveHeadAsync(
        string repositoryDirectory,
        CancellationToken cancellationToken = default) =>
        ResolveCommitAsync(repositoryDirectory, "HEAD", cancellationToken);

    public async Task<string> ResolveCommitAsync(
        string repositoryDirectory,
        string reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryDirectory);
        GitSnapshotProvider.ValidateReference(reference);

        var repositoryRoot = Path.GetFullPath(repositoryDirectory);
        if (!Directory.Exists(repositoryRoot))
        {
            throw new DirectoryNotFoundException($"Git repository directory '{repositoryRoot}' does not exist.");
        }

        var result = await RunGitAsync(
            ["rev-parse", "--verify", "--end-of-options", $"{reference}^{{commit}}"],
            repositoryRoot,
            cancellationToken).ConfigureAwait(false);
        return ParseGitCommit(result.StandardOutput);
    }

    public async Task EnsureHeadEqualsAsync(
        string repositoryDirectory,
        string expectedCommit,
        CancellationToken cancellationToken = default)
    {
        var expected = NormalizeExpectedCommit(expectedCommit);
        var current = await ResolveHeadAsync(repositoryDirectory, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(expected, current, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Git HEAD changed during the operation; expected commit '{expected}', but found '{current}'.");
        }
    }

    public async Task EnsureVerificationTreeSupportedAsync(
        string repositoryDirectory,
        string commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryDirectory);
        var normalizedCommit = NormalizeExpectedCommit(commit);
        var repositoryRoot = Path.GetFullPath(repositoryDirectory);
        if (!Directory.Exists(repositoryRoot))
        {
            throw new DirectoryNotFoundException($"Git repository directory '{repositoryRoot}' does not exist.");
        }

        var result = await RunGitAsync(
            [
                "ls-tree",
                "-r",
                "--full-tree",
                "--format=%(objectmode) %(objecttype)",
                normalizedCommit,
            ],
            repositoryRoot,
            cancellationToken,
            MaximumTreeOutputCharacters).ConfigureAwait(false);
        foreach (var entry in result.StandardOutput.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var metadata = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (metadata.Length != 2 || metadata[0] is not ("100644" or "100755") ||
                !metadata[1].Equals("blob", StringComparison.Ordinal))
            {
                var kind = metadata.Length > 0 ? metadata[0] : "unknown";
                throw new InvalidOperationException(
                    $"Verified execution cannot reproduce tracked Git entry mode '{kind}'; symbolic links, submodules, and special entries are unsupported.");
            }
        }
    }

    private async Task<ProcessResult> RunGitAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        int maximumOutputCharacters = MaximumOutputCharacters)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        ProcessResult result;
        try
        {
            result = await processRunner.RunAsync(
                "git",
                arguments,
                workingDirectory,
                timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Git commit inspection timed out after {timeout.TotalSeconds:0.###} seconds.");
        }

        if (result.StandardOutputTruncated ||
            result.StandardErrorTruncated ||
            result.StandardOutput.Length > maximumOutputCharacters ||
            result.StandardError.Length > maximumOutputCharacters)
        {
            throw new InvalidOperationException(
                $"Git commit inspection output exceeded the {maximumOutputCharacters}-character safety limit.");
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Git commit inspection failed with exit code {result.ExitCode}.{CompactError(result)}");
        }

        return result;
    }

    private static string ParseGitCommit(string value)
    {
        var commit = value.Trim();
        if (commit.Contains('\n', StringComparison.Ordinal) ||
            commit.Contains('\r', StringComparison.Ordinal) ||
            (commit.Length is not 40 and not 64) ||
            !commit.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("Git returned an invalid commit identifier.");
        }

        return commit.ToLowerInvariant();
    }

    private static string NormalizeExpectedCommit(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            (value.Length is not 40 and not 64) ||
            !value.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "The expected Git commit must be a complete 40- or 64-character hexadecimal identifier.",
                nameof(value));
        }

        return value.ToLowerInvariant();
    }

    private static string CompactError(ProcessResult result)
    {
        var source = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        var redacted = ProcessRunner.RedactSecrets(source);
        var compact = string.Join(
            ' ',
            redacted.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Take(3));
        if (compact.Length == 0)
        {
            return string.Empty;
        }

        return " " + (compact.Length <= 500 ? compact : string.Concat(compact.AsSpan(0, 500), "..."));
    }
}
