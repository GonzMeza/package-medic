using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PackageMedic.Core;

public static partial class SarifResultSerializer
{
    private const string SourceRootBaseId = "%SRCROOT%";
    private const string SchemaUri = "https://json.schemastore.org/sarif-2.1.0.json";
    private const string InformationUri = "https://github.com/GonzMeza/package-medic";

    public static string Serialize(AnalysisResult result, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var root = PortablePath.ParseRoot(repositoryRoot);
        var rules = DiagnosticRuleCatalog.All.OrderBy(rule => rule.Code, StringComparer.Ordinal).ToArray();
        var ruleIndexes = rules
            .Select((rule, index) => (rule.Code, Index: index))
            .ToDictionary(item => item.Code, item => item.Index, StringComparer.Ordinal);
        var results = result.Diagnostics
            .Select(diagnostic => CreateSerializableDiagnostic(diagnostic, root))
            .OrderByDescending(item => item.Diagnostic.Severity)
            .ThenBy(item => item.Diagnostic.Code, StringComparer.Ordinal)
            .ThenBy(item => item.RelativePath, StringComparer.Ordinal)
            .ThenBy(item => item.Diagnostic.Line)
            .ThenBy(item => item.Diagnostic.Project, StringComparer.Ordinal)
            .ThenBy(item => item.Diagnostic.Evidence, StringComparer.Ordinal)
            .ToArray();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
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

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static SerializableDiagnostic CreateSerializableDiagnostic(Diagnostic diagnostic, PortablePath root)
    {
        var relativePath = PortablePath.TryGetRelativeUri(diagnostic.File, root);
        var message = string.Join(
            "\n\n",
            SanitizeText(diagnostic.Title, root),
            SanitizeText(diagnostic.Explanation, root),
            $"Evidence: {SanitizeText(diagnostic.Evidence, root)}",
            $"Suggested action: {SanitizeText(diagnostic.SuggestedAction, root)}");
        var fingerprintInput = string.Join(
            "\n",
            diagnostic.Code,
            diagnostic.OriginalCode ?? string.Empty,
            relativePath ?? string.Empty,
            SanitizeText(diagnostic.Evidence, root));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput))).ToLowerInvariant();

        return new SerializableDiagnostic(diagnostic, relativePath, message, fingerprint);
    }

    private static string SanitizeText(string value, PortablePath root)
    {
        var redacted = ProcessRunner.RedactSecrets(value);
        if (root.Normalized != "/")
        {
            redacted = redacted.Replace(root.Original, SourceRootBaseId, root.Comparison);
            if (!root.Original.Equals(root.Normalized, StringComparison.Ordinal))
            {
                redacted = redacted.Replace(root.Normalized, SourceRootBaseId, root.Comparison);
            }
        }

        redacted = redacted.Replace('\\', '/');
        redacted = WindowsAbsolutePathRegex().Replace(redacted, "[ABSOLUTE_PATH]");
        return UnixAbsolutePathRegex().Replace(redacted, "[ABSOLUTE_PATH]");
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
        writer.WriteString("packageMedicDiagnostic/v1", item.Fingerprint);
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
        string Fingerprint);

    private sealed record PortablePath(string Original, string Normalized, bool IsWindows, StringComparison Comparison)
    {
        public static PortablePath ParseRoot(string value)
        {
            var original = value.Trim();
            var normalized = NormalizeSeparators(original);
            var isWindows = IsWindowsAbsolute(normalized);
            if (!isWindows && !IsUnixAbsolute(normalized))
            {
                throw new ArgumentException("The repository root must be an absolute Windows or Unix path.", nameof(value));
            }

            var isFileSystemRoot = normalized == "/" ||
                                   (isWindows && normalized.Length == 3 && normalized[1] == ':' && normalized[2] == '/');
            if (!isFileSystemRoot)
            {
                original = original.TrimEnd('/', '\\');
                normalized = normalized.TrimEnd('/');
            }

            return new PortablePath(
                original,
                normalized,
                isWindows,
                isWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        public static string? TryGetRelativeUri(string? file, PortablePath root)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                return null;
            }

            var normalizedFile = NormalizeSeparators(file.Trim());
            string relative;
            if (IsWindowsAbsolute(normalizedFile) || IsUnixAbsolute(normalizedFile))
            {
                var fileIsWindows = IsWindowsAbsolute(normalizedFile);
                var rootEndsWithSeparator = root.Normalized.EndsWith("/", StringComparison.Ordinal);
                if (fileIsWindows != root.IsWindows ||
                    !normalizedFile.StartsWith(root.Normalized, root.Comparison) ||
                    (!rootEndsWithSeparator && normalizedFile.Length > root.Normalized.Length &&
                     normalizedFile[root.Normalized.Length] != '/'))
                {
                    return null;
                }

                relative = normalizedFile[root.Normalized.Length..].TrimStart('/');
            }
            else
            {
                relative = normalizedFile.TrimStart('/');
            }

            var segments = new List<string>();
            foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment == ".")
                {
                    continue;
                }

                if (segment == "..")
                {
                    if (segments.Count == 0)
                    {
                        return null;
                    }

                    segments.RemoveAt(segments.Count - 1);
                    continue;
                }

                segments.Add(segment);
            }

            if (segments.Count == 0)
            {
                return null;
            }

            return string.Join('/', segments.Select(Uri.EscapeDataString));
        }

        private static string NormalizeSeparators(string value) => value.Replace('\\', '/');

        private static bool IsWindowsAbsolute(string value) =>
            (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '/') ||
            value.StartsWith("//", StringComparison.Ordinal);

        private static bool IsUnixAbsolute(string value) => value.StartsWith("/", StringComparison.Ordinal);
    }

    [GeneratedRegex("(?<![:A-Za-z0-9])(?:[A-Za-z]:/|//[^/\\s]+/[^/\\s]+/)[^\\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsAbsolutePathRegex();

    [GeneratedRegex("(?<![:/%A-Za-z0-9])/(?:[^/\\s]+/)+[^/\\s]*", RegexOptions.CultureInvariant)]
    private static partial Regex UnixAbsolutePathRegex();
}
