using System.Text.Json;
using System.Text.Json.Nodes;
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

    public static string Serialize(AnalysisResult result, AnalysisReportContext? context)
    {
        ArgumentNullException.ThrowIfNull(result);
        var root = JsonSerializer.SerializeToNode(result, Options)?.AsObject()
            ?? throw new InvalidOperationException("Could not serialize the analysis result.");
        root["schemaVersion"] = AnalysisReportContext.ReportSchemaVersion;

        if (context is not null)
        {
            var classified = context.Baseline.Current
                .GroupBy(item => item.Fingerprint, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var diagnostics = new JsonArray();
            foreach (var diagnostic in result.Diagnostics)
            {
                var node = JsonSerializer.SerializeToNode(diagnostic, Options)?.AsObject()
                    ?? throw new InvalidOperationException("Could not serialize a diagnostic.");
                var fingerprint = DiagnosticFingerprint.Compute(diagnostic, context.RepositoryRoot);
                if (classified.TryGetValue(fingerprint, out var baselineDiagnostic))
                {
                    node["fingerprint"] = baselineDiagnostic.Fingerprint;
                    node["baselineState"] = baselineDiagnostic.State.ToString().ToLowerInvariant();
                }

                diagnostics.Add(node);
            }

            root["diagnostics"] = diagnostics;
            root["policy"] = JsonSerializer.SerializeToNode(
                new
                {
                    configurationFile = context.ConfigurationFile,
                    failOn = context.Policy.FailOn,
                    failOnNew = context.Policy.FailOnNew,
                    baselineFile = context.BaselineFile,
                    suppressed = context.PolicyApplication.SuppressedDiagnostics.Count,
                    excluded = context.PolicyApplication.ExcludedDiagnostics.Count,
                    disabled = context.PolicyApplication.DisabledDiagnostics.Count,
                },
                Options);
            root["baseline"] = JsonSerializer.SerializeToNode(
                new
                {
                    @new = context.Baseline.NewCount,
                    existing = context.Baseline.ExistingCount,
                    resolved = context.Baseline.ResolvedCount,
                },
                Options);
            root["resolvedDiagnostics"] = JsonSerializer.SerializeToNode(
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
            root["suppressedDiagnostics"] = JsonSerializer.SerializeToNode(
                context.PolicyApplication.SuppressedDiagnostics.Select(item => new
                {
                    code = item.Diagnostic.Code,
                    title = item.Diagnostic.Title,
                    file = item.Diagnostic.File,
                    line = item.Diagnostic.Line,
                    reason = item.Suppression.Reason,
                }),
                Options);
        }

        return root.ToJsonString(Options);
    }
}
