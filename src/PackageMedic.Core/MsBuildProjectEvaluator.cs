using System.Collections.Concurrent;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace PackageMedic.Core;

public sealed class MsBuildProjectEvaluator
{
    private readonly IProcessRunner processRunner;
    private readonly TimeSpan timeout;
    private readonly SemaphoreSlim processGate;

    private static readonly string[] QueriedProperties =
    [
        "ManagePackageVersionsCentrally",
        "CentralPackageTransitivePinningEnabled",
        "TargetFramework",
        "TargetFrameworks",
        "ProjectAssetsFile",
        "BaseIntermediateOutputPath",
        "MSBuildProjectDirectory",
    ];

    public MsBuildProjectEvaluator(IProcessRunner processRunner)
        : this(
            processRunner,
            AnalysisExecutionOptions.Default.MsBuildEvaluationTimeout,
            AnalysisExecutionOptions.Default.MaxDegreeOfParallelism)
    {
    }

    public MsBuildProjectEvaluator(IProcessRunner processRunner, TimeSpan timeout)
        : this(processRunner, timeout, AnalysisExecutionOptions.Default.MaxDegreeOfParallelism)
    {
    }

    public MsBuildProjectEvaluator(IProcessRunner processRunner, TimeSpan timeout, int maxDegreeOfParallelism)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan || timeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The MSBuild evaluation timeout must be greater than zero and no longer than one hour.");
        }

        if (maxDegreeOfParallelism is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism));
        }

        this.timeout = timeout;
        processGate = new SemaphoreSlim(maxDegreeOfParallelism, maxDegreeOfParallelism);
    }

    public Task<EvaluatedProject> EvaluateAsync(string projectPath, CancellationToken cancellationToken) =>
        EvaluateAsync(projectPath, new XmlItemLineLocator(), cancellationToken);

    internal async Task<EvaluatedProject> EvaluateAsync(
        string projectPath,
        XmlItemLineLocator lineLocator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lineLocator);
        var outer = await QueryAsync(projectPath, null, cancellationToken).ConfigureAwait(false);
        var frameworks = SplitFrameworks(outer.Properties);
        IReadOnlyList<QueryResult> evaluations = frameworks.Count > 1
            ? await Task.WhenAll(frameworks.Select(framework => QueryAsync(projectPath, framework, cancellationToken)))
                .ConfigureAwait(false)
            : [outer];

        var nearestCentralProps = FindNearestCentralProps(projectPath);
        var directPackages = new List<DirectPackageReference>();
        var centralVersions = new List<CentralPackageVersion>();
        foreach (var evaluation in evaluations)
        {
            var occurrenceCounters = new Dictionary<(string File, string Item, string Id, string Version), int>();
            foreach (var item in evaluation.PackageReferences)
            {
                var source = GetMetadata(item, "DefiningProjectFullPath") ?? projectPath;
                var id = GetMetadata(item, "Identity") ?? string.Empty;
                var version = NullIfEmpty(GetMetadata(item, "Version"));
                var overrideVersion = NullIfEmpty(GetMetadata(item, "VersionOverride"));
                var line = lineLocator.FindLine(source, "PackageReference", id, version ?? overrideVersion, occurrenceCounters);
                if (!string.IsNullOrWhiteSpace(id))
                {
                    directPackages.Add(new DirectPackageReference(id, version, overrideVersion, source, line, evaluation.TargetFramework));
                }
            }

            foreach (var item in evaluation.PackageVersions)
            {
                var source = GetMetadata(item, "DefiningProjectFullPath") ?? nearestCentralProps ?? projectPath;
                var id = GetMetadata(item, "Identity") ?? string.Empty;
                var version = GetMetadata(item, "Version") ?? string.Empty;
                var line = lineLocator.FindLine(source, "PackageVersion", id, version, occurrenceCounters);
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(version))
                {
                    centralVersions.Add(new CentralPackageVersion(id, version, source, line, evaluation.TargetFramework));
                }
            }
        }

        var properties = evaluations[0].Properties;
        var assetsFile = GetProperty(properties, "ProjectAssetsFile");
        if (string.IsNullOrWhiteSpace(assetsFile))
        {
            var intermediate = GetProperty(properties, "BaseIntermediateOutputPath");
            if (string.IsNullOrWhiteSpace(intermediate))
            {
                intermediate = "obj";
            }

            assetsFile = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectPath)!, intermediate, "project.assets.json"));
        }
        else if (!Path.IsPathRooted(assetsFile))
        {
            assetsFile = Path.GetFullPath(assetsFile, Path.GetDirectoryName(projectPath)!);
        }

        return new EvaluatedProject(
            projectPath,
            IsTrue(GetProperty(properties, "ManagePackageVersionsCentrally")),
            IsTrue(GetProperty(properties, "CentralPackageTransitivePinningEnabled")),
            frameworks,
            DeduplicateReferences(directPackages),
            DeduplicateCentralVersions(centralVersions),
            assetsFile);
    }

    private async Task<QueryResult> QueryAsync(string projectPath, string? targetFramework, CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "msbuild",
            projectPath,
            "-nologo",
            "-verbosity:quiet",
            $"-getProperty:{string.Join(',', QueriedProperties)}",
            "-getItem:PackageReference;PackageVersion",
        };
        if (!string.IsNullOrWhiteSpace(targetFramework))
        {
            arguments.Add($"-property:TargetFramework={targetFramework}");
        }

        ProcessResult result;
        await processGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                result = await processRunner.RunAsync(
                    "dotnet",
                    arguments,
                    Path.GetDirectoryName(projectPath)!,
                    timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    $"MSBuild evaluation timed out for '{projectPath}' after {timeout.TotalSeconds:0} seconds.");
            }
        }
        finally
        {
            processGate.Release();
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"MSBuild evaluation failed for '{projectPath}': {CompactError(result)}");
        }

        if (result.StandardOutputTruncated)
        {
            throw new InvalidOperationException(
                $"MSBuild evaluation output exceeded the {ProcessRunner.DefaultMaximumOutputCharacters}-character safety limit for '{projectPath}'.");
        }

        using var document = ParseJsonOutput(result.StandardOutput, projectPath);
        var root = document.RootElement;
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("Properties", out var propertiesElement))
        {
            foreach (var property in propertiesElement.EnumerateObject())
            {
                properties[property.Name] = property.Value.ToString();
            }
        }

        var packageReferences = ReadItems(root, "PackageReference");
        var packageVersions = ReadItems(root, "PackageVersion");
        return new QueryResult(targetFramework, properties, packageReferences, packageVersions);
    }

    private static JsonDocument ParseJsonOutput(string output, string projectPath)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            throw new InvalidOperationException($"MSBuild did not return evaluation JSON for '{projectPath}'.");
        }

        return JsonDocument.Parse(output[start..(end + 1)]);
    }

    private static IReadOnlyList<Dictionary<string, string>> ReadItems(JsonElement root, string itemName)
    {
        if (!root.TryGetProperty("Items", out var items) ||
            !items.TryGetProperty(itemName, out var itemArray) ||
            itemArray.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<Dictionary<string, string>>();
        foreach (var item in itemArray.EnumerateArray())
        {
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in item.EnumerateObject())
            {
                metadata[property.Name] = property.Value.ToString();
            }

            result.Add(metadata);
        }

        return result;
    }

    private static IReadOnlyList<string> SplitFrameworks(IReadOnlyDictionary<string, string> properties)
    {
        var frameworks = GetProperty(properties, "TargetFrameworks")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (frameworks.Length > 0)
        {
            return frameworks;
        }

        var framework = GetProperty(properties, "TargetFramework");
        return string.IsNullOrWhiteSpace(framework) ? [] : [framework];
    }

    private static IReadOnlyList<DirectPackageReference> DeduplicateReferences(IEnumerable<DirectPackageReference> references) => references
        .DistinctBy(item => (item.Id.ToUpperInvariant(), item.Version, item.VersionOverride, item.SourceFile.ToUpperInvariant(), item.Line, item.TargetFramework))
        .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.TargetFramework, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static IReadOnlyList<CentralPackageVersion> DeduplicateCentralVersions(IEnumerable<CentralPackageVersion> versions) => versions
        .DistinctBy(item => (item.Id.ToUpperInvariant(), item.Version, item.SourceFile.ToUpperInvariant(), item.Line, item.TargetFramework))
        .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.SourceFile, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.Line)
        .ToArray();

    private static string? FindNearestCentralProps(string projectPath)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(projectPath)!);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Directory.Packages.props");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string CompactError(ProcessResult result)
    {
        var error = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        return string.Join(' ', error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3));
    }

    private static string GetProperty(IReadOnlyDictionary<string, string> properties, string name) =>
        properties.TryGetValue(name, out var value) ? value : string.Empty;

    private static string? GetMetadata(IReadOnlyDictionary<string, string> metadata, string name) =>
        metadata.TryGetValue(name, out var value) ? value : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool IsTrue(string? value) => value?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

    private sealed record QueryResult(
        string? TargetFramework,
        IReadOnlyDictionary<string, string> Properties,
        IReadOnlyList<Dictionary<string, string>> PackageReferences,
        IReadOnlyList<Dictionary<string, string>> PackageVersions);
}

public sealed record EvaluatedProject(
    string ProjectPath,
    bool ManagePackageVersionsCentrally,
    bool CentralPackageTransitivePinningEnabled,
    IReadOnlyList<string> TargetFrameworks,
    IReadOnlyList<DirectPackageReference> DirectPackages,
    IReadOnlyList<CentralPackageVersion> CentralVersions,
    string AssetsFile);

internal sealed class XmlItemLineLocator
{
    internal const long MaximumSourceXmlBytes = 64L * 1024 * 1024;

    private readonly ConcurrentDictionary<string, Lazy<IReadOnlyList<XmlItemLocation>>> cache =
        new(StringComparer.OrdinalIgnoreCase);

    public int? FindLine(
        string sourceFile,
        string itemName,
        string id,
        string? version,
        IDictionary<(string File, string Item, string Id, string Version), int> counters)
    {
        if (!File.Exists(sourceFile))
        {
            return null;
        }

        var locations = cache.GetOrAdd(
            sourceFile,
            static file => new Lazy<IReadOnlyList<XmlItemLocation>>(
                () => ReadLocations(file),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        var candidates = locations.Where(item =>
            item.ItemName.Equals(itemName, StringComparison.OrdinalIgnoreCase) &&
            item.Id.Equals(id, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(version) ||
             (string.IsNullOrWhiteSpace(item.Version) && string.IsNullOrWhiteSpace(item.VersionOverride)) ||
             item.Version.Equals(version, StringComparison.OrdinalIgnoreCase) ||
             item.VersionOverride.Equals(version, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        var key = (sourceFile.ToUpperInvariant(), itemName.ToUpperInvariant(), id.ToUpperInvariant(), version?.ToUpperInvariant() ?? string.Empty);
        counters.TryGetValue(key, out var index);
        counters[key] = index + 1;
        return candidates[Math.Min(index, candidates.Length - 1)].Line;
    }

    private static IReadOnlyList<XmlItemLocation> ReadLocations(string sourceFile)
    {
        try
        {
            using var stream = new FileStream(
                sourceFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length > MaximumSourceXmlBytes)
            {
                throw new InvalidDataException(
                    $"MSBuild source file '{sourceFile}' exceeds the {MaximumSourceXmlBytes}-byte safety limit.");
            }

            using var reader = XmlReader.Create(
                stream,
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaximumSourceXmlBytes,
                });
            var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
            return document.Descendants()
                .Where(element => element.Name.LocalName is "PackageReference" or "PackageVersion")
                .Select(element => new XmlItemLocation(
                    element.Name.LocalName,
                    (string?)element.Attribute("Include") ?? (string?)element.Attribute("Update") ?? string.Empty,
                    (string?)element.Attribute("Version") ?? element.Elements().FirstOrDefault(child => child.Name.LocalName == "Version")?.Value ?? string.Empty,
                    (string?)element.Attribute("VersionOverride") ?? element.Elements().FirstOrDefault(child => child.Name.LocalName == "VersionOverride")?.Value ?? string.Empty,
                    (element as IXmlLineInfo)?.LineNumber ?? 0))
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .ToArray();
        }
        catch (Exception exception) when (exception is XmlException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private sealed record XmlItemLocation(string ItemName, string Id, string Version, string VersionOverride, int Line);
}
