using System.Text;
using System.Text.Json;

namespace PackageMedic.Core;

public sealed record PackageMedicBaseline(
    int SchemaVersion,
    string ToolVersion,
    IReadOnlyList<BaselineEntry> Entries)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record BaselineEntry(
    string Fingerprint,
    string RuleId,
    DiagnosticSeverity Severity,
    string Title,
    string? File,
    string? Project);

public enum BaselineDiagnosticState
{
    New,
    Existing,
}

public sealed record BaselineDiagnostic(
    Diagnostic Diagnostic,
    string Fingerprint,
    string? RelativePath,
    BaselineDiagnosticState State);

public sealed record BaselineComparison(
    IReadOnlyList<BaselineDiagnostic> Current,
    IReadOnlyList<BaselineEntry> Resolved)
{
    public int NewCount => Current.Count(item => item.State == BaselineDiagnosticState.New);

    public int ExistingCount => Current.Count(item => item.State == BaselineDiagnosticState.Existing);

    public int ResolvedCount => Resolved.Count;
}

/// <summary>
/// Creates and reads the deterministic PackageMedic baseline schema.
/// </summary>
public static class BaselineSerializer
{
    public static PackageMedicBaseline Create(AnalysisResult result, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(result);
        var root = RepositoryRoot.Parse(repositoryRoot);

        var entries = result.Diagnostics
            .Select(diagnostic => CreateEntry(diagnostic, root))
            .OrderBy(entry => entry.Fingerprint, StringComparer.Ordinal)
            .ThenBy(entry => entry.RuleId, StringComparer.Ordinal)
            .ThenBy(entry => entry.File, StringComparer.Ordinal)
            .ThenBy(entry => entry.Project, StringComparer.Ordinal)
            .ThenBy(entry => entry.Title, StringComparer.Ordinal)
            .GroupBy(entry => entry.Fingerprint, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        return new PackageMedicBaseline(
            PackageMedicBaseline.CurrentSchemaVersion,
            result.Version,
            entries);
    }

    public static PackageMedicBaseline Update(
        PackageMedicBaseline previous,
        AnalysisResult current,
        string repositoryRoot)
    {
        Validate(previous);
        return Create(current, repositoryRoot);
    }

    public static string Serialize(PackageMedicBaseline baseline)
    {
        Validate(baseline);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", baseline.SchemaVersion);
            writer.WriteString("toolVersion", baseline.ToolVersion);
            writer.WriteStartArray("entries");
            foreach (var entry in CanonicalizeEntries(baseline.Entries))
            {
                writer.WriteStartObject();
                writer.WriteString("fingerprint", entry.Fingerprint);
                writer.WriteString("ruleId", entry.RuleId);
                writer.WriteString("severity", ToJsonSeverity(entry.Severity));
                writer.WriteString("title", entry.Title);
                if (entry.File is not null)
                {
                    writer.WriteString("file", entry.File);
                }

                if (entry.Project is not null)
                {
                    writer.WriteString("project", entry.Project);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static PackageMedicBaseline Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("The baseline root must be a JSON object.");
            }

            var schemaVersion = RequiredInt32(root, "schemaVersion");
            if (schemaVersion != PackageMedicBaseline.CurrentSchemaVersion)
            {
                throw Invalid(
                    $"Unsupported baseline schemaVersion '{schemaVersion}'. Expected '{PackageMedicBaseline.CurrentSchemaVersion}'.");
            }

            var toolVersion = RequiredString(root, "toolVersion");
            if (!root.TryGetProperty("entries", out var entriesElement) ||
                entriesElement.ValueKind != JsonValueKind.Array)
            {
                throw Invalid("Baseline property 'entries' must be an array.");
            }

            var entries = new List<BaselineEntry>();
            foreach (var element in entriesElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    throw Invalid("Every baseline entry must be a JSON object.");
                }

                entries.Add(new BaselineEntry(
                    RequiredString(element, "fingerprint").ToLowerInvariant(),
                    RequiredString(element, "ruleId"),
                    ParseSeverity(RequiredString(element, "severity")),
                    RequiredString(element, "title"),
                    OptionalString(element, "file"),
                    OptionalString(element, "project")));
            }

            var baseline = new PackageMedicBaseline(schemaVersion, toolVersion, entries);
            Validate(baseline);
            return baseline with { Entries = CanonicalizeEntries(entries) };
        }
        catch (JsonException exception)
        {
            throw Invalid("The baseline is not valid JSON.", exception);
        }
    }

    public static PackageMedicBaseline Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            return Deserialize(File.ReadAllText(path));
        }
        catch (IOException exception)
        {
            throw Invalid($"Could not read baseline '{path}'.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw Invalid($"Could not read baseline '{path}'.", exception);
        }
    }

    private static BaselineEntry CreateEntry(Diagnostic diagnostic, RepositoryRoot root)
    {
        var identity = DiagnosticFingerprint.Create(diagnostic, root);
        return new BaselineEntry(
            identity.Fingerprint,
            diagnostic.Code,
            diagnostic.Severity,
            DiagnosticFingerprint.SanitizeText(diagnostic.Title, root),
            identity.RelativePath,
            string.IsNullOrWhiteSpace(diagnostic.Project)
                ? null
                : DiagnosticFingerprint.SanitizeText(diagnostic.Project, root));
    }

    private static IReadOnlyList<BaselineEntry> CanonicalizeEntries(IEnumerable<BaselineEntry> entries) => entries
        .OrderBy(entry => entry.Fingerprint, StringComparer.Ordinal)
        .ThenBy(entry => entry.RuleId, StringComparer.Ordinal)
        .ThenBy(entry => entry.File, StringComparer.Ordinal)
        .ThenBy(entry => entry.Project, StringComparer.Ordinal)
        .ThenBy(entry => entry.Title, StringComparer.Ordinal)
        .ToArray();

    private static void Validate(PackageMedicBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (baseline.SchemaVersion != PackageMedicBaseline.CurrentSchemaVersion)
        {
            throw Invalid(
                $"Unsupported baseline schemaVersion '{baseline.SchemaVersion}'. Expected '{PackageMedicBaseline.CurrentSchemaVersion}'.");
        }

        if (string.IsNullOrWhiteSpace(baseline.ToolVersion))
        {
            throw Invalid("Baseline property 'toolVersion' is required.");
        }

        if (baseline.Entries is null)
        {
            throw Invalid("Baseline property 'entries' is required.");
        }

        var fingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in baseline.Entries)
        {
            if (entry is null)
            {
                throw Invalid("Baseline entries cannot be null.");
            }

            if (!IsFingerprint(entry.Fingerprint))
            {
                throw Invalid("Every baseline fingerprint must contain exactly 64 hexadecimal characters.");
            }

            if (!fingerprints.Add(entry.Fingerprint))
            {
                throw Invalid($"Baseline fingerprint '{entry.Fingerprint}' is duplicated.");
            }

            if (string.IsNullOrWhiteSpace(entry.RuleId))
            {
                throw Invalid("Every baseline entry requires a ruleId.");
            }

            if (string.IsNullOrWhiteSpace(entry.Title))
            {
                throw Invalid("Every baseline entry requires a title.");
            }

            if (entry.File is not null && !IsSafeRelativePath(entry.File))
            {
                throw Invalid($"Baseline file '{entry.File}' must be a repository-relative portable path.");
            }
        }
    }

    private static bool IsFingerprint(string value) =>
        value is { Length: 64 } && value.All(character => char.IsAsciiHexDigit(character));

    private static bool IsSafeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.StartsWith("/", StringComparison.Ordinal) ||
            value.StartsWith('\\') ||
            value.Contains('\\'))
        {
            return false;
        }

        var decoded = Uri.UnescapeDataString(value);
        if (decoded.Length >= 2 && char.IsAsciiLetter(decoded[0]) && decoded[1] == ':')
        {
            return false;
        }

        return decoded
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .All(segment => segment is not "." and not "..");
    }

    private static int RequiredInt32(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out var value))
        {
            throw Invalid($"Baseline property '{name}' must be an integer.");
        }

        return value;
    }

    private static string RequiredString(JsonElement element, string name)
    {
        var value = OptionalString(element, name);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw Invalid($"Baseline property '{name}' must be a non-empty string.");
    }

    private static string? OptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw Invalid($"Baseline property '{name}' must be a string.");
        }

        return property.GetString();
    }

    private static DiagnosticSeverity ParseSeverity(string value) => value.ToLowerInvariant() switch
    {
        "information" => DiagnosticSeverity.Information,
        "warning" => DiagnosticSeverity.Warning,
        "error" => DiagnosticSeverity.Error,
        _ => throw Invalid($"Unknown baseline severity '{value}'."),
    };

    private static string ToJsonSeverity(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Information => "information",
        DiagnosticSeverity.Warning => "warning",
        DiagnosticSeverity.Error => "error",
        _ => throw Invalid($"Unknown diagnostic severity '{severity}'."),
    };

    private static InvalidDataException Invalid(string message, Exception? innerException = null) =>
        new(message, innerException);
}

public static class BaselineMatcher
{
    public static BaselineComparison Compare(
        AnalysisResult current,
        PackageMedicBaseline baseline,
        string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(current);
        // Canonical serialization performs the complete schema and entry validation once.
        _ = BaselineSerializer.Serialize(baseline);
        var root = RepositoryRoot.Parse(repositoryRoot);
        var baselineFingerprints = baseline.Entries
            .ToDictionary(entry => entry.Fingerprint, StringComparer.OrdinalIgnoreCase);
        var currentFingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var classified = current.Diagnostics
            .Select(diagnostic =>
            {
                var identity = DiagnosticFingerprint.Create(diagnostic, root);
                currentFingerprints.Add(identity.Fingerprint);
                return new BaselineDiagnostic(
                    diagnostic,
                    identity.Fingerprint,
                    identity.RelativePath,
                    baselineFingerprints.ContainsKey(identity.Fingerprint)
                        ? BaselineDiagnosticState.Existing
                        : BaselineDiagnosticState.New);
            })
            .ToArray();

        var resolved = baseline.Entries
            .Where(entry => !currentFingerprints.Contains(entry.Fingerprint))
            .OrderBy(entry => entry.Fingerprint, StringComparer.Ordinal)
            .ToArray();

        return new BaselineComparison(classified, resolved);
    }
}
