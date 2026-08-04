using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PackageMedic.Core;

public static class ResultJsonSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(AnalysisResult result) => Serialize(result, null);

    public static string Serialize(
        AnalysisResult result,
        AnalysisReportContext? context,
        AnalysisDiffReport? diff = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            Write(writer, result, context, diff);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static async Task SerializeAsync(
        Stream destination,
        AnalysisResult result,
        AnalysisReportContext? context = null,
        AnalysisDiffReport? diff = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(result);
        using var writer = new Utf8JsonWriter(destination, new JsonWriterOptions { Indented = true });
        Write(writer, result, context, diff);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Write(
        Utf8JsonWriter writer,
        AnalysisResult result,
        AnalysisReportContext? context,
        AnalysisDiffReport? diff)
    {
        var root = context is null ? null : RepositoryRoot.Parse(context.RepositoryRoot);
        var classified = context?.Baseline.Current
            .GroupBy(item => item.Fingerprint, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        writer.WriteStartObject();
        writer.WriteString("version", result.Version);
        writer.WriteString("target", context is null
            ? result.Target
            : ToPortablePath(result.Target, context.RepositoryRoot) ?? ".");
        writer.WritePropertyName("summary");
        JsonSerializer.Serialize(writer, result.Summary, Options);

        writer.WriteStartArray("diagnostics");
        foreach (var diagnostic in result.Diagnostics)
        {
            WriteDiagnostic(writer, diagnostic, context, root, classified);
        }

        writer.WriteEndArray();
        writer.WriteStartArray("analysisErrors");
        foreach (var error in result.AnalysisErrors)
        {
            writer.WriteStringValue(context is null
                ? error
                : DiagnosticFingerprint.SanitizeText(ProcessRunner.RedactSecrets(error), root!));
        }

        writer.WriteEndArray();
        writer.WritePropertyName("packages");
        JsonSerializer.Serialize(
            writer,
            context is null
                ? result.Packages
                : result.Packages.Select(item => item with
                {
                    Project = ToPortablePath(item.Project, context.RepositoryRoot) ?? Path.GetFileName(item.Project),
                    SourceFile = ToPortablePath(item.SourceFile, context.RepositoryRoot),
                }),
            Options);
        writer.WritePropertyName("projectSettings");
        JsonSerializer.Serialize(
            writer,
            context is null
                ? result.ProjectSettings
                : result.ProjectSettings.Select(item => item with
                {
                    Project = ToPortablePath(item.Project, context.RepositoryRoot) ?? Path.GetFileName(item.Project),
                }),
            Options);
        writer.WritePropertyName("vulnerabilities");
        JsonSerializer.Serialize(
            writer,
            context is null
                ? result.Vulnerabilities
                : result.Vulnerabilities.Select(item => item with
                {
                    Project = ToPortablePath(item.Project, context.RepositoryRoot) ?? Path.GetFileName(item.Project),
                }),
            Options);
        writer.WritePropertyName("dependencyPaths");
        JsonSerializer.Serialize(
            writer,
            context is null
                ? result.DependencyPaths
                : result.DependencyPaths.Select(item => item with
                {
                    Project = ToPortablePath(item.Project, context.RepositoryRoot) ?? Path.GetFileName(item.Project),
                }),
            Options);
        writer.WritePropertyName("deprecatedPackages");
        JsonSerializer.Serialize(
            writer,
            context is null
                ? result.DeprecatedPackages
                : result.DeprecatedPackages.Select(item => item with
                {
                    Project = ToPortablePath(item.Project, context.RepositoryRoot) ?? Path.GetFileName(item.Project),
                }),
            Options);
        writer.WriteNumber("schemaVersion", AnalysisReportContext.ReportSchemaVersion);

        if (context is not null)
        {
            writer.WritePropertyName("policy");
            JsonSerializer.Serialize(writer, new
            {
                configurationFile = context.ConfigurationFile,
                failOn = context.Policy.FailOn,
                failOnNew = context.Policy.FailOnNew,
                impact = context.Policy.Impact,
                baselineFile = context.BaselineFile,
                suppressed = context.PolicyApplication.SuppressedDiagnostics.Count,
                excluded = context.PolicyApplication.ExcludedDiagnostics.Count,
                disabled = context.PolicyApplication.DisabledDiagnostics.Count,
            }, Options);
            writer.WritePropertyName("baseline");
            JsonSerializer.Serialize(writer, new
            {
                @new = context.Baseline.NewCount,
                existing = context.Baseline.ExistingCount,
                resolved = context.Baseline.ResolvedCount,
            }, Options);
            writer.WritePropertyName("resolvedDiagnostics");
            JsonSerializer.Serialize(
                writer,
                context.Baseline.Resolved.Select(item => new
                {
                    fingerprint = item.Fingerprint,
                    code = item.RuleId,
                    severity = item.Severity,
                    title = item.Title,
                    file = item.File,
                    project = item.Project,
                    baselineState = "resolved",
                }),
                Options);
            writer.WritePropertyName("suppressedDiagnostics");
            JsonSerializer.Serialize(
                writer,
                context.PolicyApplication.SuppressedDiagnostics.Select(item => new
                {
                    code = item.Diagnostic.Code,
                    title = DiagnosticFingerprint.SanitizeText(item.Diagnostic.Title, root!),
                    file = ToPortablePath(item.Diagnostic.File, context.RepositoryRoot),
                    line = item.Diagnostic.Line,
                    reason = item.Suppression.Reason,
                }),
                Options);
        }

        if (diff is not null)
        {
            writer.WritePropertyName("diff");
            JsonSerializer.Serialize(writer, diff, Options);
        }

        writer.WriteEndObject();
    }

    private static void WriteDiagnostic(
        Utf8JsonWriter writer,
        Diagnostic diagnostic,
        AnalysisReportContext? context,
        RepositoryRoot? root,
        IReadOnlyDictionary<string, BaselineDiagnostic>? classified)
    {
        writer.WriteStartObject();
        writer.WriteString("code", diagnostic.Code);
        writer.WritePropertyName("severity");
        JsonSerializer.Serialize(writer, diagnostic.Severity, Options);
        writer.WriteString("title", context is null
            ? diagnostic.Title
            : DiagnosticFingerprint.SanitizeText(diagnostic.Title, root!));
        writer.WriteString("explanation", context is null
            ? diagnostic.Explanation
            : DiagnosticFingerprint.SanitizeText(diagnostic.Explanation, root!));
        WriteOptionalString(writer, "project", context is null
            ? diagnostic.Project
            : ToPortablePath(diagnostic.Project, context.RepositoryRoot));
        WriteOptionalString(writer, "file", context is null
            ? diagnostic.File
            : ToPortablePath(diagnostic.File, context.RepositoryRoot));
        if (diagnostic.Line is { } line)
        {
            writer.WriteNumber("line", line);
        }

        writer.WriteString("evidence", context is null
            ? diagnostic.Evidence
            : DiagnosticFingerprint.SanitizeText(diagnostic.Evidence, root!));
        writer.WriteString("suggestedAction", context is null
            ? diagnostic.SuggestedAction
            : DiagnosticFingerprint.SanitizeText(diagnostic.SuggestedAction, root!));
        if (diagnostic.Confidence is { } confidence)
        {
            writer.WritePropertyName("confidence");
            JsonSerializer.Serialize(writer, confidence, Options);
        }

        WriteOptionalString(writer, "originalCode", diagnostic.OriginalCode);
        WriteOptionalString(writer, "packageId", diagnostic.PackageId);
        if (context is not null)
        {
            var fingerprint = DiagnosticFingerprint.Compute(diagnostic, context.RepositoryRoot);
            if (classified?.TryGetValue(fingerprint, out var baselineDiagnostic) == true)
            {
                writer.WriteString("fingerprint", baselineDiagnostic.Fingerprint);
                writer.WriteString("baselineState", baselineDiagnostic.State.ToString().ToLowerInvariant());
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }

    private static string? ToPortablePath(string? value, string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Path.IsPathFullyQualified(value) && string.Equals(Path.GetFullPath(value), Path.GetFullPath(repositoryRoot),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return ".";
        }

        return DiagnosticFingerprint.GetRelativePath(value, repositoryRoot);
    }
}
