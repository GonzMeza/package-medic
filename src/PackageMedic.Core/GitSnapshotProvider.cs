using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;

namespace PackageMedic.Core;

/// <summary>
/// A detached, tracked-files-only view of a Git commit. Disposing the snapshot removes
/// every temporary file created while materializing it.
/// </summary>
public sealed class GitSnapshot : IDisposable
{
    private readonly string ownedDirectory;
    private readonly string ownershipToken;
    private readonly Dictionary<string, GitTrackedFileFingerprint> trackedFiles;
    private int disposed;

    internal GitSnapshot(
        string repositoryRoot,
        string reference,
        string commit,
        string snapshotDirectory,
        string ownedDirectory,
        string ownershipToken,
        Dictionary<string, GitTrackedFileFingerprint> trackedFiles)
    {
        RepositoryRoot = repositoryRoot;
        Reference = reference;
        Commit = commit;
        SnapshotDirectory = snapshotDirectory;
        this.ownedDirectory = ownedDirectory;
        this.ownershipToken = ownershipToken;
        this.trackedFiles = trackedFiles;
    }

    public string RepositoryRoot { get; }

    public string Reference { get; }

    public string Commit { get; }

    public string SnapshotDirectory { get; }

    public void EnsureTrackedFilesUnchanged() =>
        GitSnapshotProvider.EnsureTrackedFilesUnchanged(SnapshotDirectory, trackedFiles);

    public void RecordExpectedTrackedFileMutation(string relativePath) =>
        GitSnapshotProvider.RecordExpectedTrackedFileMutation(SnapshotDirectory, trackedFiles, relativePath);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        GitSnapshotProvider.DeleteOwnedDirectory(ownedDirectory, ownershipToken);
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
    internal const string OwnershipMarkerFileName = ".packagemedic-snapshot-owner";
    private const string OwnershipMarkerPrefix = "PackageMedic.GitSnapshot:v1:";
    private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromMinutes(2);

    private readonly IProcessRunner processRunner;
    private readonly GitCommitInspector commitInspector;
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
        commitInspector = new GitCommitInspector(processRunner, operationTimeout);
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

        var commit = await commitInspector.ResolveCommitAsync(repositoryRoot, reference, cancellationToken)
            .ConfigureAwait(false);
        var preflight = await PreflightTreeAsync(repositoryRoot, commit, cancellationToken)
            .ConfigureAwait(false);

        EnsureTemporaryRootIsSafe(repositoryRoot);
        Directory.CreateDirectory(temporaryRoot);
        EnsureTemporaryRootIsSafe(repositoryRoot);
        EnsureMaterializationCapacity(temporaryRoot, preflight);
        var ownedDirectory = Path.Combine(
            temporaryRoot,
            TemporaryDirectoryPrefix + Guid.NewGuid().ToString("N"));
        var ownershipToken = Guid.NewGuid().ToString("N");
        var archivePath = Path.Combine(ownedDirectory, "snapshot.tar");
        var snapshotDirectory = Path.Combine(ownedDirectory, "worktree");

        try
        {
            CreateOwnedDirectory(ownedDirectory, ownershipToken);
            Directory.CreateDirectory(snapshotDirectory);
            await RunGitAsync(
                ["-c", "core.attributesFile=", "archive", "--format=tar", $"--output={archivePath}", commit],
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
            var trackedFiles = CreateTrackedFileManifest(snapshotDirectory);
            return new GitSnapshot(
                repositoryRoot,
                reference,
                commit,
                snapshotDirectory,
                ownedDirectory,
                ownershipToken,
                trackedFiles);
        }
        catch (Exception operationError)
        {
            try
            {
                DeleteOwnedDirectory(ownedDirectory, ownershipToken);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(
                    "Git snapshot materialization failed and its owned temporary directory could not be cleaned up.",
                    operationError,
                    cleanupError);
            }

            throw;
        }
    }

    private async Task<GitTreePreflight> PreflightTreeAsync(
        string repositoryRoot,
        string commit,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            [
                "ls-tree",
                "-r",
                "--full-tree",
                "--format=%(objectmode) %(objecttype) %(objectsize)",
                commit,
            ],
            repositoryRoot,
            $"inspect Git commit '{commit}' before archiving",
            cancellationToken).ConfigureAwait(false);
        long expandedBytes = 0;
        long estimatedArchiveBytes = 1024;
        var entries = 0;
        try
        {
            foreach (var entry in result.StandardOutput.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                entries = checked(entries + 1);
                if (entries > limits.MaximumEntries)
                {
                    throw new InvalidDataException(
                        $"The Git snapshot contains more than the configured {limits.MaximumEntries} entries.");
                }

                var metadata = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (metadata.Length != 3)
                {
                    throw new InvalidDataException("Git returned malformed tracked-tree metadata.");
                }

                var size = metadata[2] == "-"
                    ? 0L
                    : long.TryParse(
                        metadata[2],
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var parsed) && parsed >= 0
                        ? parsed
                        : throw new InvalidDataException("Git returned an invalid tracked blob size.");
                if (size > limits.MaximumSingleFileBytes)
                {
                    throw new InvalidDataException(
                        $"A tracked Git blob is {size} bytes, exceeding the configured {limits.MaximumSingleFileBytes}-byte single-file limit.");
                }

                expandedBytes = checked(expandedBytes + size);
                if (expandedBytes > limits.MaximumExpandedBytes)
                {
                    throw new InvalidDataException(
                        $"The Git snapshot expands beyond the configured {limits.MaximumExpandedBytes}-byte limit.");
                }

                var roundedSize = checked((size + 511) / 512 * 512);
                estimatedArchiveBytes = checked(estimatedArchiveBytes + roundedSize + 8192);
                if (estimatedArchiveBytes > limits.MaximumArchiveBytes)
                {
                    throw new InvalidDataException(
                        $"The estimated Git snapshot archive exceeds the configured {limits.MaximumArchiveBytes}-byte limit.");
                }
            }
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("The Git snapshot size estimate overflowed its safety bounds.", exception);
        }

        return new GitTreePreflight(expandedBytes, estimatedArchiveBytes);
    }

    private void EnsureMaterializationCapacity(string destinationRoot, GitTreePreflight preflight)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(destinationRoot));
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            var required = checked(
                preflight.EstimatedArchiveBytes +
                preflight.ExpandedBytes +
                limits.MinimumFreeBytesAfterExtraction);
            var available = new DriveInfo(root).AvailableFreeSpace;
            if (available < required)
            {
                throw new IOException(
                    $"The Git snapshot needs at least {required} free bytes before materialization, but only {available} are available.");
            }
        }
        catch (OverflowException exception)
        {
            throw new IOException("Could not validate free space for the Git snapshot.", exception);
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

    internal static Dictionary<string, GitTrackedFileFingerprint> CreateTrackedFileManifest(
        string snapshotDirectory)
    {
        var root = Path.GetFullPath(snapshotDirectory);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var manifest = new Dictionary<string, GitTrackedFileFingerprint>(comparison);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException(
                    "The materialized Git snapshot unexpectedly contains a filesystem link.");
            }

            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = SHA256.HashData(stream);
            UnixFileMode? mode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(path);
            manifest.Add(relative, new GitTrackedFileFingerprint(info.Length, hash, mode));
        }

        return manifest;
    }

    internal static void EnsureTrackedFilesUnchanged(
        string snapshotDirectory,
        IReadOnlyDictionary<string, GitTrackedFileFingerprint> trackedFiles)
    {
        var root = Path.GetFullPath(snapshotDirectory);
        foreach (var (relative, expected) in trackedFiles)
        {
            var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithin(path, root) || !File.Exists(path))
            {
                throw new InvalidOperationException(
                    $"Tracked snapshot file '{relative}' was removed or changed type during verified execution.");
            }

            var info = new FileInfo(path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length != expected.Length)
            {
                throw new InvalidOperationException(
                    $"Tracked snapshot file '{relative}' changed during verified execution.");
            }

            if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(path) != expected.UnixMode)
            {
                throw new InvalidOperationException(
                    $"Tracked snapshot file '{relative}' changed mode during verified execution.");
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var observed = SHA256.HashData(stream);
            if (!CryptographicOperations.FixedTimeEquals(observed, expected.Sha256))
            {
                throw new InvalidOperationException(
                    $"Tracked snapshot file '{relative}' changed content during verified execution.");
            }
        }
    }

    internal static void RecordExpectedTrackedFileMutation(
        string snapshotDirectory,
        IDictionary<string, GitTrackedFileFingerprint> trackedFiles,
        string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var portable = relativePath.Replace('\\', '/');
        if (!trackedFiles.ContainsKey(portable))
        {
            throw new InvalidOperationException(
                "Dependency Time Machine may mutate only a file tracked by the immutable source commit.");
        }

        var root = Path.GetFullPath(snapshotDirectory);
        var path = Path.GetFullPath(Path.Combine(root, portable.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(path, root) || !File.Exists(path) ||
            File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException(
                "The authorized dependency declaration mutation left its trusted snapshot boundary.");
        }

        var info = new FileInfo(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        trackedFiles[portable] = new GitTrackedFileFingerprint(
            info.Length,
            SHA256.HashData(stream),
            OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(path));
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

                    if (!OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(destination, entry.Mode);
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

    private static void CreateOwnedDirectory(string directory, string ownershipToken)
    {
        Directory.CreateDirectory(directory);
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("The owned Git snapshot directory cannot be a symbolic link or junction.");
        }
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var markerPath = Path.Combine(directory, OwnershipMarkerFileName);
        var markerCreated = false;
        try
        {
            using var marker = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            markerCreated = true;
            using var writer = new StreamWriter(marker, new UTF8Encoding(false));
            writer.Write(OwnershipMarkerPrefix);
            writer.Write(ownershipToken);
        }
        catch
        {
            if (markerCreated)
            {
                try
                {
                    File.Delete(markerPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Preserve the marker-creation exception.
                }
            }

            try
            {
                // Never recurse before ownership has been established. If another entry appeared,
                // leave the directory behind instead of risking deletion outside our boundary.
                Directory.Delete(directory, recursive: false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Preserve the marker-creation exception; a leftover temporary directory is safer.
            }

            throw;
        }
    }

    internal static void DeleteOwnedDirectory(string directory, string ownershipToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownershipToken);

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var name = Path.GetFileName(fullPath);
        var suffix = name.StartsWith(TemporaryDirectoryPrefix, StringComparison.Ordinal)
            ? name[TemporaryDirectoryPrefix.Length..]
            : string.Empty;
        if (!Guid.TryParseExact(suffix, "N", out _))
        {
            throw new InvalidOperationException("Refusing to delete a directory not owned by PackageMedic.");
        }

        if (!Directory.Exists(fullPath))
        {
            return;
        }

        var rootAttributes = File.GetAttributes(fullPath);
        if (rootAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("Refusing to delete a PackageMedic snapshot root that is a symbolic link or junction.");
        }

        var markerPath = Path.Combine(fullPath, OwnershipMarkerFileName);
        if (!File.Exists(markerPath))
        {
            throw new InvalidOperationException("Refusing to delete a PackageMedic snapshot without its ownership marker.");
        }

        var markerAttributes = File.GetAttributes(markerPath);
        if (markerAttributes.HasFlag(FileAttributes.Directory) ||
            markerAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("Refusing to trust an invalid PackageMedic snapshot ownership marker.");
        }

        var expectedMarker = OwnershipMarkerPrefix + ownershipToken;
        var markerInfo = new FileInfo(markerPath);
        if (markerInfo.Length != Encoding.UTF8.GetByteCount(expectedMarker) ||
            !string.Equals(File.ReadAllText(markerPath, Encoding.UTF8), expectedMarker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to delete a PackageMedic snapshot with an invalid ownership marker.");
        }

        DeleteDirectoryWithoutFollowingLinks(fullPath);
    }

    private static void DeleteDirectoryWithoutFollowingLinks(string directory)
    {
        var options = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        var pending = new Stack<(string Path, bool DeleteAfterChildren)>();
        pending.Push((directory, false));
        while (pending.TryPop(out var current))
        {
            var attributes = File.GetAttributes(current.Path);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (string.Equals(
                        Path.GetFullPath(current.Path),
                        Path.GetFullPath(directory),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Refusing to delete a PackageMedic snapshot root that became a symbolic link or junction.");
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    Directory.Delete(current.Path, recursive: false);
                }
                else
                {
                    File.Delete(current.Path);
                }

                continue;
            }

            if (!attributes.HasFlag(FileAttributes.Directory))
            {
                ClearReadOnly(current.Path, attributes);
                File.Delete(current.Path);
                continue;
            }

            if (current.DeleteAfterChildren)
            {
                ClearReadOnly(current.Path, attributes);
                Directory.Delete(current.Path, recursive: false);
                continue;
            }

            pending.Push((current.Path, true));
            foreach (var entry in Directory.EnumerateFileSystemEntries(current.Path, "*", options))
            {
                var entryAttributes = File.GetAttributes(entry);
                if (entryAttributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    if (entryAttributes.HasFlag(FileAttributes.Directory))
                    {
                        Directory.Delete(entry, recursive: false);
                    }
                    else
                    {
                        File.Delete(entry);
                    }
                }
                else if (entryAttributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push((entry, false));
                }
                else
                {
                    ClearReadOnly(entry, entryAttributes);
                    File.Delete(entry);
                }
            }
        }
    }

    private static void ClearReadOnly(string path, FileAttributes attributes)
    {
        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
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
            if (result.StandardOutputTruncated || result.StandardErrorTruncated)
            {
                throw new InvalidOperationException(
                    $"Could not {operation}; Git output exceeded the subprocess safety limit.");
            }

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

    private void EnsureTemporaryRootIsSafe(string repositoryRoot)
    {
        if (File.Exists(temporaryRoot))
        {
            throw new InvalidOperationException(
                "The Git snapshot temporary root must be a directory.");
        }

        if (Directory.Exists(temporaryRoot))
        {
            var rootInfo = new DirectoryInfo(temporaryRoot);
            if (rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) || rootInfo.LinkTarget is not null)
            {
                throw new InvalidOperationException(
                    "The Git snapshot temporary root cannot be a symbolic link, junction, or reparse point.");
            }
        }

        string physicalTemporaryRoot;
        string physicalRepositoryRoot;
        try
        {
            physicalTemporaryRoot = ResolvePhysicalDirectoryPath(temporaryRoot, requireExisting: false);
            physicalRepositoryRoot = ResolvePhysicalDirectoryPath(repositoryRoot, requireExisting: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "Could not validate the physical Git snapshot temporary root.",
                exception);
        }

        if (IsWithin(temporaryRoot, repositoryRoot) ||
            IsWithin(physicalTemporaryRoot, physicalRepositoryRoot))
        {
            throw new InvalidOperationException(
                "The Git snapshot temporary directory must be physically outside the current repository.");
        }
    }

    internal static string ResolvePhysicalDirectoryPath(string path, bool requireExisting)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("The directory path does not have a filesystem root.");
        }

        var segments = Path.GetRelativePath(root, fullPath)
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != ".")
            .ToArray();
        var current = root;
        for (var index = 0; index < segments.Length; index++)
        {
            var candidate = Path.Combine(current, segments[index]);
            var entry = new DirectoryInfo(candidate);
            if (!entry.Exists)
            {
                if (requireExisting)
                {
                    throw new InvalidOperationException("The directory path no longer exists.");
                }

                for (; index < segments.Length; index++)
                {
                    current = Path.Combine(current, segments[index]);
                }

                return Path.GetFullPath(current);
            }

            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry.LinkTarget is not null)
            {
                var resolved = entry.ResolveLinkTarget(returnFinalTarget: true)
                    ?? throw new InvalidOperationException(
                        "The directory path contains an invalid symbolic-link or junction target.");
                if (!resolved.Attributes.HasFlag(FileAttributes.Directory))
                {
                    throw new InvalidOperationException(
                        "The directory path contains a symbolic link that does not resolve to a directory.");
                }

                current = Path.GetFullPath(resolved.FullName);
                continue;
            }

            current = candidate;
        }

        return Path.GetFullPath(current);
    }

    internal static bool IsWithin(string candidate, string parent)
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

    private sealed record GitTreePreflight(long ExpandedBytes, long EstimatedArchiveBytes);
}

internal sealed record GitTrackedFileFingerprint(
    long Length,
    byte[] Sha256,
    UnixFileMode? UnixMode);
