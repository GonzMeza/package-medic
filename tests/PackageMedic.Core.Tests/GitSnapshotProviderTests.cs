using System.Formats.Tar;
using System.Text;
using PackageMedic.Core;

namespace PackageMedic.Core.Tests;

public sealed class GitSnapshotProviderTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task MaterializesTrackedArchiveWithoutChangingTheCheckoutAndCleansUp()
    {
        var repository = CreateTemporaryDirectory("repository");
        var temporaryRoot = CreateTemporaryDirectory("snapshots");
        try
        {
            var runner = new ArchiveProcessRunner(repository, Commit);
            var provider = new GitSnapshotProvider(runner, TimeSpan.FromSeconds(5), temporaryRoot);

            var snapshot = await provider.MaterializeAsync(
                repository,
                "main~1",
                TestContext.Current.CancellationToken);
            var ownedDirectory = Directory.GetParent(snapshot.SnapshotDirectory)!.FullName;

            Assert.Equal(repository, snapshot.RepositoryRoot);
            Assert.Equal("main~1", snapshot.Reference);
            Assert.Equal(Commit, snapshot.Commit);
            Assert.Equal("tracked", File.ReadAllText(Path.Combine(snapshot.SnapshotDirectory, "src", "App.csproj")));
            Assert.False(File.Exists(Path.Combine(snapshot.SnapshotDirectory, "untracked.txt")));
            Assert.DoesNotContain(
                runner.Invocations,
                invocation => invocation.Arguments.Any(argument =>
                    argument.Equals("checkout", StringComparison.Ordinal) ||
                    argument.Equals("switch", StringComparison.Ordinal) ||
                    argument.Equals("worktree", StringComparison.Ordinal)));
            Assert.Contains(
                runner.Invocations,
                invocation => invocation.Arguments.SequenceEqual(
                    ["rev-parse", "--verify", "--end-of-options", "main~1^{commit}"]));
            Assert.Contains(
                runner.Invocations,
                invocation => invocation.Arguments.Take(3).SequenceEqual(
                    ["-c", "core.attributesFile=", "archive"]));

            snapshot.Dispose();
            snapshot.Dispose();
            Assert.False(Directory.Exists(ownedDirectory));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("--help")]
    [InlineData(" main")]
    [InlineData("main branch")]
    [InlineData("main\nHEAD")]
    public async Task RejectsUnsafeRevisionBeforeStartingGit(string reference)
    {
        var repository = CreateTemporaryDirectory("repository");
        var temporaryRoot = CreateTemporaryDirectory("snapshots");
        try
        {
            var runner = new ArchiveProcessRunner(repository, Commit);
            var provider = new GitSnapshotProvider(runner, TimeSpan.FromSeconds(5), temporaryRoot);

            await Assert.ThrowsAsync<ArgumentException>(
                () => provider.MaterializeAsync(repository, reference, TestContext.Current.CancellationToken));

            Assert.Empty(runner.Invocations);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CleansTemporaryDirectoryWhenArchivingFails()
    {
        var repository = CreateTemporaryDirectory("repository");
        var temporaryRoot = CreateTemporaryDirectory("snapshots");
        try
        {
            var runner = new ArchiveProcessRunner(repository, Commit) { FailArchive = true };
            var provider = new GitSnapshotProvider(runner, TimeSpan.FromSeconds(5), temporaryRoot);

            await Assert.ThrowsAsync<IOException>(
                () => provider.MaterializeAsync(repository, "main", TestContext.Current.CancellationToken));

            Assert.Empty(Directory.EnumerateFileSystemEntries(temporaryRoot));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RepresentsTrackedSymbolicLinksAsInertFiles()
    {
        var repository = CreateTemporaryDirectory("repository");
        var temporaryRoot = CreateTemporaryDirectory("snapshots");
        try
        {
            var runner = new ArchiveProcessRunner(repository, Commit) { IncludeSymbolicLink = true };
            var provider = new GitSnapshotProvider(runner, TimeSpan.FromSeconds(5), temporaryRoot);

            using var snapshot = await provider.MaterializeAsync(
                repository,
                "HEAD",
                TestContext.Current.CancellationToken);
            var linkPath = Path.Combine(snapshot.SnapshotDirectory, "outside-link");

            Assert.False(File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint));
            Assert.Equal("../../outside", File.ReadAllText(linkPath));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CleanupDoesNotFollowAHostileDirectorySymbolicLink()
    {
        var repository = CreateTemporaryDirectory("repository");
        var temporaryRoot = CreateTemporaryDirectory("snapshots");
        var external = CreateTemporaryDirectory("external");
        var sentinel = Path.Combine(external, "must-survive.txt");
        File.WriteAllText(sentinel, "outside");
        try
        {
            var provider = new GitSnapshotProvider(
                new ArchiveProcessRunner(repository, Commit),
                TimeSpan.FromSeconds(5),
                temporaryRoot);
            var snapshot = await provider.MaterializeAsync(
                repository,
                "HEAD",
                TestContext.Current.CancellationToken);
            var ownedDirectory = Directory.GetParent(snapshot.SnapshotDirectory)!.FullName;
            var link = Path.Combine(snapshot.SnapshotDirectory, "hostile-link");

            try
            {
                Directory.CreateSymbolicLink(link, external);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                snapshot.Dispose();
                Assert.True(File.Exists(sentinel));
                return;
            }

            snapshot.Dispose();

            Assert.False(Directory.Exists(ownedDirectory));
            Assert.True(File.Exists(sentinel));
            Assert.Equal("outside", File.ReadAllText(sentinel));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
            Directory.Delete(temporaryRoot, recursive: true);
            Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public async Task CleanupRemovesReadOnlyFilesAndOwnershipMarker()
    {
        var repository = CreateTemporaryDirectory("repository");
        var temporaryRoot = CreateTemporaryDirectory("snapshots");
        try
        {
            var provider = new GitSnapshotProvider(
                new ArchiveProcessRunner(repository, Commit),
                TimeSpan.FromSeconds(5),
                temporaryRoot);
            var snapshot = await provider.MaterializeAsync(
                repository,
                "HEAD",
                TestContext.Current.CancellationToken);
            var ownedDirectory = Directory.GetParent(snapshot.SnapshotDirectory)!.FullName;
            var nestedDirectory = Path.Combine(snapshot.SnapshotDirectory, "readonly");
            var nestedFile = Path.Combine(nestedDirectory, "content.txt");
            var marker = Path.Combine(ownedDirectory, GitSnapshotProvider.OwnershipMarkerFileName);
            Directory.CreateDirectory(nestedDirectory);
            File.WriteAllText(nestedFile, "content");
            File.SetAttributes(nestedFile, File.GetAttributes(nestedFile) | FileAttributes.ReadOnly);
            File.SetAttributes(marker, File.GetAttributes(marker) | FileAttributes.ReadOnly);

            snapshot.Dispose();

            Assert.False(Directory.Exists(ownedDirectory));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public void RefusesPrefixOnlyDirectoryWithoutOwnershipMarker()
    {
        var root = CreateTemporaryDirectory("ownership");
        var candidate = Path.Combine(root, $"PackageMedic.GitSnapshot.{Guid.NewGuid():N}");
        var sentinel = Path.Combine(candidate, "must-survive.txt");
        Directory.CreateDirectory(candidate);
        File.WriteAllText(sentinel, "not-owned");
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                GitSnapshotProvider.DeleteOwnedDirectory(candidate, Guid.NewGuid().ToString("N")));

            Assert.Contains("ownership marker", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(sentinel));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RefusesAnIncorrectOwnershipTokenWithoutDeletingTheSnapshot()
    {
        var repository = CreateTemporaryDirectory("repository");
        var temporaryRoot = CreateTemporaryDirectory("snapshots");
        try
        {
            var provider = new GitSnapshotProvider(
                new ArchiveProcessRunner(repository, Commit),
                TimeSpan.FromSeconds(5),
                temporaryRoot);
            using var snapshot = await provider.MaterializeAsync(
                repository,
                "HEAD",
                TestContext.Current.CancellationToken);
            var ownedDirectory = Directory.GetParent(snapshot.SnapshotDirectory)!.FullName;

            var exception = Assert.Throws<InvalidOperationException>(() =>
                GitSnapshotProvider.DeleteOwnedDirectory(ownedDirectory, Guid.NewGuid().ToString("N")));

            Assert.Contains("invalid ownership marker", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(ownedDirectory));
            Assert.True(File.Exists(Path.Combine(snapshot.SnapshotDirectory, "src", "App.csproj")));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("src/../outside")]
    [InlineData("src/./App.csproj")]
    [InlineData("src//App.csproj")]
    public void RejectsNonCanonicalArchivePaths(string path)
    {
        var nativePath = path.Replace('/', Path.DirectorySeparatorChar);

        Assert.Throws<InvalidDataException>(() => GitSnapshotProvider.ValidateTrackedPath(nativePath));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("aux.txt")]
    [InlineData("file.txt.")]
    [InlineData("file.txt ")]
    [InlineData("file:stream")]
    public void RejectsWindowsDeviceAliasesAndAmbiguousNames(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Throws<InvalidDataException>(() => GitSnapshotProvider.ValidateTrackedPath(path));
    }

    [Fact]
    public async Task RejectsAnArchiveThatExceedsTheConfiguredArchiveLimitAndCleansUp()
    {
        var repository = CreateTemporaryDirectory("repository");
        var temporaryRoot = CreateTemporaryDirectory("snapshots");
        try
        {
            var limits = new GitSnapshotLimits(
                MaximumArchiveBytes: 1,
                MaximumExpandedBytes: 1024,
                MaximumSingleFileBytes: 1024,
                MaximumEntries: 10,
                MinimumFreeBytesAfterExtraction: 0);
            var provider = new GitSnapshotProvider(
                new ArchiveProcessRunner(repository, Commit),
                TimeSpan.FromSeconds(5),
                temporaryRoot,
                limits);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => provider.MaterializeAsync(repository, "HEAD", TestContext.Current.CancellationToken));

            Assert.Contains("archive", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateFileSystemEntries(temporaryRoot));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsSnapshotEntriesThatExceedSingleAndTotalSizeLimits()
    {
        var root = CreateTemporaryDirectory("tar-limits");
        var archive = Path.Combine(root, "snapshot.tar");
        var destination = Path.Combine(root, "output");
        Directory.CreateDirectory(destination);
        try
        {
            WriteArchive(archive, ("one.bin", "1234"), ("two.bin", "5678"));
            var singleFileLimits = new GitSnapshotLimits(4096, 4096, 3, 10, 0);
            await Assert.ThrowsAsync<InvalidDataException>(() => GitSnapshotProvider.ExtractTrackedFilesAsync(
                archive,
                destination,
                TestContext.Current.CancellationToken,
                singleFileLimits));

            Directory.Delete(destination, recursive: true);
            Directory.CreateDirectory(destination);
            var totalLimits = new GitSnapshotLimits(4096, 7, 4, 10, 0);
            await Assert.ThrowsAsync<InvalidDataException>(() => GitSnapshotProvider.ExtractTrackedFilesAsync(
                archive,
                destination,
                TestContext.Current.CancellationToken,
                totalLimits));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsSnapshotsWithTooManyTrackedEntries()
    {
        var root = CreateTemporaryDirectory("tar-entry-limit");
        var archive = Path.Combine(root, "snapshot.tar");
        var destination = Path.Combine(root, "output");
        Directory.CreateDirectory(destination);
        try
        {
            WriteArchive(archive, ("one.txt", "1"), ("two.txt", "2"));
            var limits = new GitSnapshotLimits(4096, 4096, 4096, 1, 0);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                GitSnapshotProvider.ExtractTrackedFilesAsync(
                    archive,
                    destination,
                    TestContext.Current.CancellationToken,
                    limits));

            Assert.Contains("entries", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PreservesTrackedExecutableModeOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTemporaryDirectory("tar-mode");
        var archive = Path.Combine(root, "snapshot.tar");
        var destination = Path.Combine(root, "output");
        Directory.CreateDirectory(destination);
        try
        {
            using (var stream = File.Create(archive))
            using (var writer = new TarWriter(stream, leaveOpen: false))
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "build.sh")
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes("#!/bin/sh\n")),
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                });
            }

            await GitSnapshotProvider.ExtractTrackedFilesAsync(
                archive,
                destination,
                TestContext.Current.CancellationToken);

            Assert.True(File.GetUnixFileMode(Path.Combine(destination, "build.sh")).HasFlag(UnixFileMode.UserExecute));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteArchive(string path, params (string Name, string Content)[] entries)
    {
        using var stream = File.Create(path);
        using var writer = new TarWriter(stream, leaveOpen: false);
        foreach (var (name, content) in entries)
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
            });
        }
    }

    private static string CreateTemporaryDirectory(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"PackageMedic.Tests.{suffix}.{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ArchiveProcessRunner(string repositoryRoot, string commit) : IProcessRunner
    {
        public List<(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory)> Invocations { get; } = [];

        public bool FailArchive { get; init; }

        public bool IncludeSymbolicLink { get; init; }

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            Invocations.Add((fileName, arguments.ToArray(), workingDirectory));
            if (arguments.SequenceEqual(["rev-parse", "--show-toplevel"]))
            {
                return Task.FromResult(new ProcessResult(0, repositoryRoot + Environment.NewLine, string.Empty));
            }

            if (arguments.Count >= 2 && arguments[0] == "rev-parse" && arguments[1] == "--verify")
            {
                return Task.FromResult(new ProcessResult(0, commit + Environment.NewLine, string.Empty));
            }

            if (arguments.Contains("archive", StringComparer.Ordinal))
            {
                var output = arguments.Single(argument => argument.StartsWith("--output=", StringComparison.Ordinal))[9..];
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                if (FailArchive)
                {
                    File.WriteAllText(output, "partial archive");
                    throw new IOException("Simulated archive failure.");
                }

                using var stream = File.Create(output);
                using var writer = new TarWriter(stream, leaveOpen: false);
                var content = new MemoryStream(Encoding.UTF8.GetBytes("tracked"));
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "src/App.csproj")
                {
                    DataStream = content,
                });
                if (IncludeSymbolicLink)
                {
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "outside-link")
                    {
                        LinkName = "../../outside",
                    });
                }

                return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            }

            throw new InvalidOperationException("Unexpected Git invocation.");
        }
    }
}
