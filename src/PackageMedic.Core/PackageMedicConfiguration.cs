using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PackageMedic.Core;

public enum PolicyFailureLevel
{
    None,
    Warning,
    Error,
}

public sealed record PolicyRule(bool Enabled = true, DiagnosticSeverity? Severity = null);

public sealed record PolicySuppression(
    string Rule,
    string Reason,
    string? Path = null,
    string? Package = null);

public sealed record ConfiguredPolicyTimeouts(
    int? RestoreSeconds = null,
    int? EvaluationSeconds = null);

public sealed record PackageMedicConfiguration(
    int SchemaVersion,
    PolicyFailureLevel? FailOn,
    PolicyFailureLevel? FailOnNew,
    string? Baseline,
    IReadOnlyList<string> Exclude,
    IReadOnlyDictionary<string, PolicyRule> Rules,
    IReadOnlyList<PolicySuppression> Suppressions,
    ConfiguredPolicyTimeouts Timeouts,
    int? MaxParallelism)
{
    public const int CurrentSchemaVersion = 1;

    public static PackageMedicConfiguration Default { get; } = new(
        CurrentSchemaVersion,
        null,
        null,
        null,
        [],
        new Dictionary<string, PolicyRule>(StringComparer.Ordinal),
        [],
        new ConfiguredPolicyTimeouts(),
        null);
}

public sealed class PackageMedicConfigurationException : Exception
{
    public PackageMedicConfigurationException(string message)
        : base(message)
    {
    }

    public PackageMedicConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class PackageMedicConfigurationLoader
{
    internal const int MaximumConfigurationCharacters = 1024 * 1024;
    internal const int MaximumExcludePatterns = 1000;
    internal const int MaximumSuppressions = 1000;
    internal const int MaximumPatternCharacters = 4096;
    private const int MinimumTimeoutSeconds = 1;
    private const int MaximumTimeoutSeconds = 3600;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new JsonStringEnumConverter<PolicyFailureLevel>(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
            new JsonStringEnumConverter<DiagnosticSeverity>(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
        },
    };

    public static PackageMedicConfiguration Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string json;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length > MaximumConfigurationCharacters)
            {
                throw new PackageMedicConfigurationException(
                    $"Invalid PackageMedic configuration '{path}': the file exceeds the {MaximumConfigurationCharacters}-byte safety limit.");
            }

            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 16 * 1024,
                leaveOpen: false);
            json = reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PackageMedicConfigurationException(
                $"Could not read PackageMedic configuration '{path}': {exception.Message}",
                exception);
        }

        return Parse(json, path);
    }

    public static PackageMedicConfiguration Parse(string json, string sourceName = ".packagemedic.json")
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        if (json.Length > MaximumConfigurationCharacters)
        {
            throw Invalid(
                sourceName,
                $"the document exceeds the {MaximumConfigurationCharacters}-character safety limit.");
        }

        RawConfiguration? raw;
        try
        {
            using var document = JsonDocument.Parse(json);
            ValidateNoDuplicateProperties(document.RootElement, "$", sourceName);
            raw = JsonSerializer.Deserialize<RawConfiguration>(document.RootElement.GetRawText(), SerializerOptions);
        }
        catch (PackageMedicConfigurationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            var location = exception.LineNumber is null
                ? string.Empty
                : $" at line {exception.LineNumber + 1}, byte {exception.BytePositionInLine + 1}";
            throw new PackageMedicConfigurationException(
                $"Invalid PackageMedic configuration '{sourceName}'{location}: {exception.Message}",
                exception);
        }

        if (raw is null)
        {
            throw new PackageMedicConfigurationException(
                $"Invalid PackageMedic configuration '{sourceName}': the root must be a JSON object.");
        }

        return ValidateAndNormalize(raw, sourceName);
    }

    private static PackageMedicConfiguration ValidateAndNormalize(RawConfiguration raw, string sourceName)
    {
        if (raw.SchemaVersion != PackageMedicConfiguration.CurrentSchemaVersion)
        {
            throw Invalid(
                sourceName,
                $"schemaVersion must be {PackageMedicConfiguration.CurrentSchemaVersion}; received {raw.SchemaVersion}.");
        }

        var baseline = NormalizeOptionalPattern(raw.Baseline, "baseline", sourceName);
        var exclude = NormalizePatterns(raw.Exclude, "exclude", sourceName);
        var rules = NormalizeRules(raw.Rules, sourceName);
        var suppressions = NormalizeSuppressions(raw.Suppressions, sourceName);
        var timeouts = NormalizeTimeouts(raw.Timeouts, sourceName);
        ValidateParallelism(raw.MaxParallelism, sourceName);

        return new PackageMedicConfiguration(
            raw.SchemaVersion,
            raw.FailOn,
            raw.FailOnNew,
            baseline,
            exclude,
            rules,
            suppressions,
            timeouts,
            raw.MaxParallelism);
    }

    private static IReadOnlyDictionary<string, PolicyRule> NormalizeRules(
        Dictionary<string, RawRule?>? configuredRules,
        string sourceName)
    {
        if (configuredRules is null)
        {
            return new Dictionary<string, PolicyRule>(StringComparer.Ordinal);
        }

        var rules = new SortedDictionary<string, PolicyRule>(StringComparer.Ordinal);
        foreach (var pair in configuredRules)
        {
            var code = pair.Key.Trim().ToUpperInvariant();
            if (code.Length == 0)
            {
                throw Invalid(sourceName, "rules contains an empty diagnostic code.");
            }

            if (!DiagnosticRuleCatalog.TryGet(code, out _))
            {
                throw Invalid(sourceName, $"rules contains unknown diagnostic code '{pair.Key}'.");
            }

            if (pair.Value is null)
            {
                throw Invalid(sourceName, $"rules.{code} must be a JSON object.");
            }

            if (!rules.TryAdd(code, new PolicyRule(pair.Value.Enabled ?? true, pair.Value.Severity)))
            {
                throw Invalid(sourceName, $"rules contains diagnostic code '{code}' more than once.");
            }
        }

        return rules;
    }

    private static IReadOnlyList<PolicySuppression> NormalizeSuppressions(
        IReadOnlyList<RawSuppression?>? configuredSuppressions,
        string sourceName)
    {
        if (configuredSuppressions is null)
        {
            return [];
        }

        if (configuredSuppressions.Count > MaximumSuppressions)
        {
            throw Invalid(
                sourceName,
                $"suppressions cannot contain more than {MaximumSuppressions} entries.");
        }

        var suppressions = new List<PolicySuppression>(configuredSuppressions.Count);
        for (var index = 0; index < configuredSuppressions.Count; index++)
        {
            var configured = configuredSuppressions[index];
            if (configured is null)
            {
                throw Invalid(sourceName, $"suppressions[{index}] must be a JSON object.");
            }

            var rule = RequireString(configured.Rule, $"suppressions[{index}].rule", sourceName).ToUpperInvariant();
            if (!DiagnosticRuleCatalog.TryGet(rule, out _))
            {
                throw Invalid(sourceName, $"suppressions[{index}].rule contains unknown diagnostic code '{rule}'.");
            }

            var reason = RequireString(configured.Reason, $"suppressions[{index}].reason", sourceName);
            var path = NormalizeOptionalPattern(configured.Path, $"suppressions[{index}].path", sourceName);
            var package = NormalizeOptionalString(configured.Package, $"suppressions[{index}].package", sourceName);

            suppressions.Add(new PolicySuppression(rule, reason, path, package));
        }

        return suppressions
            .Distinct()
            .OrderBy(item => item.Rule, StringComparer.Ordinal)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Package, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Reason, StringComparer.Ordinal)
            .ToArray();
    }

    private static ConfiguredPolicyTimeouts NormalizeTimeouts(RawTimeouts? configured, string sourceName)
    {
        if (configured is null)
        {
            return new ConfiguredPolicyTimeouts();
        }

        ValidateTimeout(configured.RestoreSeconds, "timeouts.restoreSeconds", sourceName);
        ValidateTimeout(configured.EvaluationSeconds, "timeouts.evaluationSeconds", sourceName);
        return new ConfiguredPolicyTimeouts(configured.RestoreSeconds, configured.EvaluationSeconds);
    }

    private static void ValidateTimeout(int? seconds, string property, string sourceName)
    {
        if (seconds is < MinimumTimeoutSeconds or > MaximumTimeoutSeconds)
        {
            throw Invalid(
                sourceName,
                $"{property} must be between {MinimumTimeoutSeconds} and {MaximumTimeoutSeconds} seconds.");
        }
    }

    private static void ValidateParallelism(int? value, string sourceName)
    {
        if (value is < 1 or > 32)
        {
            throw Invalid(sourceName, "maxParallelism must be between 1 and 32.");
        }
    }

    private static IReadOnlyList<string> NormalizePatterns(
        IReadOnlyList<string?>? patterns,
        string property,
        string sourceName)
    {
        if (patterns is null)
        {
            return [];
        }

        if (patterns.Count > MaximumExcludePatterns)
        {
            throw Invalid(
                sourceName,
                $"{property} cannot contain more than {MaximumExcludePatterns} entries.");
        }

        var normalized = new List<string>(patterns.Count);
        for (var index = 0; index < patterns.Count; index++)
        {
            normalized.Add(NormalizePattern(patterns[index], $"{property}[{index}]", sourceName));
        }

        return normalized
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? NormalizeOptionalPattern(string? value, string property, string sourceName) =>
        value is null ? null : NormalizePattern(value, property, sourceName);

    private static string NormalizePattern(string? value, string property, string sourceName)
    {
        var normalized = RequireString(value, property, sourceName).Replace('\\', '/');
        if (normalized.Length > MaximumPatternCharacters)
        {
            throw Invalid(
                sourceName,
                $"{property} cannot exceed {MaximumPatternCharacters} characters.");
        }
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        if (normalized.IndexOf('\0') >= 0)
        {
            throw Invalid(sourceName, $"{property} contains an invalid null character.");
        }

        if (normalized.StartsWith("/", StringComparison.Ordinal) ||
            (normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':') ||
            normalized.Split('/').Contains("..", StringComparer.Ordinal))
        {
            throw Invalid(sourceName, $"{property} must be a repository-relative portable pattern.");
        }

        return normalized;
    }

    private static string? NormalizeOptionalString(string? value, string property, string sourceName)
    {
        if (value is null)
        {
            return null;
        }

        return RequireString(value, property, sourceName);
    }

    private static string RequireString(string? value, string property, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid(sourceName, $"{property} must be a non-empty string.");
        }

        return value.Trim();
    }

    private static void ValidateNoDuplicateProperties(JsonElement element, string path, string sourceName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var properties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!properties.Add(property.Name))
                {
                    throw Invalid(sourceName, $"duplicate property '{property.Name}' at {path}.");
                }

                ValidateNoDuplicateProperties(property.Value, $"{path}.{property.Name}", sourceName);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                ValidateNoDuplicateProperties(item, $"{path}[{index}]", sourceName);
                index++;
            }
        }
    }

    private static PackageMedicConfigurationException Invalid(string sourceName, string detail) =>
        new($"Invalid PackageMedic configuration '{sourceName}': {detail}");

    private sealed class RawConfiguration
    {
        [JsonPropertyName("$schema")]
        public string? Schema { get; init; }

        public int SchemaVersion { get; init; }

        public PolicyFailureLevel? FailOn { get; init; }

        public PolicyFailureLevel? FailOnNew { get; init; }

        public string? Baseline { get; init; }

        public IReadOnlyList<string?>? Exclude { get; init; }

        public Dictionary<string, RawRule?>? Rules { get; init; }

        public IReadOnlyList<RawSuppression?>? Suppressions { get; init; }

        public RawTimeouts? Timeouts { get; init; }

        public int? MaxParallelism { get; init; }
    }

    private sealed class RawRule
    {
        public bool? Enabled { get; init; }

        public DiagnosticSeverity? Severity { get; init; }
    }

    private sealed class RawSuppression
    {
        public string? Rule { get; init; }

        public string? Reason { get; init; }

        public string? Path { get; init; }

        public string? Package { get; init; }
    }

    private sealed class RawTimeouts
    {
        public int? RestoreSeconds { get; init; }

        public int? EvaluationSeconds { get; init; }
    }
}
