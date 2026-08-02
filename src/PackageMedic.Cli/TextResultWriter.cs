using PackageMedic.Core;

namespace PackageMedic.Cli;

public static class TextResultWriter
{
    public static async Task WriteAsync(AnalysisResult result, OutputVerbosity verbosity, TextWriter writer)
    {
        if (verbosity != OutputVerbosity.Quiet)
        {
            await writer.WriteLineAsync($"PackageMedic {result.Version}").ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync("Scanned:").ConfigureAwait(false);
            await writer.WriteLineAsync($"  {result.Summary.Solutions} {Plural(result.Summary.Solutions, "solution", "solutions")}").ConfigureAwait(false);
            await writer.WriteLineAsync($"  {result.Summary.Projects} {Plural(result.Summary.Projects, "project", "projects")}").ConfigureAwait(false);
            await writer.WriteLineAsync($"  {result.Summary.DirectPackages} direct packages").ConfigureAwait(false);
            await writer.WriteLineAsync($"  {result.Summary.TransitivePackages} transitive packages").ConfigureAwait(false);

            foreach (var diagnostic in result.Diagnostics)
            {
                await WriteDiagnosticAsync(diagnostic, verbosity, writer).ConfigureAwait(false);
            }

            if (result.AnalysisErrors.Count > 0)
            {
                await writer.WriteLineAsync().ConfigureAwait(false);
                await writer.WriteLineAsync("Analysis errors:").ConfigureAwait(false);
                foreach (var analysisError in result.AnalysisErrors)
                {
                    await writer.WriteLineAsync($"  {analysisError}").ConfigureAwait(false);
                }
            }
        }

        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync(
            $"Summary: {result.Summary.Errors} errors, {result.Summary.Warnings} warnings, {result.Summary.Information} informational").ConfigureAwait(false);
    }

    private static async Task WriteDiagnosticAsync(Diagnostic diagnostic, OutputVerbosity verbosity, TextWriter writer)
    {
        await writer.WriteLineAsync().ConfigureAwait(false);
        var originalCode = diagnostic.OriginalCode is null ? string.Empty : $" ({diagnostic.OriginalCode})";
        await writer.WriteLineAsync($"{diagnostic.Code}{originalCode} {diagnostic.Severity.ToString().ToLowerInvariant()}: {diagnostic.Title}").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(diagnostic.Project))
        {
            await writer.WriteLineAsync($"  Project: {diagnostic.Project}").ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(diagnostic.File))
        {
            var line = diagnostic.Line is null ? string.Empty : $":{diagnostic.Line}";
            await writer.WriteLineAsync($"  File: {diagnostic.File}{line}").ConfigureAwait(false);
        }

        await writer.WriteLineAsync($"  {diagnostic.Explanation}").ConfigureAwait(false);
        if (verbosity == OutputVerbosity.Detailed)
        {
            await writer.WriteLineAsync($"  Evidence: {diagnostic.Evidence}").ConfigureAwait(false);
            if (diagnostic.Confidence is not null)
            {
                await writer.WriteLineAsync($"  Confidence: {diagnostic.Confidence.ToString()!.ToLowerInvariant()}").ConfigureAwait(false);
            }
        }

        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync("  Suggested action:").ConfigureAwait(false);
        await writer.WriteLineAsync($"  {diagnostic.SuggestedAction}").ConfigureAwait(false);
    }

    private static string Plural(int count, string singular, string plural) => count == 1 ? singular : plural;
}
