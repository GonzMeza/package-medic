using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PackageMedic.Core;

public sealed partial class ProjectDiscovery
{
    private static readonly string[] IgnoredDirectoryNames = ["bin", "obj", ".git", ".vs", "node_modules"];

    public DiscoveryResult Discover(string? requestedPath)
    {
        var target = Path.GetFullPath(string.IsNullOrWhiteSpace(requestedPath) ? Directory.GetCurrentDirectory() : requestedPath);
        if (!File.Exists(target) && !Directory.Exists(target))
        {
            throw new ArgumentException($"The target path does not exist: {target}");
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
                var projects = ReadSolutionProjects(target);
                if (projects.Count == 0)
                {
                    throw new InvalidOperationException($"No C# projects were found in solution '{target}'.");
                }

                return new DiscoveryResult(target, [target], projects, [target]);
            }

            throw new ArgumentException("The target must be a directory, .csproj, .sln, or .slnx file.");
        }

        var solutions = EnumerateFiles(target, "*.sln")
            .Concat(EnumerateFiles(target, "*.slnx"))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var projectsInDirectory = EnumerateFiles(target, "*.csproj")
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (projectsInDirectory.Length == 0)
        {
            throw new InvalidOperationException($"No .csproj files were found under '{target}'.");
        }

        IReadOnlyList<string> restoreTargets = solutions.Length == 1 ? solutions : projectsInDirectory;
        return new DiscoveryResult(target, solutions, projectsInDirectory, restoreTargets);
    }

    private static IReadOnlyList<string> ReadSolutionProjects(string solutionPath)
    {
        var directory = Path.GetDirectoryName(solutionPath)!;
        var projects = Path.GetExtension(solutionPath).Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            ? ReadSlnxProjects(solutionPath)
            : ReadSlnProjects(solutionPath);

        return projects
            .Select(path => Path.GetFullPath(path, directory))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> ReadSlnProjects(string solutionPath)
    {
        foreach (var line in File.ReadLines(solutionPath))
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
        var document = XDocument.Load(solutionPath);
        return document.Descendants()
            .Where(element => element.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase))
            .Select(element => (string?)element.Attribute("Path"))
            .Where(path => !string.IsNullOrWhiteSpace(path) && Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(path => path!);
    }

    private static IEnumerable<string> EnumerateFiles(string root, string pattern)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> files;
            IEnumerable<string> directories;
            try
            {
                files = Directory.EnumerateFiles(current, pattern, SearchOption.TopDirectoryOnly);
                directories = Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return Path.GetFullPath(file);
            }

            foreach (var directory in directories)
            {
                if (!IgnoredDirectoryNames.Contains(Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase))
                {
                    pending.Push(directory);
                }
            }
        }
    }

    [GeneratedRegex("^Project\\([^)]*\\)\\s*=\\s*\"[^\"]*\",\\s*\"([^\"]+)\"")]
    private static partial Regex SolutionProjectRegex();
}
