namespace PackageMedic.Core;

public sealed record ResolvedPackageDependencyEdge(
    string Project,
    string Framework,
    string? RuntimeIdentifier,
    string ParentPackageId,
    string ParentResolvedVersion,
    string ChildPackageId,
    string ChildResolvedVersion);

public static class DependencyGraphBuilder
{
    internal const int MaximumNodesPerTarget = 100_000;
    internal const int MaximumEdgesPerTarget = 1_000_000;
    internal const int MaximumPathSegmentsPerTarget = 1_000_000;
    internal const int MaximumRootReachabilityPerTarget = 1_000_000;
    internal const int MaximumTraversalOperationsPerTarget = 5_000_000;

    public static IReadOnlyList<PackageDependencyPath> BuildPaths(
        IReadOnlyList<PackageInventoryItem> inventory,
        IReadOnlyList<ResolvedPackageDependencyEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(edges);

        var paths = new List<PackageDependencyPath>();
        var edgesByTarget = edges
            .GroupBy(TargetKey, TargetKeyComparer.Instance)
            .ToDictionary(group => group.Key, group => group.ToArray(), TargetKeyComparer.Instance);
        foreach (var targetGroup in inventory.GroupBy(TargetKey, TargetKeyComparer.Instance))
        {
            var packages = targetGroup
                .GroupBy(item => NodeKey(item.Id, item.ResolvedVersion), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            if (packages.Count > MaximumNodesPerTarget)
            {
                throw new InvalidDataException(
                    $"The dependency target '{targetGroup.Key.Framework}' cannot contain more than {MaximumNodesPerTarget} package nodes.");
            }

            var targetEdges = edgesByTarget.GetValueOrDefault(targetGroup.Key, [])
                .Where(edge => packages.ContainsKey(NodeKey(edge.ParentPackageId, edge.ParentResolvedVersion)) &&
                               packages.ContainsKey(NodeKey(edge.ChildPackageId, edge.ChildResolvedVersion)))
                .GroupBy(edge => NodeKey(edge.ParentPackageId, edge.ParentResolvedVersion), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(edge => NodeKey(edge.ChildPackageId, edge.ChildResolvedVersion))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            var edgeCount = targetEdges.Values.Sum(children => (long)children.Length);
            if (edgeCount > MaximumEdgesPerTarget)
            {
                throw new InvalidDataException(
                    $"The dependency target '{targetGroup.Key.Framework}' cannot contain more than {MaximumEdgesPerTarget} dependency edges.");
            }

            var roots = packages.Values
                .Where(item => item.DependencyKind == PackageDependencyKind.Direct)
                .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.ResolvedVersion, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var selections = BuildCanonicalSelections(roots, packages, targetEdges);
            var reachableRoots = BuildReachableRoots(
                roots,
                packages,
                targetEdges,
                targetGroup.Key.Framework);
            long pathSegments = 0;

            foreach (var package in packages.Values
                         .OrderBy(item => item.DependencyKind)
                         .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item => item.ResolvedVersion, StringComparer.OrdinalIgnoreCase))
            {
                var key = NodeKey(package.Id, package.ResolvedVersion);
                if (!selections.TryGetValue(key, out var selected))
                {
                    continue;
                }

                var nodeKeys = ReconstructPath(key, selections);
                pathSegments += nodeKeys.Count;
                if (pathSegments > MaximumPathSegmentsPerTarget)
                {
                    throw new InvalidDataException(
                        $"The dependency target '{targetGroup.Key.Framework}' exceeds the {MaximumPathSegmentsPerTarget}-segment path safety limit.");
                }

                var alternativeRoots = reachableRoots.GetValueOrDefault(key, [])
                    .Where(item => !item.Equals(selected.RootPackageId, StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                paths.Add(new PackageDependencyPath(
                    package.Project,
                    package.Framework,
                    package.RuntimeIdentifier,
                    package.Id,
                    package.ResolvedVersion,
                    selected.RootPackageId,
                    selected.RootResolvedVersion,
                    nodeKeys.Select(keyValue =>
                    {
                        var node = packages[keyValue];
                        return new DependencyPathSegment(node.Id, node.ResolvedVersion);
                    }).ToArray(),
                    alternativeRoots));
            }
        }

        return paths
            .OrderBy(item => item.Project, PathComparer())
            .ThenBy(item => item.Framework, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.RuntimeIdentifier, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ResolvedVersion, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Dictionary<string, PathSelection> BuildCanonicalSelections(
        IReadOnlyList<PackageInventoryItem> roots,
        IReadOnlyDictionary<string, PackageInventoryItem> packages,
        IReadOnlyDictionary<string, string[]> adjacency)
    {
        var selections = new Dictionary<string, PathSelection>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        foreach (var root in roots)
        {
            var rootKey = NodeKey(root.Id, root.ResolvedVersion);
            if (selections.TryAdd(rootKey, new PathSelection(root.Id, root.ResolvedVersion, null)))
            {
                queue.Enqueue(rootKey);
            }
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!adjacency.TryGetValue(current, out var children))
            {
                continue;
            }

            var parent = selections[current];
            foreach (var child in children)
            {
                if (!packages.ContainsKey(child) || selections.ContainsKey(child))
                {
                    continue;
                }

                selections[child] = new PathSelection(parent.RootPackageId, parent.RootResolvedVersion, current);
                queue.Enqueue(child);
            }
        }

        return selections;
    }

    private static Dictionary<string, HashSet<string>> BuildReachableRoots(
        IReadOnlyList<PackageInventoryItem> roots,
        IReadOnlyDictionary<string, PackageInventoryItem> packages,
        IReadOnlyDictionary<string, string[]> adjacency,
        string framework)
    {
        var reachableRoots = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        var queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long associations = 0;
        long traversalOperations = 0;
        foreach (var root in roots)
        {
            var rootKey = NodeKey(root.Id, root.ResolvedVersion);
            if (!reachableRoots.TryGetValue(rootKey, out var nodeRoots))
            {
                nodeRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                reachableRoots[rootKey] = nodeRoots;
            }

            if (nodeRoots.Add(root.Id))
            {
                associations++;
                if (associations > MaximumRootReachabilityPerTarget)
                {
                    throw new InvalidDataException(
                        $"The dependency target '{framework}' exceeds the {MaximumRootReachabilityPerTarget}-association root reachability safety limit.");
                }

                if (queued.Add(rootKey))
                {
                    queue.Enqueue(rootKey);
                }
            }
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            queued.Remove(current);
            if (!adjacency.TryGetValue(current, out var children))
            {
                continue;
            }

            var currentRoots = reachableRoots[current];
            foreach (var child in children)
            {
                traversalOperations++;
                if (traversalOperations > MaximumTraversalOperationsPerTarget)
                {
                    throw new InvalidDataException(
                        $"The dependency target '{framework}' exceeds the {MaximumTraversalOperationsPerTarget}-operation graph traversal safety limit.");
                }

                if (!packages.ContainsKey(child))
                {
                    continue;
                }

                if (!reachableRoots.TryGetValue(child, out var childRoots))
                {
                    childRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    reachableRoots[child] = childRoots;
                }

                var changed = false;
                foreach (var root in currentRoots)
                {
                    if (!childRoots.Add(root))
                    {
                        continue;
                    }

                    changed = true;
                    associations++;
                    if (associations > MaximumRootReachabilityPerTarget)
                    {
                        throw new InvalidDataException(
                            $"The dependency target '{framework}' exceeds the {MaximumRootReachabilityPerTarget}-association root reachability safety limit.");
                    }
                }

                if (changed && queued.Add(child))
                {
                    queue.Enqueue(child);
                }
            }
        }

        return reachableRoots;
    }

    private static IReadOnlyList<string> ReconstructPath(
        string key,
        IReadOnlyDictionary<string, PathSelection> selections)
    {
        var reversed = new List<string>();
        string? current = key;
        while (current is not null)
        {
            reversed.Add(current);
            current = selections[current].ParentNodeKey;
        }

        reversed.Reverse();
        return reversed;
    }

    private static TargetIdentity TargetKey(PackageInventoryItem item) =>
        new(item.Project, item.Framework, item.RuntimeIdentifier);

    private static TargetIdentity TargetKey(ResolvedPackageDependencyEdge edge) =>
        new(edge.Project, edge.Framework, edge.RuntimeIdentifier);

    private static string NodeKey(string id, string version) => $"{id}/{version}";

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record PathSelection(
        string RootPackageId,
        string RootResolvedVersion,
        string? ParentNodeKey);

    private sealed record TargetIdentity(string Project, string Framework, string? RuntimeIdentifier);

    private sealed class TargetKeyComparer : IEqualityComparer<TargetIdentity>
    {
        public static TargetKeyComparer Instance { get; } = new();

        public bool Equals(TargetIdentity? left, TargetIdentity? right) =>
            ReferenceEquals(left, right) ||
            left is not null && right is not null &&
            PathComparer().Equals(left.Project, right.Project) &&
            StringComparer.OrdinalIgnoreCase.Equals(left.Framework, right.Framework) &&
            StringComparer.OrdinalIgnoreCase.Equals(left.RuntimeIdentifier, right.RuntimeIdentifier);

        public int GetHashCode(TargetIdentity value)
        {
            var hash = new HashCode();
            hash.Add(value.Project, PathComparer());
            hash.Add(value.Framework, StringComparer.OrdinalIgnoreCase);
            hash.Add(value.RuntimeIdentifier, StringComparer.OrdinalIgnoreCase);
            return hash.ToHashCode();
        }
    }
}
