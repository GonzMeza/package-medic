using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace PackageMedic.Core;

public sealed partial class ProjectDiscovery
{
    internal const long MaximumSolutionFileBytes = 64L * 1024 * 1024;

    private static readonly string[] IgnoredDirectoryNames =
    [
        "bin",
        "obj",
        ".git",
        ".vs",
        "node_modules",
        "artifacts",
        "TestResults",
        "coverage",
        ".next",
        ".vinext",
        ".wrangler",
        "dist",
        "out",
    ];

    public DiscoveryResult Discover(string? requestedPath, string? containmentRoot = null)
    {
        var target = Path.GetFullPath(string.IsNullOrWhiteSpace(requestedPath) ? Directory.GetCurrentDirectory() : requestedPath);
        var boundary = containmentRoot is null ? null : Path.GetFullPath(containmentRoot);
        if (!File.Exists(target) && !Directory.Exists(target))
        {
            throw new ArgumentException($"The target path does not exist: {target}");
        }

        if (boundary is not null && !IsSafelyContained(boundary, target))
        {
            throw new ArgumentException("The target must resolve inside the analysis root without symbolic links or junctions.");
        }

        if (File.Exists(target))
        {
            var extension = Path.GetExtension(target);
            if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                return new DiscoveryResult(target, [], [target], [target]);
            }

            if (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                var projects = ReadSolutionProjects(target, boundary);
                if (projects.Count == 0)
                {
                    throw new InvalidOperationException($"No C# projects were found in solution '{target}'.");
                }

                return new DiscoveryResult(target, [target], projects, [target]);
            }

            throw new ArgumentException("The target must be a directory, .csproj, .sln, or .slnx file.");
        }

        var scan = EnumerateCandidates(target);
        var solutions = scan.Files
            .Where(path =>
                Path.GetExtension(path).Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(path).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var projectsInDirectory = scan.Files
            .Where(path => Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (projectsInDirectory.Length == 0)
        {
            throw new InvalidOperationException($"No .csproj files were found under '{target}'.");
        }

        IReadOnlyList<string> restoreTargets = solutions.Length == 1 ? solutions : projectsInDirectory;
        return new DiscoveryResult(target, solutions, projectsInDirectory, restoreTargets)
        {
            Errors = scan.Errors,
        };
    }

    private static IReadOnlyList<string> ReadSolutionProjects(string solutionPath, string? containmentRoot)
    {
        var directory = Path.GetDirectoryName(solutionPath)!;
        var projects = Path.GetExtension(solutionPath).Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            ? ReadSlnxProjects(solutionPath)
            : ReadSlnProjects(solutionPath);

        var resolved = projects
            .Select(path => Path.GetFullPath(path, directory))
            .ToArray();
        if (containmentRoot is not null && resolved.FirstOrDefault(path => !IsSafelyContained(containmentRoot, path)) is { } unsafeProject)
        {
            throw new InvalidOperationException(
                $"Solution '{solutionPath}' references project '{unsafeProject}' outside the safe analysis root or through a symbolic link.");
        }

        var missing = resolved
            .Where(path => !File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Solution '{solutionPath}' references missing C# project(s): {string.Join(", ", missing)}.");
        }

        return resolved
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> ReadSlnProjects(string solutionPath)
    {
        using var stream = OpenBoundedSolution(solutionPath);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            var match = SolutionProjectRegex().Match(line);
            if (match.Success && Path.GetExtension(match.Groups[1].Value).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                yield return match.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar);
            }
        }
    }

    private static IEnumerable<string> ReadSlnxProjects(string solutionPath)
    {
        try
        {
            using var stream = OpenBoundedSolution(solutionPath);
            using var reader = XmlReader.Create(
                stream,
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaximumSolutionFileBytes,
                });
            var document = XDocument.Load(reader);
            return document.Descendants()
                .Where(element => element.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase))
                .Select(element => (string?)element.Attribute("Path"))
                .Where(path => !string.IsNullOrWhiteSpace(path) && Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                .Select(path => path!)
                .ToArray();
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException(
                $"Solution '{solutionPath}' is not valid safe XML: {exception.Message}",
                exception);
        }
    }

    private static FileStream OpenBoundedSolution(string solutionPath)
    {
        var stream = new FileStream(
            solutionPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length <= MaximumSolutionFileBytes)
        {
            return stream;
        }

        stream.Dispose();
        throw new InvalidDataException(
            $"Solution '{solutionPath}' exceeds the {MaximumSolutionFileBytes}-byte safety limit.");
    }

    private static DiscoveryScan EnumerateCandidates(string root)
    {
        var candidateFiles = new List<string>();
        var errors = new List<string>();
        var pending = new Stack<string>();
        var visited = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = Path.GetFullPath(pending.Pop());
            if (!visited.Add(current))
            {
                continue;
            }

            string[] entries;
            try
            {
                // Materialize inside the guarded block because filesystem enumeration is lazy.
                entries = Directory.GetFileSystemEntries(current, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                errors.Add($"Could not enumerate directory '{current}': {exception.Message}");
                continue;
            }

            foreach (var entry in entries)
            {
                if (!TryGetAttributes(entry, out var attributes, out var error))
                {
                    errors.Add(error!);
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    if (!IgnoredDirectoryNames.Contains(Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase))
                    {
                        pending.Push(entry);
                    }

                    continue;
                }

                var extension = Path.GetExtension(entry);
                if (!extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                candidateFiles.Add(Path.GetFullPath(entry));
            }
        }

        return new DiscoveryScan(
            candidateFiles
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .ToArray(),
            errors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    private sealed record DiscoveryScan(IReadOnlyList<string> Files, IReadOnlyList<string> Errors);

    private static bool TryGetAttributes(string path, out FileAttributes attributes, out string? error)
    {
        try
        {
            attributes = File.GetAttributes(path);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            attributes = default;
            error = $"Could not inspect filesystem entry '{path}': {exception.Message}";
            return false;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // If an entry changes or becomes inaccessible during discovery, skipping it is safer
            // than following a path whose containment can no longer be established.
            return true;
        }
    }

    private static bool IsSafelyContained(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var normalizedCandidate = Path.GetFullPath(candidate);
        var relative = Path.GetRelativePath(normalizedRoot, normalizedCandidate);
        if (Path.IsPathFullyQualified(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return false;
        }

        if (relative.Equals(".", StringComparison.Ordinal))
        {
            return true;
        }

        var current = normalizedRoot;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (IsReparsePoint(current))
            {
                return false;
            }
        }

        return true;
    }

    [GeneratedRegex("^Project\\([^)]*\\)\\s*=\\s*\"[^\"]*\",\\s*\"([^\"]+)\"", RegexOptions.NonBacktracking)]
    private static partial Regex SolutionProjectRegex();
}
