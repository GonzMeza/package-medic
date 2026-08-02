using System.Text.RegularExpressions;

namespace PackageMedic.Core;

public sealed partial class RestoreRunner(IProcessRunner processRunner)
{
    public async Task<(IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<string> Errors)> RestoreAsync(
        DiscoveryResult discovery,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<Diagnostic>();
        var errors = new List<string>();

        foreach (var target in discovery.RestoreTargets)
        {
            progress?.Invoke($"Running dotnet restore for {Path.GetFileName(target)} (configured NuGet feeds may be contacted)...");
            var result = await processRunner.RunAsync(
                "dotnet",
                ["restore", target, "--nologo", "--verbosity", "minimal"],
                Path.GetDirectoryName(target)!,
                cancellationToken).ConfigureAwait(false);

            diagnostics.AddRange(ParseNuGetDiagnostics(result.CombinedOutput, target));
            if (result.ExitCode != 0)
            {
                errors.Add($"dotnet restore failed for '{target}' with exit code {result.ExitCode}.");
            }
        }

        return (Deduplicate(diagnostics), errors);
    }

    public static IReadOnlyList<Diagnostic> ParseNuGetDiagnostics(string output, string fallbackFile)
    {
        var diagnostics = new List<Diagnostic>();
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = NuGetDiagnosticRegex().Match(rawLine);
            if (!match.Success)
            {
                continue;
            }

            var severity = match.Groups["level"].Value.Equals("error", StringComparison.OrdinalIgnoreCase)
                ? DiagnosticSeverity.Error
                : DiagnosticSeverity.Warning;
            var code = match.Groups["code"].Value.ToUpperInvariant();
            var message = match.Groups["message"].Value.Trim();
            var location = match.Groups["file"].Success ? match.Groups["file"].Value.Trim() : fallbackFile;
            diagnostics.Add(new Diagnostic(
                "PM005",
                severity,
                "NuGet restore problem",
                $"NuGet reported {code} during restore.",
                null,
                location,
                null,
                message,
                "Resolve the underlying NuGet restore issue, then run PackageMedic again.",
                DiagnosticConfidence.High,
                code));
        }

        return Deduplicate(diagnostics);
    }

    internal static IReadOnlyList<Diagnostic> Deduplicate(IEnumerable<Diagnostic> diagnostics) => diagnostics
        .DistinctBy(item => (item.Code, item.OriginalCode, item.Severity, item.File, item.Line, item.Evidence))
        .OrderByDescending(item => item.Severity)
        .ThenBy(item => item.OriginalCode, StringComparer.Ordinal)
        .ThenBy(item => item.File, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.Line)
        .ToArray();

    [GeneratedRegex("(?:(?<file>.+?)\\s*:\\s*)?(?<level>warning|error)\\s+(?<code>NU\\d{4})\\s*:\\s*(?<message>.+?)(?:\\s+\\[[^]]+\\])?$", RegexOptions.IgnoreCase)]
    private static partial Regex NuGetDiagnosticRegex();
}
