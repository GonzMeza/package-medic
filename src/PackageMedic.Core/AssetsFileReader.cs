using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace PackageMedic.Core;

public sealed class AssetsFileReader
{
    internal const long MaximumAssetsFileBytes = 512L * 1024 * 1024;
    internal const int InitialJsonBufferBytes = 64 * 1024;
    internal const int MaximumJsonTokenBytes = 16 * 1024 * 1024;
    internal const long MaximumNuGetConfigBytes = 1024L * 1024;
    internal const int MaximumPackageFolders = 128;
    internal const int MaximumPackageSources = 512;
    internal const int MaximumFrameworks = 256;
    internal const int MaximumDirectPackagesPerFramework = 100_000;
    internal const int MaximumPackageLibraries = 250_000;
    internal const int MaximumTargetPackages = 500_000;
    internal const int MaximumDependencyEdges = 2_000_000;
    internal const int MaximumRestoreDiagnostics = 100_000;

    public AssetsReadResult Read(
        string assetsFile,
        string projectPath,
        string? trustedRoot = null,
        string? trustedPackagesDirectory = null)
    {
        var project = Path.GetFullPath(projectPath);
        var root = Path.GetFullPath(trustedRoot ?? Path.GetDirectoryName(project)!);
        var normalizedAssetsFile = Path.GetFullPath(assetsFile);
        if (!File.Exists(normalizedAssetsFile))
        {
            throw new FileNotFoundException(
                $"NuGet assets file was not found for '{projectPath}'. Run without --no-restore or run dotnet restore first.",
                normalizedAssetsFile);
        }

        if (trustedRoot is not null &&
            !ProjectDiscovery.IsSafelyContained(root, normalizedAssetsFile))
        {
            throw new InvalidDataException(
                $"NuGet assets file '{normalizedAssetsFile}' for '{projectPath}' is outside the trusted analysis root '{root}'.");
        }

        using var stream = new FileStream(
            normalizedAssetsFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: InitialJsonBufferBytes,
            FileOptions.SequentialScan);
        if (stream.Length > MaximumAssetsFileBytes)
        {
            throw new InvalidDataException(
                $"NuGet assets file for '{projectPath}' exceeds the {MaximumAssetsFileBytes}-byte safety limit.");
        }

        var parser = new AssetsJsonParser(
            project,
            root,
            trustedPackagesDirectory is null ? null : Path.GetFullPath(trustedPackagesDirectory));
        ReadJsonTokens(stream, parser);
        return parser.CreateResult();
    }

    private static void ReadJsonTokens(Stream stream, AssetsJsonParser parser)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(InitialJsonBufferBytes);
        var bufferedBytes = 0;
        var firstBlock = true;
        var readerState = new JsonReaderState();
        try
        {
            while (true)
            {
                if (bufferedBytes == buffer.Length)
                {
                    buffer = GrowBuffer(buffer, bufferedBytes, stream.Length);
                }

                var bytesRead = stream.Read(buffer, bufferedBytes, buffer.Length - bufferedBytes);
                var finalBlock = bytesRead == 0;
                bufferedBytes += bytesRead;

                if (firstBlock)
                {
                    if (bufferedBytes < 3 && !finalBlock)
                    {
                        continue;
                    }

                    firstBlock = false;
                    if (bufferedBytes >= 3 &&
                        buffer[0] == 0xEF &&
                        buffer[1] == 0xBB &&
                        buffer[2] == 0xBF)
                    {
                        buffer.AsSpan(3, bufferedBytes - 3).CopyTo(buffer);
                        bufferedBytes -= 3;
                    }
                }

                var reader = new Utf8JsonReader(buffer.AsSpan(0, bufferedBytes), finalBlock, readerState);
                while (reader.Read())
                {
                    parser.Process(ref reader);
                }

                var consumedBytes = checked((int)reader.BytesConsumed);
                readerState = reader.CurrentState;
                bufferedBytes -= consumedBytes;
                if (bufferedBytes > 0)
                {
                    buffer.AsSpan(consumedBytes, bufferedBytes).CopyTo(buffer);
                }

                if (finalBlock)
                {
                    parser.Complete();
                    return;
                }

                if (bufferedBytes == buffer.Length)
                {
                    buffer = GrowBuffer(buffer, bufferedBytes, stream.Length);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static byte[] GrowBuffer(byte[] buffer, int bufferedBytes, long fileLength)
    {
        var maximumLength = checked((int)Math.Min(
            Math.Min(fileLength, MaximumAssetsFileBytes),
            MaximumJsonTokenBytes));
        if (buffer.Length >= maximumLength)
        {
            throw new JsonException("A JSON token in project.assets.json could not be read within the file safety limit.");
        }

        var requestedLength = Math.Min(maximumLength, checked(buffer.Length * 2));
        var replacement = ArrayPool<byte>.Shared.Rent(requestedLength);
        buffer.AsSpan(0, bufferedBytes).CopyTo(replacement);
        ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        return replacement;
    }

    private sealed class AssetsJsonParser(
        string projectPath,
        string trustedRoot,
        string? trustedPackagesDirectory)
    {
        private readonly Dictionary<string, HashSet<string>> _directByFramework =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Diagnostic> _diagnostics = [];
        private readonly Dictionary<string, LibraryMetadata> _packageLibraries = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _packageFolders = [];
        private readonly HashSet<string> _packageSources = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<RawDependencyEdge> _dependencyEdges = [];
        private readonly List<TargetPackage> _targetPackages = [];

        private string? _activeDependenciesFramework;
        private string? _currentFramework;
        private string? _currentLibrary;
        private string? _currentRootProperty;
        private string? _currentTarget;
        private TargetPackage? _currentTargetPackage;
        private LogEntry? _log;
        private string? _pendingLogProperty;
        private string? _pendingLibraryProperty;
        private bool _currentLibraryIsPackage;
        private bool _currentLibraryHasSignature;
        private string? _currentLibraryPath;
        private string? _currentLibrarySha512;
        private bool _insideFrameworks;
        private bool _insideLibraryFiles;
        private bool _insideLibraries;
        private bool _insideLogs;
        private bool _insidePackageFolders;
        private bool _insideProject;
        private bool _insideRestore;
        private bool _insideRestoreSources;
        private bool _insideTargetDependencies;
        private bool _insideTargets;
        private bool _pendingDependencies;
        private bool _pendingFrameworks;
        private bool _pendingLibraryFiles;
        private bool _pendingLibraryType;
        private bool _pendingRestore;
        private bool _pendingRestoreSources;
        private bool _rootCompleted;
        private bool _rootStarted;

        public void Process(ref Utf8JsonReader reader)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    ProcessStartObject(reader.CurrentDepth);
                    break;
                case JsonTokenType.EndObject:
                    ProcessEndObject(reader.CurrentDepth);
                    break;
                case JsonTokenType.StartArray:
                    ProcessStartArray(reader.CurrentDepth);
                    break;
                case JsonTokenType.EndArray:
                    ProcessEndArray(reader.CurrentDepth);
                    break;
                case JsonTokenType.PropertyName:
                    ProcessProperty(reader.CurrentDepth, reader.GetString() ?? string.Empty);
                    break;
                default:
                    ProcessValue(ref reader);
                    break;
            }
        }

        public void Complete()
        {
            if (!_rootStarted || !_rootCompleted)
            {
                throw new JsonException("project.assets.json does not contain a complete JSON object.");
            }
        }

        public AssetsReadResult CreateResult()
        {
            FlushLibrary();
            var metadataCache = new Dictionary<string, PackageCacheMetadata>(StringComparer.OrdinalIgnoreCase);
            var trustedPackageFolders = _packageFolders
                .Where(folder => IsTrustedPackageFolder(folder, projectPath, trustedPackagesDirectory))
                .Distinct(PathComparer())
                .ToArray();
            var inventory = _targetPackages
                .Where(item => _packageLibraries.ContainsKey(item.Identity))
                .Select(item =>
                {
                    var direct = ResolveDirectPackages(item.Framework, _directByFramework);
                    var library = _packageLibraries[item.Identity];
                    var cacheMetadata = ResolveCacheMetadata(library, metadataCache, trustedPackageFolders);
                    return new PackageInventoryItem(
                        projectPath,
                        item.Framework,
                        item.Id,
                        item.Version,
                        direct.Contains(item.Id) ? PackageDependencyKind.Direct : PackageDependencyKind.Transitive,
                        null,
                        "resolved",
                        item.RuntimeIdentifier,
                        PackageSource: cacheMetadata.Source,
                        ContentHash: library.Sha512 ?? cacheMetadata.ContentHash,
                        SignaturePresent: library.SignaturePresent ? true : cacheMetadata.SignaturePresent);
                })
                .DistinctBy(
                    item => $"{item.Project}|{item.Framework}|{item.RuntimeIdentifier}|{item.Id}|{item.ResolvedVersion}|{item.DependencyKind}",
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item.Project, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Framework, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.RuntimeIdentifier, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.DependencyKind)
                .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.ResolvedVersion, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var resolved = inventory.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var directPackages = _directByFramework.Values
                .SelectMany(item => item)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var transitive = resolved
                .Where(id => !directPackages.Contains(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var edges = ResolveDependencyEdges(inventory);
            return new AssetsReadResult(
                resolved,
                transitive,
                RestoreRunner.Deduplicate(_diagnostics),
                inventory,
                edges,
                inventory.Length == 0 ? 0 : _packageSources.Count,
                HasEffectivePackageSourceMapping(projectPath, trustedRoot, inventory, _packageSources));
        }

        private void ProcessStartObject(int depth)
        {
            if (depth == 0)
            {
                _rootStarted = true;
                return;
            }

            if (depth == 1)
            {
                switch (_currentRootProperty)
                {
                    case "targets":
                        _insideTargets = true;
                        break;
                    case "libraries":
                        _insideLibraries = true;
                        break;
                    case "project":
                        _insideProject = true;
                        break;
                    case "packageFolders":
                        _insidePackageFolders = true;
                        break;
                }
            }

            if (_insideTargets && depth == 4 && _pendingDependencies && _currentTargetPackage is not null)
            {
                _insideTargetDependencies = true;
                _pendingDependencies = false;
            }

            if (_insideLibraries && depth == 3 && _pendingLibraryFiles)
            {
                _insideLibraryFiles = true;
                _pendingLibraryFiles = false;
            }

            if (_insideProject && depth == 2 && _pendingRestore)
            {
                _insideRestore = true;
                _pendingRestore = false;
            }

            if (_insideRestore && depth == 3 && _pendingRestoreSources)
            {
                _insideRestoreSources = true;
                _pendingRestoreSources = false;
            }

            if (_insideProject && depth == 2 && _pendingFrameworks)
            {
                _insideFrameworks = true;
                _pendingFrameworks = false;
            }

            if (_insideFrameworks && depth == 4 && _pendingDependencies && _currentFramework is not null)
            {
                _activeDependenciesFramework = _currentFramework;
                _pendingDependencies = false;
            }

            if (_insideLogs && depth == 2)
            {
                _log = new LogEntry();
            }

            ClearPendingScalarProperties(depth);
        }

        private void ProcessEndObject(int depth)
        {
            if (_insideLibraries && depth == 2)
            {
                FlushLibrary();
            }

            if (_insideLogs && depth == 2)
            {
                FlushLog();
            }

            if (_activeDependenciesFramework is not null && depth == 4)
            {
                _activeDependenciesFramework = null;
            }

            if (_insideTargetDependencies && depth == 4)
            {
                _insideTargetDependencies = false;
            }

            if (_insideRestoreSources && depth == 3)
            {
                _insideRestoreSources = false;
            }

            if (_insideRestore && depth == 2)
            {
                _insideRestore = false;
            }

            if (_insideFrameworks && depth == 2)
            {
                _insideFrameworks = false;
                _currentFramework = null;
            }

            if (depth == 1)
            {
                if (_insideLibraries)
                {
                    FlushLibrary();
                }

                _insideTargets = false;
                _insideLibraries = false;
                _insideProject = false;
                _insidePackageFolders = false;
            }

            if (depth == 0)
            {
                _rootCompleted = true;
            }
        }

        private void ProcessStartArray(int depth)
        {
            if (depth == 1 && _currentRootProperty == "logs")
            {
                _insideLogs = true;
            }

            if (_insideLibraries && depth == 3 && _pendingLibraryFiles)
            {
                _insideLibraryFiles = true;
                _pendingLibraryFiles = false;
            }

            ClearPendingScalarProperties(depth);
        }

        private void ProcessEndArray(int depth)
        {
            if (_activeDependenciesFramework is not null && depth == 4)
            {
                _activeDependenciesFramework = null;
            }

            if (_insideLogs && depth == 1)
            {
                FlushLog();
                _insideLogs = false;
            }

            if (_insideLibraryFiles && depth == 3)
            {
                _insideLibraryFiles = false;
            }
        }

        private void ProcessProperty(int depth, string name)
        {
            if (depth == 1)
            {
                FlushLibrary();
                _currentRootProperty = name;
                return;
            }

            if (_insideTargets)
            {
                if (depth == 2)
                {
                    _currentTarget = name;
                }
                else if (depth == 3 && _currentTarget is not null)
                {
                    var targetParts = _currentTarget.Split('/', 2);
                    var (id, version) = SplitLibraryIdentity(name);
                    _currentTargetPackage = new TargetPackage(
                        targetParts[0],
                        targetParts.Length == 2 ? targetParts[1] : null,
                        id,
                        version);
                    _targetPackages.Add(_currentTargetPackage);
                    EnsureCollectionLimit(
                        _targetPackages.Count,
                        MaximumTargetPackages,
                        "target package entries");
                }
                else if (depth == 4 && name == "dependencies" && _currentTargetPackage is not null)
                {
                    _pendingDependencies = true;
                }
                else if (_insideTargetDependencies && depth == 5 && _currentTargetPackage is not null)
                {
                    _dependencyEdges.Add(new RawDependencyEdge(_currentTargetPackage, name));
                    EnsureCollectionLimit(
                        _dependencyEdges.Count,
                        MaximumDependencyEdges,
                        "dependency edges");
                }

                return;
            }

            if (_insideLibraries)
            {
                if (depth == 2)
                {
                    FlushLibrary();
                    _currentLibrary = name;
                }
                else if (depth == 3 && name == "type")
                {
                    _pendingLibraryType = true;
                }
                else if (depth == 3 && name is "path" or "sha512")
                {
                    _pendingLibraryProperty = name;
                }
                else if (depth == 3 && name == "files")
                {
                    _pendingLibraryFiles = true;
                }

                return;
            }

            if (_insidePackageFolders && depth == 2)
            {
                _packageFolders.Add(name);
                EnsureCollectionLimit(_packageFolders.Count, MaximumPackageFolders, "package folders");
                return;
            }

            if (_insideProject)
            {
                if (depth == 2 && name == "frameworks")
                {
                    _pendingFrameworks = true;
                    return;
                }

                if (depth == 2 && name == "restore")
                {
                    _pendingRestore = true;
                    return;
                }

                if (_insideRestore && depth == 3 && name == "sources")
                {
                    _pendingRestoreSources = true;
                    return;
                }

                if (_insideRestoreSources && depth == 4)
                {
                    var normalized = NormalizeSource(name);
                    _packageSources.Add(normalized ?? CreateOpaqueUntrustedSourceIdentity(name));
                    EnsureCollectionLimit(_packageSources.Count, MaximumPackageSources, "package sources");

                    return;
                }

                if (_insideFrameworks && depth == 3)
                {
                    _currentFramework = name;
                    if (!_directByFramework.ContainsKey(name))
                    {
                        EnsureCollectionLimit(
                            _directByFramework.Count + 1,
                            MaximumFrameworks,
                            "project frameworks");
                    }

                    _directByFramework[name] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    return;
                }

                if (_insideFrameworks && depth == 4 && name == "dependencies" && _currentFramework is not null)
                {
                    _directByFramework[_currentFramework] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _pendingDependencies = true;
                    return;
                }

                if (_activeDependenciesFramework is not null && depth == 5)
                {
                    _directByFramework[_activeDependenciesFramework].Add(name);
                    EnsureCollectionLimit(
                        _directByFramework[_activeDependenciesFramework].Count,
                        MaximumDirectPackagesPerFramework,
                        "direct packages in one framework");
                }

                return;
            }

            if (_insideLogs && depth == 3)
            {
                _pendingLogProperty = name;
            }
        }

        private void ProcessValue(ref Utf8JsonReader reader)
        {
            var depth = reader.CurrentDepth;
            if (_pendingLibraryType && depth == 3)
            {
                _currentLibraryIsPackage = reader.TokenType == JsonTokenType.String &&
                    reader.GetString()?.Equals("package", StringComparison.OrdinalIgnoreCase) == true;
                _pendingLibraryType = false;
            }

            if (_pendingLibraryProperty is not null && depth == 3)
            {
                var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                if (_pendingLibraryProperty == "path")
                {
                    _currentLibraryPath = value;
                }
                else if (_pendingLibraryProperty == "sha512")
                {
                    _currentLibrarySha512 = value;
                }

                _pendingLibraryProperty = null;
            }

            if (_insideLibraryFiles && depth == 4 && reader.TokenType == JsonTokenType.String &&
                reader.GetString()?.EndsWith(".signature.p7s", StringComparison.OrdinalIgnoreCase) == true)
            {
                _currentLibraryHasSignature = true;
            }

            if (_insideLogs && _log is not null && _pendingLogProperty is not null && depth == 3)
            {
                switch (_pendingLogProperty)
                {
                    case "code":
                        _log.Code = ReadScalarAsString(ref reader);
                        break;
                    case "level":
                        _log.Level = ReadScalarAsString(ref reader);
                        break;
                    case "message":
                        _log.Message = ReadScalarAsString(ref reader);
                        break;
                    case "file":
                        _log.File = ReadScalarAsString(ref reader);
                        break;
                    case "lineNumber":
                        _log.Line = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var line)
                            ? line
                            : null;
                        break;
                }

                _pendingLogProperty = null;
            }

            ClearPendingScalarProperties(depth);
        }

        private void ClearPendingScalarProperties(int depth)
        {
            if (_pendingLibraryType && depth == 3)
            {
                _pendingLibraryType = false;
            }

            if (_pendingLibraryProperty is not null && depth == 3)
            {
                _pendingLibraryProperty = null;
            }

            if (_pendingLogProperty is not null && depth == 3)
            {
                _pendingLogProperty = null;
            }

            if (_pendingFrameworks && depth == 2)
            {
                _pendingFrameworks = false;
            }

            if (_pendingDependencies && depth == 4)
            {
                _pendingDependencies = false;
            }
        }

        private void FlushLibrary()
        {
            if (_currentLibrary is not null && _currentLibraryIsPackage)
            {
                _packageLibraries[_currentLibrary] = new LibraryMetadata(
                    _currentLibrary,
                    _currentLibraryPath,
                    _currentLibrarySha512,
                    _currentLibraryHasSignature);
                EnsureCollectionLimit(
                    _packageLibraries.Count,
                    MaximumPackageLibraries,
                    "package libraries");
            }

            _currentLibrary = null;
            _currentLibraryIsPackage = false;
            _currentLibraryHasSignature = false;
            _currentLibraryPath = null;
            _currentLibrarySha512 = null;
            _pendingLibraryFiles = false;
            _insideLibraryFiles = false;
            _pendingLibraryProperty = null;
            _pendingLibraryType = false;
        }

        private IReadOnlyList<ResolvedPackageDependencyEdge> ResolveDependencyEdges(
            IReadOnlyList<PackageInventoryItem> inventory)
        {
            var versions = inventory
                .GroupBy(item => TargetIdentity(item.Framework, item.RuntimeIdentifier), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(child => child.Key, child => child.Select(item => item.ResolvedVersion)
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);
            return _dependencyEdges.Select(edge =>
                {
                    var target = TargetIdentity(edge.Parent.Framework, edge.Parent.RuntimeIdentifier);
                    if (!versions.TryGetValue(target, out var packages) ||
                        !packages.TryGetValue(edge.ChildPackageId, out var childVersions) ||
                        childVersions.Length != 1)
                    {
                        return null;
                    }

                    return new ResolvedPackageDependencyEdge(
                        projectPath,
                        edge.Parent.Framework,
                        edge.Parent.RuntimeIdentifier,
                        edge.Parent.Id,
                        edge.Parent.Version,
                        edge.ChildPackageId,
                        childVersions[0]);
                })
                .Where(item => item is not null)
                .Select(item => item!)
                .DistinctBy(item => string.Join('|', item.Framework, item.RuntimeIdentifier, item.ParentPackageId,
                    item.ParentResolvedVersion, item.ChildPackageId, item.ChildResolvedVersion), StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item.Framework, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.RuntimeIdentifier, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.ParentPackageId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.ChildPackageId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private PackageCacheMetadata ResolveCacheMetadata(
            LibraryMetadata library,
            IDictionary<string, PackageCacheMetadata> cache,
            IReadOnlyList<string> trustedPackageFolders)
        {
            if (string.IsNullOrWhiteSpace(library.Path))
            {
                return PackageCacheMetadata.Empty;
            }

            if (cache.TryGetValue(library.Path, out var known))
            {
                return known;
            }

            foreach (var folder in trustedPackageFolders)
            {
                var metadata = ReadCacheMetadata(folder, library.Path);
                if (metadata != PackageCacheMetadata.Empty)
                {
                    cache[library.Path] = metadata;
                    return metadata;
                }
            }

            cache[library.Path] = PackageCacheMetadata.Empty;
            return PackageCacheMetadata.Empty;
        }

        private static bool IsTrustedPackageFolder(
            string packageFolder,
            string project,
            string? trustedPackagesDirectory)
        {
            try
            {
                if (!Path.IsPathFullyQualified(packageFolder))
                {
                    return false;
                }

                var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageFolder));
                if (!IsRegularDirectory(candidate))
                {
                    return false;
                }

                if (trustedPackagesDirectory is not null &&
                    PathsEqual(candidate, trustedPackagesDirectory) &&
                    IsRegularDirectory(trustedPackagesDirectory))
                {
                    return true;
                }

                var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(project))!;
                var repositoryRoot = FindRepositoryRoot(projectDirectory);
                if (IsWithin(candidate, repositoryRoot) &&
                    ProjectDiscovery.IsSafelyContained(repositoryRoot, candidate))
                {
                    return true;
                }

                var configured = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
                if (!string.IsNullOrWhiteSpace(configured) &&
                    PathsEqual(candidate, configured) &&
                    IsRegularDirectory(configured))
                {
                    return true;
                }

                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var defaultCache = string.IsNullOrWhiteSpace(profile)
                    ? null
                    : Path.Combine(profile, ".nuget", "packages");
                return defaultCache is not null &&
                    PathsEqual(candidate, defaultCache) &&
                    IsRegularDirectory(defaultCache);
            }
            catch (Exception exception) when (exception is
                ArgumentException or
                IOException or
                NotSupportedException or
                PathTooLongException or
                UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static string FindRepositoryRoot(string start)
        {
            var directory = new DirectoryInfo(start);
            while (directory.Parent is not null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                    File.Exists(Path.Combine(directory.FullName, ".git")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            return start;
        }

        private static bool IsWithin(string candidate, string root)
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
            return !Path.IsPathFullyQualified(relative) &&
                   relative != ".." &&
                   !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
        }

        private static bool PathsEqual(string left, string right) => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        private static bool IsRegularDirectory(string path)
        {
            var info = new DirectoryInfo(Path.GetFullPath(path));
            return info.Exists && !info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }

        private static StringComparer PathComparer() =>
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        private static PackageCacheMetadata ReadCacheMetadata(string packageFolder, string libraryPath)
        {
            try
            {
                if (!Path.IsPathFullyQualified(packageFolder) || Path.IsPathRooted(libraryPath))
                {
                    return PackageCacheMetadata.Empty;
                }

                var root = Path.GetFullPath(packageFolder);
                var packageDirectory = Path.GetFullPath(Path.Combine(root, libraryPath.Replace('/', Path.DirectorySeparatorChar)));
                var relative = Path.GetRelativePath(root, packageDirectory);
                if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    Path.IsPathRooted(relative) ||
                    !IsRegularDirectory(root) ||
                    !Directory.Exists(packageDirectory) ||
                    !ProjectDiscovery.IsSafelyContained(root, packageDirectory))
                {
                    return PackageCacheMetadata.Empty;
                }

                var metadataPath = Path.Combine(packageDirectory, ".nupkg.metadata");
                var info = new FileInfo(metadataPath);
                if (!info.Exists ||
                    info.Attributes.HasFlag(FileAttributes.Directory) ||
                    info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                    info.Length > 64 * 1024)
                {
                    return PackageCacheMetadata.Empty;
                }

                using var stream = info.OpenRead();
                using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
                var rootElement = document.RootElement;
                var source = rootElement.TryGetProperty("source", out var sourceElement)
                    ? NormalizeSource(sourceElement.GetString())
                    : null;
                var contentHash = rootElement.TryGetProperty("contentHash", out var hashElement)
                    ? hashElement.GetString()
                    : null;
                return new PackageCacheMetadata(
                    source,
                    NullIfWhiteSpace(contentHash),
                    IsRegularFile(Path.Combine(packageDirectory, ".signature.p7s")));
            }
            catch (Exception exception) when (exception is
                ArgumentException or
                IOException or
                JsonException or
                NotSupportedException or
                PathTooLongException or
                UnauthorizedAccessException)
            {
                return PackageCacheMetadata.Empty;
            }
        }

        private static bool IsRegularFile(string path)
        {
            var info = new FileInfo(path);
            return info.Exists &&
                !info.Attributes.HasFlag(FileAttributes.Directory) &&
                !info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }

        private static bool HasEffectivePackageSourceMapping(
            string project,
            string trustedRoot,
            IReadOnlyList<PackageInventoryItem> inventory,
            IReadOnlySet<string> activeSources)
        {
            if (inventory.Count == 0 ||
                !ProjectDiscovery.IsSafelyContained(trustedRoot, project))
            {
                return false;
            }

            var packageSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var mappings = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var configPath in RepositoryNuGetConfigPaths(project, trustedRoot))
            {
                if (!TryApplyNuGetConfig(configPath, packageSources, mappings))
                {
                    return false;
                }
            }

            if (mappings.Count == 0 ||
                activeSources.Any(active => !packageSources.Values.Contains(active, StringComparer.OrdinalIgnoreCase)))
            {
                return false;
            }

            foreach (var package in inventory)
            {
                var source = NormalizeSource(package.PackageSource);
                var mapped = mappings.Any(mapping =>
                    packageSources.TryGetValue(mapping.Key, out var configuredSource) &&
                    (source is null || configuredSource.Equals(source, StringComparison.OrdinalIgnoreCase)) &&
                    mapping.Value.Any(pattern => MatchesPackagePattern(pattern, package.Id)));
                if (!mapped)
                {
                    return false;
                }
            }

            return true;
        }

        private static IReadOnlyList<string> RepositoryNuGetConfigPaths(string project, string trustedRoot)
        {
            var root = Path.GetFullPath(trustedRoot);
            var current = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(project))!);
            var directories = new Stack<string>();
            while (ProjectDiscovery.IsSafelyContained(root, current.FullName))
            {
                directories.Push(current.FullName);
                if (PathsEqual(current.FullName, root) || current.Parent is null)
                {
                    break;
                }

                current = current.Parent;
            }

            var paths = new List<string>();
            var seen = new HashSet<string>(PathComparer());
            while (directories.Count > 0)
            {
                var directory = directories.Pop();
                foreach (var fileName in new[] { "NuGet.Config", "nuget.config" })
                {
                    var candidate = Path.Combine(directory, fileName);
                    if (seen.Add(candidate) &&
                        File.Exists(candidate) &&
                        ProjectDiscovery.IsSafelyContained(root, candidate))
                    {
                        paths.Add(candidate);
                    }
                }
            }

            return paths;
        }

        private static bool TryApplyNuGetConfig(
            string configPath,
            IDictionary<string, string> packageSources,
            IDictionary<string, IReadOnlyList<string>> mappings)
        {
            try
            {
                var info = new FileInfo(configPath);
                if (!info.Exists || info.Length > MaximumNuGetConfigBytes)
                {
                    return false;
                }

                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaximumNuGetConfigBytes,
                };
                using var reader = XmlReader.Create(configPath, settings);
                var document = XDocument.Load(reader, LoadOptions.None);
                foreach (var section in document.Root?.Elements() ?? [])
                {
                    if (section.Name.LocalName.Equals("packageSources", StringComparison.OrdinalIgnoreCase))
                    {
                        ApplyPackageSources(section, Path.GetDirectoryName(configPath)!, packageSources);
                    }
                    else if (section.Name.LocalName.Equals("packageSourceMapping", StringComparison.OrdinalIgnoreCase))
                    {
                        ApplyPackageSourceMappings(section, mappings);
                    }
                }

                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException)
            {
                return false;
            }
        }

        private static void ApplyPackageSources(
            XElement section,
            string configDirectory,
            IDictionary<string, string> packageSources)
        {
            foreach (var element in section.Elements())
            {
                if (element.Name.LocalName.Equals("clear", StringComparison.OrdinalIgnoreCase))
                {
                    packageSources.Clear();
                    continue;
                }

                var key = AttributeValue(element, "key");
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (element.Name.LocalName.Equals("remove", StringComparison.OrdinalIgnoreCase))
                {
                    packageSources.Remove(key);
                }
                else if (element.Name.LocalName.Equals("add", StringComparison.OrdinalIgnoreCase) &&
                         NormalizeConfiguredSource(AttributeValue(element, "value"), configDirectory) is { } source)
                {
                    packageSources[key] = source;
                }
            }
        }

        private static void ApplyPackageSourceMappings(
            XElement section,
            IDictionary<string, IReadOnlyList<string>> mappings)
        {
            foreach (var element in section.Elements())
            {
                if (element.Name.LocalName.Equals("clear", StringComparison.OrdinalIgnoreCase))
                {
                    mappings.Clear();
                    continue;
                }

                var key = AttributeValue(element, "key");
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (element.Name.LocalName.Equals("remove", StringComparison.OrdinalIgnoreCase))
                {
                    mappings.Remove(key);
                    continue;
                }

                if (!element.Name.LocalName.Equals("packageSource", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                mappings[key] = element.Elements()
                    .Where(item => item.Name.LocalName.Equals("package", StringComparison.OrdinalIgnoreCase))
                    .Select(item => AttributeValue(item, "pattern")?.Trim())
                    .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
                    .Select(pattern => pattern!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }

        private static string? AttributeValue(XElement element, string name) => element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?
            .Value;

        private static string? NormalizeConfiguredSource(string? value, string configDirectory)
        {
            var normalized = NormalizeSource(value);
            if (normalized is not null)
            {
                return normalized;
            }

            var candidate = value?.Trim();
            if (string.IsNullOrWhiteSpace(candidate) ||
                Uri.TryCreate(candidate, UriKind.Absolute, out _))
            {
                return null;
            }

            return NormalizeSource(Path.GetFullPath(candidate, configDirectory));
        }

        private static bool MatchesPackagePattern(string pattern, string packageId)
        {
            if (pattern.Equals("*", StringComparison.Ordinal))
            {
                return true;
            }

            var wildcard = pattern.IndexOf('*');
            return wildcard < 0
                ? pattern.Equals(packageId, StringComparison.OrdinalIgnoreCase)
                : wildcard == pattern.Length - 1 &&
                  pattern.LastIndexOf('*') == wildcard &&
                  packageId.StartsWith(pattern[..wildcard], StringComparison.OrdinalIgnoreCase);
        }

        private static string? NormalizeSource(string? value)
        {
            var source = value?.Trim();
            if (string.IsNullOrWhiteSpace(source))
            {
                return null;
            }

            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrEmpty(uri.UserInfo) &&
                string.IsNullOrEmpty(uri.Query) &&
                string.IsNullOrEmpty(uri.Fragment))
            {
                return uri.AbsoluteUri.TrimEnd('/');
            }

            return Uri.TryCreate(source, UriKind.Absolute, out var local) &&
                local.IsFile &&
                string.IsNullOrEmpty(local.UserInfo) &&
                string.IsNullOrEmpty(local.Query) &&
                string.IsNullOrEmpty(local.Fragment)
                ? "local"
                : null;
        }

        private static string CreateOpaqueUntrustedSourceIdentity(string value) =>
            $"untrusted:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())))}";

        private static string TargetIdentity(string framework, string? runtimeIdentifier) =>
            $"{framework}\n{runtimeIdentifier}";

        private static string? NullIfWhiteSpace(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value;

        private void FlushLog()
        {
            if (_log is not null &&
                !string.IsNullOrWhiteSpace(_log.Code) &&
                _log.Code.StartsWith("NU", StringComparison.OrdinalIgnoreCase) &&
                (_log.Level.Equals("warning", StringComparison.OrdinalIgnoreCase) ||
                 _log.Level.Equals("error", StringComparison.OrdinalIgnoreCase)))
            {
                var code = _log.Code.ToUpperInvariant();
                _diagnostics.Add(new Diagnostic(
                    "PM005",
                    _log.Level.Equals("error", StringComparison.OrdinalIgnoreCase)
                        ? DiagnosticSeverity.Error
                        : DiagnosticSeverity.Warning,
                    "NuGet restore problem",
                    $"NuGet recorded {code} in project.assets.json.",
                    projectPath,
                    _log.File.Length > 0 ? _log.File : projectPath,
                    _log.Line,
                    _log.Message,
                    "Resolve the underlying NuGet restore issue, then run PackageMedic again.",
                    DiagnosticConfidence.High,
                    code));
                EnsureCollectionLimit(
                    _diagnostics.Count,
                    MaximumRestoreDiagnostics,
                    "NuGet restore diagnostics");
            }

            _log = null;
            _pendingLogProperty = null;
        }

        private static IReadOnlySet<string> ResolveDirectPackages(
            string targetFramework,
            IReadOnlyDictionary<string, HashSet<string>> directByFramework)
        {
            if (directByFramework.TryGetValue(targetFramework, out var exact))
            {
                return exact;
            }

            return directByFramework.Values
                .SelectMany(item => item)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static string ReadScalarAsString(ref Utf8JsonReader reader) => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? string.Empty,
            JsonTokenType.Number => Encoding.UTF8.GetString(reader.ValueSpan),
            JsonTokenType.True => bool.TrueString,
            JsonTokenType.False => bool.FalseString,
            _ => string.Empty,
        };

        private static void EnsureCollectionLimit(int count, int maximum, string description)
        {
            if (count > maximum)
            {
                throw new InvalidDataException(
                    $"project.assets.json contains more than {maximum} {description}, exceeding the safety limit.");
            }
        }

        private static string SplitLibraryKey(string key)
        {
            var separator = key.LastIndexOf('/');
            return separator > 0 ? key[..separator] : key;
        }

        private static (string Id, string Version) SplitLibraryIdentity(string key)
        {
            var separator = key.LastIndexOf('/');
            return separator > 0
                ? (key[..separator], key[(separator + 1)..])
                : (key, "unknown");
        }

        private sealed class LogEntry
        {
            public string Code { get; set; } = string.Empty;

            public string File { get; set; } = string.Empty;

            public string Level { get; set; } = string.Empty;

            public int? Line { get; set; }

            public string Message { get; set; } = string.Empty;
        }

        private sealed record TargetPackage(
            string Framework,
            string? RuntimeIdentifier,
            string Id,
            string Version)
        {
            public string Identity => $"{Id}/{Version}";
        }

        private sealed record RawDependencyEdge(TargetPackage Parent, string ChildPackageId);

        private sealed record LibraryMetadata(
            string Identity,
            string? Path,
            string? Sha512,
            bool SignaturePresent);

        private sealed record PackageCacheMetadata(
            string? Source,
            string? ContentHash,
            bool? SignaturePresent)
        {
            public static PackageCacheMetadata Empty { get; } = new(null, null, null);
        }
    }
}

public sealed record AssetsReadResult(
    IReadOnlySet<string> ResolvedPackages,
    IReadOnlySet<string> TransitivePackages,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<PackageInventoryItem> PackageInventory,
    IReadOnlyList<ResolvedPackageDependencyEdge> DependencyEdges,
    int PackageSourceCount,
    bool PackageSourceMappingEnabled);
