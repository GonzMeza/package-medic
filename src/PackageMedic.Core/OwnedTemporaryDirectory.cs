using System.Text;

namespace PackageMedic.Core;

/// <summary>
/// Owns one private temporary directory and removes it without following symbolic links or
/// junctions. The ownership marker prevents cleanup from targeting an unrelated path.
/// </summary>
public sealed class OwnedTemporaryDirectory : IDisposable
{
    private const string DirectoryPrefix = "PackageMedic.AnalysisRuntime.";
    private const string MarkerFileName = ".packagemedic-runtime-owner";
    private const string MarkerPrefix = "PackageMedic.AnalysisRuntime:v1:";
    private readonly string ownershipToken;
    private int disposed;

    private OwnedTemporaryDirectory(string directoryPath, string ownershipToken)
    {
        DirectoryPath = directoryPath;
        this.ownershipToken = ownershipToken;
    }

    public string DirectoryPath { get; }

    public static OwnedTemporaryDirectory Create(string repositoryRoot, string? temporaryRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var repository = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(temporaryRoot ?? Path.GetTempPath()));
        Directory.CreateDirectory(root);
        string physicalRepository;
        string physicalRoot;
        try
        {
            physicalRepository = GitSnapshotProvider.ResolvePhysicalDirectoryPath(repository, requireExisting: true);
            physicalRoot = GitSnapshotProvider.ResolvePhysicalDirectoryPath(root, requireExisting: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "The PackageMedic analysis runtime filesystem boundary could not be validated.",
                exception);
        }

        if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint) ||
            GitSnapshotProvider.IsWithin(root, repository) ||
            GitSnapshotProvider.IsWithin(physicalRoot, physicalRepository))
        {
            throw new InvalidOperationException(
                "The PackageMedic analysis runtime directory must be a regular directory outside the analyzed repository.");
        }

        var directory = Path.Combine(root, DirectoryPrefix + Guid.NewGuid().ToString("N"));
        var token = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(directory);
        try
        {
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException("The PackageMedic analysis runtime cannot be a symbolic link or junction.");
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var markerPath = Path.Combine(directory, MarkerFileName);
            using var marker = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(marker, new UTF8Encoding(false));
            writer.Write(MarkerPrefix);
            writer.Write(token);
            return new OwnedTemporaryDirectory(directory, token);
        }
        catch
        {
            try
            {
                Directory.Delete(directory, recursive: false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Preserve the creation failure. Leaving an unowned directory is safer than recursing.
            }

            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        DeleteOwnedDirectory(DirectoryPath, ownershipToken);
    }

    internal static void DeleteOwnedDirectory(string directory, string ownershipToken)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var suffix = Path.GetFileName(fullPath).StartsWith(DirectoryPrefix, StringComparison.Ordinal)
            ? Path.GetFileName(fullPath)[DirectoryPrefix.Length..]
            : string.Empty;
        if (!Guid.TryParseExact(suffix, "N", out _))
        {
            throw new InvalidOperationException("Refusing to delete a temporary directory not owned by PackageMedic.");
        }

        if (!Directory.Exists(fullPath))
        {
            return;
        }

        var rootAttributes = File.GetAttributes(fullPath);
        if (rootAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("Refusing to delete a PackageMedic runtime that became a symbolic link or junction.");
        }

        var markerPath = Path.Combine(fullPath, MarkerFileName);
        var expectedMarker = MarkerPrefix + ownershipToken;
        var markerInfo = new FileInfo(markerPath);
        if (!markerInfo.Exists || markerInfo.Attributes.HasFlag(FileAttributes.Directory) ||
            markerInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            markerInfo.Length != Encoding.UTF8.GetByteCount(expectedMarker) ||
            !string.Equals(File.ReadAllText(markerPath, Encoding.UTF8), expectedMarker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to delete a PackageMedic runtime without its valid ownership marker.");
        }

        DeleteWithoutFollowingLinks(fullPath);
    }

    private static void DeleteWithoutFollowingLinks(string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var options = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };
        var pending = new Stack<(string Path, bool DeleteAfterChildren)>();
        pending.Push((root, false));
        while (pending.TryPop(out var current))
        {
            var attributes = File.GetAttributes(current.Path);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (string.Equals(Path.GetFullPath(current.Path), Path.GetFullPath(root), comparison))
                {
                    throw new InvalidOperationException(
                        "Refusing to delete a PackageMedic runtime root that became a symbolic link or junction.");
                }

                DeleteEntry(current.Path, attributes);
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
                    DeleteEntry(entry, entryAttributes);
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

    private static void DeleteEntry(string path, FileAttributes attributes)
    {
        if (attributes.HasFlag(FileAttributes.Directory))
        {
            Directory.Delete(path, recursive: false);
        }
        else
        {
            File.Delete(path);
        }
    }

    private static void ClearReadOnly(string path, FileAttributes attributes)
    {
        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }
}
