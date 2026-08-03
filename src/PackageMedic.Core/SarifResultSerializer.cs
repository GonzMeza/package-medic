using System.Buffers;
using System.Text;
using System.Text.Json;

namespace PackageMedic.Core;

public static partial class SarifResultSerializer
{
    private const string SourceRootBaseId = "%SRCROOT%";
    private const string SchemaUri = "https://json.schemastore.org/sarif-2.1.0.json";
    private const string InformationUri = "https://github.com/GonzMeza/package-medic";

    public static string Serialize(AnalysisResult result, string repositoryRoot)
        => Serialize(result, repositoryRoot, null);

    public static string Serialize(
        AnalysisResult result,
        string repositoryRoot,
        BaselineComparison? baseline)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            Write(writer, result, repositoryRoot, baseline);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static async Task SerializeAsync(
        Stream destination,
        AnalysisResult result,
        string repositoryRoot,
        BaselineComparison? baseline = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var writer = new Utf8JsonWriter(destination, new JsonWriterOptions { Indented = true });
        Write(writer, result, repositoryRoot, baseline);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Write(
        Utf8JsonWriter writer,
        AnalysisResult result,
        string repositoryRoot,
        BaselineComparison? baseline)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var root = RepositoryRoot.Parse(repositoryRoot);
        var baselineStates = baseline?.Current
            .GroupBy(item => item.Fingerprint, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().State,
                StringComparer.OrdinalIgnoreCase);
        var rules = DiagnosticRuleCatalog.All.OrderBy(rule => rule.Code, StringComparer.Ordinal).ToArray();
        var ruleIndexes = rules
            .Select((rule, index) => (rule.Code, Index: index))
            .ToDictionary(item => item.Code, item => item.Index, StringComparer.Ordinal);
        var results = result.Diagnostics
            .Select(diagnostic => CreateSerializableDiagnostic(diagnostic, root, baselineStates))
            .OrderByDescending(item => item.Diagnostic.Severity)
            .ThenBy(item => item.Diagnostic.Code, StringComparer.Ordinal)
            .ThenBy(item => item.RelativePath, StringComparer.Ordinal)
            .ThenBy(item => item.Diagnostic.Line)
            .ThenBy(item => item.Diagnostic.Project, StringComparer.Ordinal)
            .ThenBy(item => item.Diagnostic.Evidence, StringComparer.Ordinal)
            .ToArray();

        writer.WriteStartObject();
        writer.WriteString("version", "2.1.0");
        writer.WriteString("$schema", SchemaUri);
        writer.WriteStartArray("runs");
        writer.WriteStartObject();
        WriteTool(writer, result.Version, rules);
        writer.WriteStartArray("results");
        foreach (var item in results)
        {
            WriteResult(writer, item, ruleIndexes);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static SerializableDiagnostic CreateSerializableDiagnostic(
        Diagnostic diagnostic,
        RepositoryRoot root,
        IReadOnlyDictionary<string, BaselineDiagnosticState>? baselineStates)
    {
        var identity = DiagnosticFingerprint.Create(diagnostic, root);
        var message = string.Join(
            "\n\n",
            DiagnosticFingerprint.SanitizeText(diagnostic.Title, root),
            DiagnosticFingerprint.SanitizeText(diagnostic.Explanation, root),
            $"Evidence: {DiagnosticFingerprint.SanitizeText(diagnostic.Evidence, root)}",
            $"Suggested action: {DiagnosticFingerprint.SanitizeText(diagnostic.SuggestedAction, root)}");

        BaselineDiagnosticState? baselineState = null;
        if (baselineStates?.TryGetValue(identity.Fingerprint, out var classifiedState) == true)
        {
            baselineState = classifiedState;
        }

        return new SerializableDiagnostic(
            diagnostic,
            identity.RelativePath,
            message,
            identity.Fingerprint,
            baselineState);
    }

    private static void WriteTool(
        Utf8JsonWriter writer,
        string version,
        IReadOnlyList<DiagnosticRuleMetadata> rules)
    {
        writer.WriteStartObject("tool");
        writer.WriteStartObject("driver");
        writer.WriteString("name", "PackageMedic");
        writer.WriteString("semanticVersion", version);
        writer.WriteString("informationUri", InformationUri);
        writer.WriteStartArray("rules");
        foreach (var rule in rules)
        {
            writer.WriteStartObject();
            writer.WriteString("id", rule.Code);
            writer.WriteString("name", rule.Name);
            WriteText(writer, "shortDescription", rule.ShortDescription);
            WriteText(writer, "fullDescription", rule.FullDescription);
            writer.WriteString("helpUri", rule.HelpUri);
            writer.WriteStartObject("defaultConfiguration");
            writer.WriteString("level", ToSarifLevel(rule.DefaultSeverity));
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteResult(
        Utf8JsonWriter writer,
        SerializableDiagnostic item,
        IReadOnlyDictionary<string, int> ruleIndexes)
    {
        var diagnostic = item.Diagnostic;
        if (!ruleIndexes.TryGetValue(diagnostic.Code, out var ruleIndex))
        {
            throw new InvalidOperationException($"Diagnostic '{diagnostic.Code}' has no registered rule metadata.");
        }

        writer.WriteStartObject();
        writer.WriteString("ruleId", diagnostic.Code);
        writer.WriteNumber("ruleIndex", ruleIndex);
        writer.WriteString("level", ToSarifLevel(diagnostic.Severity));
        if (item.BaselineState is { } baselineState)
        {
            writer.WriteString(
                "baselineState",
                baselineState == BaselineDiagnosticState.New ? "new" : "unchanged");
        }

        WriteText(writer, "message", item.Message);

        if (item.RelativePath is not null)
        {
            writer.WriteStartArray("locations");
            writer.WriteStartObject();
            writer.WriteStartObject("physicalLocation");
            writer.WriteStartObject("artifactLocation");
            writer.WriteString("uri", item.RelativePath);
            writer.WriteString("uriBaseId", SourceRootBaseId);
            writer.WriteEndObject();
            if (diagnostic.Line is > 0)
            {
                writer.WriteStartObject("region");
                writer.WriteNumber("startLine", diagnostic.Line.Value);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        writer.WriteStartObject("partialFingerprints");
        writer.WriteString("primaryLocationLineHash", item.Fingerprint);
        writer.WriteString(DiagnosticFingerprint.Algorithm, item.Fingerprint);
        writer.WriteEndObject();

        if (diagnostic.Confidence is not null || diagnostic.OriginalCode is not null)
        {
            writer.WriteStartObject("properties");
            if (diagnostic.Confidence is { } confidence)
            {
                writer.WriteString("confidence", confidence.ToString().ToLowerInvariant());
            }

            if (diagnostic.OriginalCode is { } originalCode)
            {
                writer.WriteString("originalCode", ProcessRunner.RedactSecrets(originalCode));
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteText(Utf8JsonWriter writer, string propertyName, string text)
    {
        writer.WriteStartObject(propertyName);
        writer.WriteString("text", text);
        writer.WriteEndObject();
    }

    private static string ToSarifLevel(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Information => "note",
        DiagnosticSeverity.Warning => "warning",
        DiagnosticSeverity.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unknown diagnostic severity."),
    };

    private sealed record SerializableDiagnostic(
        Diagnostic Diagnostic,
        string? RelativePath,
        string Message,
        string Fingerprint,
        BaselineDiagnosticState? BaselineState);

}
