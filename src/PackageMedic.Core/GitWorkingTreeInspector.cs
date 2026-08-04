namespace PackageMedic.Core;

/// <summary>
/// Verifies that a Git worktree has no tracked or untracked changes before a
/// simulation materializes its committed baseline.
/// </summary>
public sealed class GitWorkingTreeInspector
{
    internal const int MaximumStatusOutputCharacters = 1_000_000;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly IProcessRunner processRunner;
    private readonly TimeSpan timeout;

    public GitWorkingTreeInspector(IProcessRunner processRunner)
        : this(processRunner, DefaultTimeout)
    {
    }

    public GitWorkingTreeInspector(IProcessRunner processRunner, TimeSpan timeout)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        if (timeout <= TimeSpan.Zero ||
            timeout == Timeout.InfiniteTimeSpan ||
            timeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The Git status timeout must be greater than zero and no longer than one hour.");
        }

        this.timeout = timeout;
    }

    public async Task EnsureCleanAsync(
        string repositoryDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryDirectory);
        var repositoryRoot = Path.GetFullPath(repositoryDirectory);
        if (!Directory.Exists(repositoryRoot))
        {
            throw new DirectoryNotFoundException($"Git repository directory '{repositoryRoot}' does not exist.");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        ProcessResult result;
        try
        {
            result = await processRunner.RunAsync(
                "git",
                ["--no-optional-locks", "status", "--porcelain=v1", "-z", "--untracked-files=all"],
                repositoryRoot,
                timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Git status timed out after {timeout.TotalSeconds:0.###} seconds.");
        }

        if (result.StandardOutputTruncated ||
            result.StandardErrorTruncated ||
            result.StandardOutput.Length > MaximumStatusOutputCharacters ||
            result.StandardError.Length > MaximumStatusOutputCharacters)
        {
            throw new InvalidOperationException(
                $"Git status output exceeded the {MaximumStatusOutputCharacters}-character simulation safety limit.");
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Git status failed with exit code {result.ExitCode}.{CompactError(result)}");
        }

        if (!string.IsNullOrEmpty(result.StandardOutput))
        {
            // Do not echo porcelain output: filenames are repository-controlled and may
            // contain secrets, terminal controls, or paths that should not enter reports.
            throw new InvalidOperationException(
                "Dependency simulation requires a clean Git worktree, including untracked files; commit or stash the changes and try again.");
        }
    }

    public Task EnsureArchiveSemanticsAreReproducibleAsync(
        string repositoryDirectory,
        CancellationToken cancellationToken = default) =>
        EnsureArchiveSemanticsAreReproducibleAsync(repositoryDirectory, "HEAD", cancellationToken);

    public async Task EnsureArchiveSemanticsAreReproducibleAsync(
        string repositoryDirectory,
        string reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryDirectory);
        GitSnapshotProvider.ValidateReference(reference);
        var repositoryRoot = Path.GetFullPath(repositoryDirectory);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        ProcessResult result;
        try
        {
            result = await processRunner.RunAsync(
                "git",
                [
                    "--no-optional-locks", "grep", "-I", "-n", "-E",
                    "export-(ignore|subst)", reference, "--",
                    ".gitattributes", ":(glob)**/.gitattributes",
                ],
                repositoryRoot,
                timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Git attribute inspection timed out after {timeout.TotalSeconds:0.###} seconds.");
        }

        if (result.StandardOutputTruncated || result.StandardErrorTruncated)
        {
            throw new InvalidOperationException("Git attribute inspection exceeded the subprocess output safety limit.");
        }

        if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            throw new InvalidOperationException(
                "Dependency simulation refuses Git export-ignore/export-subst attributes because an archive could differ from the committed dependency inputs.");
        }

        // git grep uses 1 for a valid search with no matches.
        if (result.ExitCode is not 0 and not 1)
        {
            throw new InvalidOperationException(
                $"Git attribute inspection failed with exit code {result.ExitCode}.{CompactError(result)}");
        }

        await EnsureRepositoryLocalAttributesAreSafeAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureRepositoryLocalAttributesAreSafeAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var result = await processRunner.RunAsync(
            "git",
            ["--no-optional-locks", "rev-parse", "--git-path", "info/attributes"],
            repositoryRoot,
            timeoutSource.Token).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.StandardOutputTruncated || result.StandardErrorTruncated)
        {
            throw new InvalidOperationException(
                $"Could not locate repository-local Git attributes.{CompactError(result)}");
        }

        var value = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SingleOrDefault();
        if (value is null)
        {
            throw new InvalidOperationException("Git did not return its repository-local attributes path.");
        }

        var attributesPath = Path.GetFullPath(value, repositoryRoot);
        var info = new FileInfo(attributesPath);
        if (!info.Exists)
        {
            return;
        }

        if (info.Attributes.HasFlag(FileAttributes.Directory) ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            info.Length > PackageMedicConfigurationLoader.MaximumConfigurationCharacters)
        {
            throw new InvalidOperationException(
                "Dependency simulation refuses an unsafe or oversized repository-local Git attributes file.");
        }

        foreach (var line in File.ReadLines(attributesPath))
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (trimmed.Contains("export-ignore", StringComparison.Ordinal) ||
                trimmed.Contains("export-subst", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Dependency simulation refuses export-ignore/export-subst rules from .git/info/attributes because an archive could differ from committed HEAD.");
            }
        }
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
