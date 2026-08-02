using System.Text.Json;

namespace PackageMedic.Core;

public sealed class AssetsFileReader
{
    public AssetsReadResult Read(string assetsFile, string projectPath)
    {
        if (!File.Exists(assetsFile))
        {
            throw new FileNotFoundException(
                $"NuGet assets file was not found for '{projectPath}'. Run without --no-restore or run dotnet restore first.",
                assetsFile);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(assetsFile));
        var root = document.RootElement;
        var libraries = ReadPackageLibraries(root);
        var resolved = ReadResolvedPackages(root, libraries);
        var direct = ReadDirectDependencies(root);
        var transitive = resolved.Where(id => !direct.Contains(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var diagnostics = ReadLogs(root, projectPath);
        return new AssetsReadResult(resolved, transitive, diagnostics);
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

    private static HashSet<string> ReadResolvedPackages(JsonElement root, IReadOnlySet<string> packageLibraries)
    {
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("targets", out var targets))
        {
            return packages;
        }

        foreach (var target in targets.EnumerateObject())
        {
            foreach (var library in target.Value.EnumerateObject())
            {
                var id = SplitLibraryKey(library.Name);
                if (packageLibraries.Contains(id))
                {
                    packages.Add(id);
                }
            }
        }

        return packages;
    }

    private static HashSet<string> ReadDirectDependencies(JsonElement root)
    {
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("project", out var project) ||
            !project.TryGetProperty("frameworks", out var frameworks))
        {
            return packages;
        }

        foreach (var framework in frameworks.EnumerateObject())
        {
            if (!framework.Value.TryGetProperty("dependencies", out var dependencies))
            {
                continue;
            }

            foreach (var dependency in dependencies.EnumerateObject())
            {
                packages.Add(dependency.Name);
            }
        }

        return packages;
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

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ToString() : string.Empty;

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
}

public sealed record AssetsReadResult(
    IReadOnlySet<string> ResolvedPackages,
    IReadOnlySet<string> TransitivePackages,
    IReadOnlyList<Diagnostic> Diagnostics);
