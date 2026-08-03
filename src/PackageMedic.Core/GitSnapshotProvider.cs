using System.Formats.Tar;
using System.Text;

namespace PackageMedic.Core;

/// <summary>
/// A detached, tracked-files-only view of a Git commit. Disposing the snapshot removes
/// every temporary file created while materializing it.
/// </summary>
public sealed class GitSnapshot : IDisposable
{
    private readonly string ownedDirectory;
    private int disposed;

    internal GitSnapshot(
        string repositoryRoot,
        string reference,
        string commit,
        string snapshotDirectory,
        string ownedDirectory)
    {
        RepositoryRoot = repositoryRoot;
        Reference = reference;
        Commit = commit;
        SnapshotDirectory = snapshotDirectory;
        this.ownedDirectory = ownedDirectory;
    }

    public string RepositoryRoot { get; }

    public string Reference { get; }

    public string Commit { get; }

    public string SnapshotDirectory { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        GitSnapshotProvider.DeleteOwnedDirectory(ownedDirectory);
    }
}

public sealed record GitSnapshotLimits(
    long MaximumArchiveBytes,
    long MaximumExpandedBytes,
    long MaximumSingleFileBytes,
    int MaximumEntries,
    long MinimumFreeBytesAfterExtraction)
{
    public static GitSnapshotLimits Default { get; } = new(
        4L * 1024 * 1024 * 1024,
        8L * 1024 * 1024 * 1024,
        1L * 1024 * 1024 * 1024,
        250_000,
        256L * 1024 * 1024);

    public GitSnapshotLimits Validate()
    {
        if (MaximumArchiveBytes < 1 ||
            MaximumExpandedBytes < 1 ||
            MaximumSingleFileBytes < 1 ||
            MaximumSingleFileBytes > MaximumExpandedBytes ||
            MaximumEntries < 1 ||
            MinimumFreeBytesAfterExtraction < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(GitSnapshotLimits),
                "Git snapshot limits must be positive, internally consistent values.");
        }

        return this;
    }
}

/// <summary>
/// Resolves a Git revision to an immutable commit and extracts its tracked files without
/// changing HEAD, the index, or the current worktree.
/// </summary>
public sealed class GitSnapshotProvider
{
    private const string TemporaryDirectoryPrefix = "PackageMedic.GitSnapshot.";
    private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromMinutes(2);

    private readonly IProcessRunner processRunner;
    private readonly TimeSpan operationTimeout;
    private readonly string temporaryRoot;
    private readonly GitSnapshotLimits limits;

    public GitSnapshotProvider(IProcessRunner processRunner)
        : this(processRunner, DefaultOperationTimeout, Path.GetTempPath(), GitSnapshotLimits.Default)
    {
    }

    public GitSnapshotProvider(
        IProcessRunner processRunner,
        TimeSpan operationTimeout,
        string temporaryRoot)
        : this(processRunner, operationTimeout, temporaryRoot, GitSnapshotLimits.Default)
    {
    }

    public GitSnapshotProvider(
        IProcessRunner processRunner,
        TimeSpan operationTimeout,
        string temporaryRoot,
        GitSnapshotLimits limits)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryRoot);
        ArgumentNullException.ThrowIfNull(limits);
        if (operationTimeout <= TimeSpan.Zero ||
            operationTimeout == Timeout.InfiniteTimeSpan ||
            operationTimeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }

        this.processRunner = processRunner;
        this.operationTimeout = operationTimeout;
        this.temporaryRoot = Path.GetFullPath(temporaryRoot);
        this.limits = limits.Validate();
    }

    public async Task<GitSnapshot> MaterializeAsync(
        string repositoryDirectory,
        string reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryDirectory);
        ValidateReference(reference);

        var requestedDirectory = Path.GetFullPath(repositoryDirectory);
        if (!Directory.Exists(requestedDirectory))
        {
            throw new DirectoryNotFoundException($"Git repository directory '{requestedDirectory}' does not exist.");
        }

        var rootResult = await RunGitAsync(
            ["rev-parse", "--show-toplevel"],
            requestedDirectory,
            "locate the repository root",
            cancellationToken).ConfigureAwait(false);
        var repositoryRoot = ParseSingleLine(rootResult.StandardOutput, "repository root");
        if (!Path.IsPathFullyQualified(repositoryRoot))
        {
            throw new InvalidOperationException("Git returned a repository root that is not absolute.");
        }

        repositoryRoot = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(repositoryRoot))
        {
            throw new InvalidOperationException("Git returned a repository root that does not exist.");
        }

        var commitResult = await RunGitAsync(
            ["rev-parse", "--verify", "--end-of-options", $"{reference}^{{commit}}"],
            repositoryRoot,
            $"resolve Git reference '{reference}'",
            cancellationToken).ConfigureAwait(false);
        var commit = ParseCommit(commitResult.StandardOutput);

        if (IsWithin(temporaryRoot, repositoryRoot))
        {
            throw new InvalidOperationException(
                "The Git snapshot temporary directory must be outside the current repository.");
        }

        Directory.CreateDirectory(temporaryRoot);
        var ownedDirectory = Path.Combine(
            temporaryRoot,
            TemporaryDirectoryPrefix + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(ownedDirectory, "snapshot.tar");
        var snapshotDirectory = Path.Combine(ownedDirectory, "worktree");
        Directory.CreateDirectory(snapshotDirectory);

        try
        {
            await RunGitAsync(
                ["archive", "--format=tar", $"--output={archivePath}", commit],
                repositoryRoot,
                $"archive Git commit '{commit}'",
                cancellationToken).ConfigureAwait(false);
            if (!File.Exists(archivePath))
            {
                throw new InvalidOperationException("Git completed without creating the requested snapshot archive.");
            }

            var archiveLength = new FileInfo(archivePath).Length;
            if (archiveLength > limits.MaximumArchiveBytes)
            {
                throw new InvalidDataException(
                    $"The Git snapshot archive is {archiveLength} bytes, exceeding the configured {limits.MaximumArchiveBytes}-byte limit.");
            }

            EnsureExtractionCapacity(archivePath, snapshotDirectory, limits);
            using var extractionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            extractionTimeout.CancelAfter(operationTimeout);
            try
            {
                await ExtractTrackedFilesAsync(archivePath, snapshotDirectory, extractionTimeout.Token, limits)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("Timed out while extracting the Git snapshot.");
            }

            File.Delete(archivePath);
            return new GitSnapshot(
                repositoryRoot,
                reference,
                commit,
                snapshotDirectory,
                ownedDirectory);
        }
        catch
        {
            DeleteOwnedDirectory(ownedDirectory);
            throw;
        }
    }

    internal static void ValidateReference(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (!string.Equals(reference, reference.Trim(), StringComparison.Ordinal) ||
            reference.Length > 512 ||
            reference[0] == '-' ||
            reference.Any(char.IsWhiteSpace) ||
            reference.Any(char.IsControl))
        {
            throw new ArgumentException("The Git reference is not a safe revision expression.", nameof(reference));
        }
    }

    internal static async Task ExtractTrackedFilesAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken,
        GitSnapshotLimits? configuredLimits = null)
    {
        var limits = (configuredLimits ?? GitSnapshotLimits.Default).Validate();
        var root = Path.GetFullPath(destinationDirectory);
        var rootPrefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        using var stream = File.OpenRead(archivePath);
        using var reader = new TarReader(stream, leaveOpen: false);
        var entryCount = 0;
        long expandedBytes = 0;
        while (reader.GetNextEntry() is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.EntryType is TarEntryType.ExtendedAttributes or TarEntryType.GlobalExtendedAttributes)
            {
                continue;
            }

            entryCount++;
            if (entryCount > limits.MaximumEntries)
            {
                throw new InvalidDataException(
                    $"The Git snapshot contains more than the configured {limits.MaximumEntries} entries.");
            }

            var entryBytes = entry.EntryType == TarEntryType.SymbolicLink
                ? Encoding.UTF8.GetByteCount(entry.LinkName ?? string.Empty)
                : entry.Length;
            if (entryBytes < 0 || entryBytes > limits.MaximumSingleFileBytes)
            {
                throw new InvalidDataException(
                    $"Git snapshot entry '{entry.Name}' exceeds the configured {limits.MaximumSingleFileBytes}-byte single-file limit.");
            }

            try
            {
                expandedBytes = checked(expandedBytes + entryBytes);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException("The Git snapshot expanded size is invalid.", exception);
            }

            if (expandedBytes > limits.MaximumExpandedBytes)
            {
                throw new InvalidDataException(
                    $"The Git snapshot exceeds the configured {limits.MaximumExpandedBytes}-byte expanded-size limit.");
            }

            var archiveName = entry.EntryType == TarEntryType.Directory
                ? entry.Name.TrimEnd('/')
                : entry.Name;
            if (OperatingSystem.IsWindows() && archiveName.Contains('\\'))
            {
                throw new InvalidDataException(
                    "The Git archive contains a tracked path with a non-portable separator.");
            }

            var normalizedName = archiveName.Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(normalizedName) || Path.IsPathFullyQualified(normalizedName))
            {
                throw new InvalidDataException("The Git archive contains an invalid tracked path.");
            }

            ValidateTrackedPath(normalizedName);

            var destination = Path.GetFullPath(Path.Combine(root, normalizedName));
            if (!destination.StartsWith(rootPrefix, comparison))
            {
                throw new InvalidDataException("The Git archive contains a tracked path outside the snapshot root.");
            }

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(destination);
                    break;

                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                case TarEntryType.ContiguousFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        if (entry.DataStream is not null)
                        {
                            await entry.DataStream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    break;

                case TarEntryType.SymbolicLink:
                    // Git stores a symbolic link as the UTF-8 text of its target. Keeping that
                    // representation as a regular file prevents traversal outside the snapshot.
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await using (var writer = new StreamWriter(output, new UTF8Encoding(false)))
                    {
                        await writer.WriteAsync((entry.LinkName ?? string.Empty).AsMemory(), cancellationToken)
                            .ConfigureAwait(false);
                    }

                    break;

                default:
                    throw new InvalidDataException(
                        $"The Git archive contains unsupported entry type '{entry.EntryType}'.");
            }
        }
    }

    private static void EnsureExtractionCapacity(
        string archivePath,
        string destinationDirectory,
        GitSnapshotLimits limits)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(destinationDirectory));
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            var available = new DriveInfo(root).AvailableFreeSpace;
            var archiveLength = new FileInfo(archivePath).Length;
            var required = checked(archiveLength + limits.MinimumFreeBytesAfterExtraction);
            if (available < required)
            {
                throw new IOException(
                    $"The Git snapshot needs at least {required} free bytes before extraction, but only {available} are available.");
            }
        }
        catch (OverflowException exception)
        {
            throw new IOException("Could not validate free space for the Git snapshot.", exception);
        }
    }

    internal static void ValidateTrackedPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var segments = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.None);
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new InvalidDataException("The Git archive contains a non-canonical tracked path.");
        }

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var segment in segments)
        {
            if (segment.EndsWith(' ') ||
                segment.EndsWith('.') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                IsReservedWindowsName(segment))
            {
                throw new InvalidDataException(
                    "The Git archive contains a tracked path that is unsafe on Windows.");
            }
        }
    }

    private static bool IsReservedWindowsName(string segment)
    {
        var stem = segment.Split('.', 2)[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               stem.Length == 4 &&
               (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               stem[3] is >= '1' and <= '9';
    }

    internal static void DeleteOwnedDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)));
        if (!name.StartsWith(TemporaryDirectoryPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to delete a directory not owned by PackageMedic.");
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (UnauthorizedAccessException)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    private async Task<ProcessResult> RunGitAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string operation,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(operationTimeout);
        try
        {
            var result = await processRunner.RunAsync(
                "git",
                arguments,
                workingDirectory,
                timeoutSource.Token).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                var detail = ProcessRunner.RedactSecrets(string.IsNullOrWhiteSpace(result.StandardError)
                    ? result.StandardOutput.Trim()
                    : result.StandardError.Trim());
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(detail)
                        ? $"Could not {operation}; Git exited with code {result.ExitCode}."
                        : $"Could not {operation}: {detail}");
            }

            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Timed out while attempting to {operation}.");
        }
    }

    private static string ParseSingleLine(string output, string valueName)
    {
        var value = output.Trim();
        if (value.Length == 0 || value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Git returned an invalid {valueName}.");
        }

        return value;
    }

    private static string ParseCommit(string output)
    {
        var commit = ParseSingleLine(output, "commit identifier");
        if ((commit.Length is not 40 and not 64) || !commit.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("Git returned an invalid commit identifier.");
        }

        return commit.ToLowerInvariant();
    }

    private static bool IsWithin(string candidate, string parent)
    {
        var normalizedCandidate = Path.GetFullPath(candidate);
        var normalizedParent = Path.GetFullPath(parent);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(normalizedCandidate, normalizedParent, comparison))
        {
            return true;
        }

        var parentPrefix = Path.EndsInDirectorySeparator(normalizedParent)
            ? normalizedParent
            : normalizedParent + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(parentPrefix, comparison);
    }
}
