using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace PackageMedic.Core;

public sealed class MsBuildProjectEvaluator
{
    private readonly IProcessRunner processRunner;
    private readonly TimeSpan timeout;

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
        : this(processRunner, AnalysisExecutionOptions.Default.MsBuildEvaluationTimeout)
    {
    }

    public MsBuildProjectEvaluator(IProcessRunner processRunner, TimeSpan timeout)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        this.timeout = timeout;
    }

    public async Task<EvaluatedProject> EvaluateAsync(string projectPath, CancellationToken cancellationToken)
    {
        var outer = await QueryAsync(projectPath, null, cancellationToken).ConfigureAwait(false);
        var frameworks = SplitFrameworks(outer.Properties);
        var evaluations = new List<QueryResult>();

        if (frameworks.Count > 1)
        {
            foreach (var framework in frameworks)
            {
                evaluations.Add(await QueryAsync(projectPath, framework, cancellationToken).ConfigureAwait(false));
            }
        }
        else
        {
            evaluations.Add(outer);
        }

        var lineLocator = new XmlItemLineLocator();
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
                var source = GetMetadata(item, "DefiningProjectFullPath") ?? FindNearestCentralProps(projectPath) ?? projectPath;
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

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        ProcessResult result;
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
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"MSBuild evaluation failed for '{projectPath}': {CompactError(result)}");
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
    private readonly Dictionary<string, IReadOnlyList<XmlItemLocation>> cache = new(StringComparer.OrdinalIgnoreCase);

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

        if (!cache.TryGetValue(sourceFile, out var locations))
        {
            locations = ReadLocations(sourceFile);
            cache[sourceFile] = locations;
        }

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
            var document = XDocument.Load(sourceFile, LoadOptions.SetLineInfo);
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
        catch (XmlException)
        {
            return [];
        }
    }

    private sealed record XmlItemLocation(string ItemName, string Id, string Version, string VersionOverride, int Line);
}
