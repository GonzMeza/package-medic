namespace PackageMedic.Core;

public enum DiagnosticSeverity
{
    Information = 0,
    Warning = 1,
    Error = 2,
}

public enum DiagnosticConfidence
{
    Low,
    Medium,
    High,
}

public sealed record Diagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Title,
    string Explanation,
    string? Project,
    string? File,
    int? Line,
    string Evidence,
    string SuggestedAction,
    DiagnosticConfidence? Confidence = null,
    string? OriginalCode = null);

public sealed record ScanSummary(
    int Solutions,
    int Projects,
    int DirectPackages,
    int TransitivePackages,
    int Errors,
    int Warnings,
    int Information);

public sealed record AnalysisResult(
    string Version,
    string Target,
    ScanSummary Summary,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<string> AnalysisErrors);

public sealed record AnalysisOutcome(AnalysisResult Result, bool HasOperationalError);

public sealed record DiscoveryResult(
    string Target,
    IReadOnlyList<string> Solutions,
    IReadOnlyList<string> Projects,
    IReadOnlyList<string> RestoreTargets);

public sealed record DirectPackageReference(
    string Id,
    string? Version,
    string? VersionOverride,
    string SourceFile,
    int? Line,
    string? TargetFramework)
{
    public string? ExplicitVersion => !string.IsNullOrWhiteSpace(VersionOverride) ? VersionOverride : Version;
}

public sealed record CentralPackageVersion(
    string Id,
    string Version,
    string SourceFile,
    int? Line,
    string? TargetFramework);

public sealed class ProjectAnalysis
{
    public required string ProjectPath { get; init; }

    public required bool ManagePackageVersionsCentrally { get; init; }

    public required bool CentralPackageTransitivePinningEnabled { get; init; }

    public required IReadOnlyList<string> TargetFrameworks { get; init; }

    public required IReadOnlyList<DirectPackageReference> DirectPackages { get; init; }

    public required IReadOnlyList<CentralPackageVersion> CentralVersions { get; init; }

    public required IReadOnlySet<string> ResolvedPackages { get; init; }

    public required IReadOnlySet<string> TransitivePackages { get; init; }

    public required IReadOnlyList<Diagnostic> AssetDiagnostics { get; init; }

    public string Name => Path.GetFileNameWithoutExtension(ProjectPath);
}
