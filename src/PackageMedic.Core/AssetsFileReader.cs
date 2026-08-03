using System.Text.Json;

namespace PackageMedic.Core;

public sealed class AssetsFileReader
{
    internal const long MaximumAssetsFileBytes = 512L * 1024 * 1024;

    public AssetsReadResult Read(string assetsFile, string projectPath)
    {
        if (!File.Exists(assetsFile))
        {
            throw new FileNotFoundException(
                $"NuGet assets file was not found for '{projectPath}'. Run without --no-restore or run dotnet restore first.",
                assetsFile);
        }

        using var stream = new FileStream(
            assetsFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length > MaximumAssetsFileBytes)
        {
            throw new InvalidDataException(
                $"NuGet assets file for '{projectPath}' exceeds the {MaximumAssetsFileBytes}-byte safety limit.");
        }

        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var libraries = ReadPackageLibraries(root);
        var directByFramework = ReadDirectDependencies(root);
        var inventory = ReadPackageInventory(root, libraries, directByFramework, projectPath);
        var resolved = inventory.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var direct = directByFramework.Values.SelectMany(item => item).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var transitive = resolved.Where(id => !direct.Contains(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var diagnostics = ReadLogs(root, projectPath);
        return new AssetsReadResult(resolved, transitive, diagnostics, inventory);
    }

    private static HashSet<string> ReadPackageLibraries(JsonElement root)
    {
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("libraries", out var libraries))
        {
            return packages;
        }

        foreach (var library in libraries.EnumerateObject())
        {
            if (library.Value.TryGetProperty("type", out var type) && type.GetString()?.Equals("package", StringComparison.OrdinalIgnoreCase) == true)
            {
                packages.Add(SplitLibraryKey(library.Name));
            }
        }

        return packages;
    }

    private static IReadOnlyList<PackageInventoryItem> ReadPackageInventory(
        JsonElement root,
        IReadOnlySet<string> packageLibraries,
        IReadOnlyDictionary<string, HashSet<string>> directByFramework,
        string projectPath)
    {
        var packages = new List<PackageInventoryItem>();
        if (!root.TryGetProperty("targets", out var targets))
        {
            return packages;
        }

        foreach (var target in targets.EnumerateObject())
        {
            var targetParts = target.Name.Split('/', 2);
            var framework = targetParts[0];
            var runtimeIdentifier = targetParts.Length == 2 ? targetParts[1] : null;
            var direct = ResolveDirectPackages(framework, directByFramework);
            foreach (var library in target.Value.EnumerateObject())
            {
                var (id, version) = SplitLibraryIdentity(library.Name);
                if (packageLibraries.Contains(id))
                {
                    packages.Add(new PackageInventoryItem(
                        projectPath,
                        framework,
                        id,
                        version,
                        direct.Contains(id) ? PackageDependencyKind.Direct : PackageDependencyKind.Transitive,
                        null,
                        "resolved",
                        runtimeIdentifier));
                }
            }
        }

        return packages
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
    }

    private static Dictionary<string, HashSet<string>> ReadDirectDependencies(JsonElement root)
    {
        var packages = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("project", out var project) ||
            !project.TryGetProperty("frameworks", out var frameworks))
        {
            return packages;
        }

        foreach (var framework in frameworks.EnumerateObject())
        {
            var frameworkPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            packages[framework.Name] = frameworkPackages;
            if (!framework.Value.TryGetProperty("dependencies", out var dependencies))
            {
                continue;
            }

            foreach (var dependency in dependencies.EnumerateObject())
            {
                frameworkPackages.Add(dependency.Name);
            }
        }

        return packages;
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

    private static IReadOnlyList<Diagnostic> ReadLogs(JsonElement root, string projectPath)
    {
        if (!root.TryGetProperty("logs", out var logs) || logs.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var diagnostics = new List<Diagnostic>();
        foreach (var log in logs.EnumerateArray())
        {
            var code = GetString(log, "code");
            var level = GetString(log, "level");
            if (string.IsNullOrWhiteSpace(code) || !code.StartsWith("NU", StringComparison.OrdinalIgnoreCase) ||
                (!level.Equals("warning", StringComparison.OrdinalIgnoreCase) && !level.Equals("error", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            diagnostics.Add(new Diagnostic(
                "PM005",
                level.Equals("error", StringComparison.OrdinalIgnoreCase) ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                "NuGet restore problem",
                $"NuGet recorded {code.ToUpperInvariant()} in project.assets.json.",
                projectPath,
                GetString(log, "file") is { Length: > 0 } file ? file : projectPath,
                GetInt(log, "lineNumber"),
                GetString(log, "message"),
                "Resolve the underlying NuGet restore issue, then run PackageMedic again.",
                DiagnosticConfidence.High,
                code.ToUpperInvariant()));
        }

        return RestoreRunner.Deduplicate(diagnostics);
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

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ToString() : string.Empty;

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
}

public sealed record AssetsReadResult(
    IReadOnlySet<string> ResolvedPackages,
    IReadOnlySet<string> TransitivePackages,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<PackageInventoryItem> PackageInventory);
