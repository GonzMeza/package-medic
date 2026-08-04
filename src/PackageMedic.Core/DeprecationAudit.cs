using System.Text.Json;

namespace PackageMedic.Core;

public enum PackageDeprecationReason
{
    Unknown,
    Legacy,
    CriticalBugs,
    Other,
}

public sealed record DeprecatedPackage(
    string PackageId,
    string ResolvedVersion,
    IReadOnlyList<PackageDeprecationReason> Reasons,
    string? AlternativePackageId,
    string? AlternativeVersionRange,
    string Project,
    string Framework,
    PackageDependencyKind DependencyKind)
{
    public bool IsDirect => DependencyKind == PackageDependencyKind.Direct;

    public bool IsTransitive => DependencyKind == PackageDependencyKind.Transitive;
}

public sealed record DeprecationAuditResult(
    IReadOnlyList<DeprecatedPackage> DeprecatedPackages,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<string> Errors)
{
    public bool HasOperationalError => Errors.Count > 0;
}

public static class DeprecationAuditParser
{
    private const int MaximumDeprecatedPackages = 100_000;

    public static IReadOnlyList<DeprecatedPackage> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("dotnet list package returned an empty deprecation report.");
        }

        var redactedJson = ProcessRunner.RedactSecrets(json);
        var firstObject = redactedJson.IndexOf('{');
        var lastObject = redactedJson.LastIndexOf('}');
        if (firstObject < 0 || lastObject < firstObject)
        {
            throw new InvalidDataException("dotnet list package did not return a JSON object.");
        }

        using var document = JsonDocument.Parse(redactedJson.AsMemory(firstObject, lastObject - firstObject + 1));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The deprecation report root must be a JSON object.");
        }

        if (TryGetProperty(root, "version", out var version) && !IsVersionOne(version))
        {
            throw new InvalidDataException("Only dotnet list package JSON output version 1 is supported.");
        }

        if (!TryGetProperty(root, "projects", out var projects) || projects.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (projects.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The deprecation report 'projects' property must be an array.");
        }

        var deprecatedPackages = new List<DeprecatedPackage>();
        foreach (var project in projects.EnumerateArray())
        {
            if (project.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var projectPath = GetString(project, "path");
            if (string.IsNullOrWhiteSpace(projectPath) ||
                !TryGetProperty(project, "frameworks", out var frameworks) ||
                frameworks.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var framework in frameworks.EnumerateArray())
            {
                if (framework.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var frameworkName = GetString(framework, "framework") ?? "unknown";
                ParsePackages(
                    framework,
                    "topLevelPackages",
                    PackageDependencyKind.Direct,
                    projectPath,
                    frameworkName,
                    deprecatedPackages);
                ParsePackages(
                    framework,
                    "transitivePackages",
                    PackageDependencyKind.Transitive,
                    projectPath,
                    frameworkName,
                    deprecatedPackages);
            }
        }

        return deprecatedPackages
            .DistinctBy(DeprecationIdentity)
            .OrderByDescending(item => Severity(item))
            .ThenBy(item => item.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ResolvedVersion, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Framework, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.DependencyKind)
            .ToArray();
    }

    public static IReadOnlyList<Diagnostic> ToDiagnostics(
        IEnumerable<DeprecatedPackage> deprecatedPackages,
        IReadOnlyList<PackageInventoryItem>? inventory = null)
    {
        ArgumentNullException.ThrowIfNull(deprecatedPackages);
        inventory ??= [];

        return deprecatedPackages
            .Select(item => CreateDiagnostic(item, FindInventoryItem(item, inventory)))
            .DistinctBy(item => $"{item.Code}|{item.Project}|{item.File}|{item.Evidence}")
            .OrderByDescending(item => item.Severity)
            .ThenBy(item => item.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Evidence, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ParsePackages(
        JsonElement framework,
        string propertyName,
        PackageDependencyKind dependencyKind,
        string project,
        string frameworkName,
        ICollection<DeprecatedPackage> destination)
    {
        if (!TryGetProperty(framework, propertyName, out var packages) || packages.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var package in packages.EnumerateArray())
        {
            if (package.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var packageId = GetString(package, "id");
            if (string.IsNullOrWhiteSpace(packageId))
            {
                continue;
            }

            var reasons = ParseReasons(package);
            var markedDeprecated = TryGetProperty(package, "deprecated", out var deprecated) &&
                                   deprecated.ValueKind is JsonValueKind.True;
            if (reasons.Count == 0 && !markedDeprecated)
            {
                // The --deprecated report normally contains only deprecated packages. Requiring
                // explicit metadata prevents a malformed general package-list report from being
                // interpreted as deprecation evidence.
                continue;
            }

            var alternative = ParseAlternative(package);
            destination.Add(new DeprecatedPackage(
                packageId,
                GetString(package, "resolvedVersion") ?? GetString(package, "requestedVersion") ?? "unknown",
                reasons.Count == 0 ? [PackageDeprecationReason.Unknown] : reasons,
                alternative.Id,
                alternative.VersionRange,
                project,
                frameworkName,
                dependencyKind));
            if (destination.Count > MaximumDeprecatedPackages)
            {
                throw new InvalidDataException(
                    $"The deprecation report cannot contain more than {MaximumDeprecatedPackages} packages.");
            }
        }
    }

    private static IReadOnlyList<PackageDeprecationReason> ParseReasons(JsonElement package)
    {
        if (!TryGetProperty(package, "deprecationReasons", out var reasons) || reasons.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return reasons.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => ParseReason(item.GetString()))
            .Distinct()
            .OrderBy(item => item)
            .ToArray();
    }

    private static PackageDeprecationReason ParseReason(string? value) => value?.Trim()
        .Replace("_", string.Empty, StringComparison.Ordinal)
        .Replace("-", string.Empty, StringComparison.Ordinal)
        .Replace(" ", string.Empty, StringComparison.Ordinal)
        .ToUpperInvariant() switch
    {
        "LEGACY" => PackageDeprecationReason.Legacy,
        "CRITICALBUGS" => PackageDeprecationReason.CriticalBugs,
        "OTHER" => PackageDeprecationReason.Other,
        _ => PackageDeprecationReason.Unknown,
    };

    private static (string? Id, string? VersionRange) ParseAlternative(JsonElement package)
    {
        if (!TryGetProperty(package, "alternativePackage", out var alternative) ||
            alternative.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        return (
            GetString(alternative, "id"),
            GetString(alternative, "versionRange") ?? GetString(alternative, "version"));
    }

    private static Diagnostic CreateDiagnostic(
        DeprecatedPackage package,
        PackageInventoryItem? inventory)
    {
        var dependency = package.IsTransitive ? "transitive" : "direct";
        var reasons = string.Join(
            ", ",
            package.Reasons.Select(ReasonDisplayName).Order(StringComparer.OrdinalIgnoreCase));
        var alternative = string.IsNullOrWhiteSpace(package.AlternativePackageId)
            ? null
            : string.IsNullOrWhiteSpace(package.AlternativeVersionRange)
                ? package.AlternativePackageId
                : $"{package.AlternativePackageId} {package.AlternativeVersionRange}";
        var alternativeSentence = alternative is null
            ? "No replacement package was provided by the source."
            : $"The source recommends '{alternative}'.";

        return new Diagnostic(
            "PM008",
            Severity(package),
            "Package is deprecated",
            $"'{package.PackageId}' {package.ResolvedVersion} is deprecated ({reasons.ToLowerInvariant()}). {alternativeSentence}",
            package.Project,
            inventory?.SourceFile ?? package.Project,
            inventory?.SourceLine,
            $"{package.PackageId} {package.ResolvedVersion}; {dependency}; framework {package.Framework}.",
            alternative is null
                ? "Review why the package was deprecated and replace or remove it when a compatible path is available."
                : $"Review compatibility and migrate to '{alternative}'.",
            DiagnosticConfidence.High,
            PackageId: package.PackageId);
    }

    private static PackageInventoryItem? FindInventoryItem(
        DeprecatedPackage package,
        IReadOnlyList<PackageInventoryItem> inventory) => inventory
        .Where(item => item.DependencyKind == package.DependencyKind &&
                       item.Id.Equals(package.PackageId, StringComparison.OrdinalIgnoreCase) &&
                       item.ResolvedVersion.Equals(package.ResolvedVersion, StringComparison.OrdinalIgnoreCase) &&
                       item.Framework.Equals(package.Framework, StringComparison.OrdinalIgnoreCase) &&
                       PathsEqual(item.Project, package.Project))
        .OrderBy(item => item.RuntimeIdentifier, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault();

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return Path.GetFullPath(left).Equals(
                Path.GetFullPath(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return left.Equals(right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static DiagnosticSeverity Severity(DeprecatedPackage package) =>
        package.Reasons.Contains(PackageDeprecationReason.CriticalBugs)
            ? DiagnosticSeverity.Error
            : DiagnosticSeverity.Warning;

    private static string ReasonDisplayName(PackageDeprecationReason reason) => reason switch
    {
        PackageDeprecationReason.CriticalBugs => "critical bugs",
        PackageDeprecationReason.Legacy => "legacy",
        PackageDeprecationReason.Other => "other",
        _ => "unknown reason",
    };

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool IsVersionOne(JsonElement version) =>
        version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var numeric) && numeric == 1 ||
        version.ValueKind == JsonValueKind.String && version.GetString()?.Trim() == "1";

    private static string DeprecationIdentity(DeprecatedPackage package) => string.Join(
        '|',
        package.PackageId.ToUpperInvariant(),
        package.ResolvedVersion.ToUpperInvariant(),
        string.Join(',', package.Reasons.OrderBy(item => item)),
        package.AlternativePackageId?.ToUpperInvariant(),
        package.AlternativeVersionRange?.ToUpperInvariant(),
        package.Project.ToUpperInvariant(),
        package.Framework.ToUpperInvariant(),
        package.DependencyKind);
}

public sealed class DeprecationAuditRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    private readonly IProcessRunner processRunner;
    private readonly TimeSpan timeout;
    private readonly int maxDegreeOfParallelism;

    public DeprecationAuditRunner(IProcessRunner processRunner)
        : this(processRunner, DefaultTimeout, AnalysisExecutionOptions.Default.MaxDegreeOfParallelism)
    {
    }

    public DeprecationAuditRunner(
        IProcessRunner processRunner,
        TimeSpan timeout,
        int maxDegreeOfParallelism)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The deprecation timeout must be greater than zero and no longer than one hour.");
        }

        if (maxDegreeOfParallelism is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism));
        }

        this.timeout = timeout;
        this.maxDegreeOfParallelism = maxDegreeOfParallelism;
    }

    public async Task<DeprecationAuditResult> AuditAsync(
        DiscoveryResult discovery,
        bool includeTransitive,
        IReadOnlyList<PackageInventoryItem>? inventory = null,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(discovery);

        var progressGate = new object();
        var targets = discovery.RestoreTargets
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var outcomes = new DeprecationTargetOutcome?[targets.Length];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, targets.Length),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = maxDegreeOfParallelism,
            },
            async (index, token) =>
            {
                outcomes[index] = await AuditTargetAsync(
                    targets[index],
                    includeTransitive,
                    progress,
                    progressGate,
                    token).ConfigureAwait(false);
            }).ConfigureAwait(false);

        var deprecatedPackages = outcomes.SelectMany(item => item!.DeprecatedPackages)
            .DistinctBy(item => string.Join(
                '|',
                item.PackageId.ToUpperInvariant(),
                item.ResolvedVersion.ToUpperInvariant(),
                string.Join(',', item.Reasons.OrderBy(reason => reason)),
                item.AlternativePackageId?.ToUpperInvariant(),
                item.AlternativeVersionRange?.ToUpperInvariant(),
                item.Project.ToUpperInvariant(),
                item.Framework.ToUpperInvariant(),
                item.DependencyKind))
            .OrderByDescending(item => item.Reasons.Contains(PackageDeprecationReason.CriticalBugs))
            .ThenBy(item => item.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ResolvedVersion, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Framework, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.DependencyKind)
            .ToArray();
        var errors = outcomes.SelectMany(item => item!.Errors)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new DeprecationAuditResult(
            deprecatedPackages,
            DeprecationAuditParser.ToDiagnostics(deprecatedPackages, inventory),
            errors);
    }

    private async Task<DeprecationTargetOutcome> AuditTargetAsync(
        string target,
        bool includeTransitive,
        Action<string>? progress,
        object progressGate,
        CancellationToken cancellationToken)
    {
        lock (progressGate)
        {
            progress?.Invoke($"Checking {Path.GetFileName(target)} for deprecated packages using configured NuGet sources...");
        }

        var arguments = new List<string>
        {
            "list",
            target,
            "package",
            "--deprecated",
            "--format",
            "json",
            "--output-version",
            "1",
        };
        if (includeTransitive)
        {
            arguments.Add("--include-transitive");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        ProcessResult processResult;
        try
        {
            processResult = await processRunner.RunAsync(
                "dotnet",
                arguments,
                Path.GetDirectoryName(target)!,
                timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DeprecationTargetOutcome(
                [],
                [$"dotnet list package deprecation query timed out for '{target}' after {timeout.TotalSeconds:0} seconds."]);
        }

        if (processResult.ExitCode != 0)
        {
            var detail = CompactError(processResult);
            var error = $"dotnet list package deprecation query failed for '{target}' with exit code {processResult.ExitCode}." +
                        (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" {detail}");
            return new DeprecationTargetOutcome([], [error]);
        }

        if (processResult.StandardOutputTruncated)
        {
            return new DeprecationTargetOutcome(
                [],
                [$"dotnet list package deprecation output exceeded the {ProcessRunner.DefaultMaximumOutputCharacters}-character safety limit for '{target}'."]);
        }

        try
        {
            return new DeprecationTargetOutcome(DeprecationAuditParser.Parse(processResult.StandardOutput), []);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return new DeprecationTargetOutcome(
                [],
                [$"Could not read the package deprecation report for '{target}': {exception.Message}"]);
        }
    }

    private static string CompactError(ProcessResult result)
    {
        var value = ProcessRunner.RedactSecrets(
            string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError);
        var compact = string.Join(
            ' ',
            value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return compact.Length <= 500 ? compact : string.Concat(compact.AsSpan(0, 500), "...");
    }

    private sealed record DeprecationTargetOutcome(
        IReadOnlyList<DeprecatedPackage> DeprecatedPackages,
        IReadOnlyList<string> Errors);
}
