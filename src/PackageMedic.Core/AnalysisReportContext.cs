namespace PackageMedic.Core;

public sealed record AnalysisReportContext(
    string RepositoryRoot,
    string? ConfigurationFile,
    AnalysisPolicy Policy,
    PolicyApplication PolicyApplication,
    BaselineComparison Baseline,
    string? BaselineFile)
{
    public const int ReportSchemaVersion = 1;
}
