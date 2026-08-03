namespace PackageMedic.Core;

public sealed record DiagnosticRuleMetadata(
    string Code,
    string Name,
    string ShortDescription,
    string FullDescription,
    string HelpUri,
    DiagnosticSeverity DefaultSeverity);

public static class DiagnosticRuleCatalog
{
    private const string DiagnosticHelpBaseUri =
        "https://github.com/GonzMeza/package-medic/blob/main/docs/diagnostics/README.md";

    private static readonly IReadOnlyList<DiagnosticRuleMetadata> Rules =
    [
        new(
            "PM001",
            "UnusedCentralPackageVersion",
            "Central package version is not used",
            "An effective Central Package Management version is not used by any affected project.",
            $"{DiagnosticHelpBaseUri}#pm001--unusedcentralpackageversion",
            DiagnosticSeverity.Warning),
        new(
            "PM002",
            "PackageVersionDrift",
            "Package versions differ across projects",
            "A direct package has different explicit versions across projects that are not centrally managed.",
            $"{DiagnosticHelpBaseUri}#pm002--packageversiondrift",
            DiagnosticSeverity.Warning),
        new(
            "PM003",
            "CentralPackageManagementBypass",
            "PackageReference bypasses Central Package Management",
            "A PackageReference declares Version while Central Package Management is enabled.",
            $"{DiagnosticHelpBaseUri}#pm003--centralpackagemanagementbypass",
            DiagnosticSeverity.Warning),
        new(
            "PM004",
            "DuplicateCentralPackageVersion",
            "Central package version is defined more than once",
            "Multiple effective PackageVersion items define the same package in a project's evaluated scope.",
            $"{DiagnosticHelpBaseUri}#pm004--duplicatecentralpackageversion",
            DiagnosticSeverity.Error),
        new(
            "PM005",
            "NuGetRestoreProblem",
            "NuGet reported a restore problem",
            "A NuGet warning or error was reported by restore or recorded in project.assets.json.",
            $"{DiagnosticHelpBaseUri}#pm005--nugetrestoreproblem",
            DiagnosticSeverity.Warning),
    ];

    private static readonly IReadOnlyDictionary<string, DiagnosticRuleMetadata> RulesByCode =
        Rules.ToDictionary(rule => rule.Code, StringComparer.Ordinal);

    public static IReadOnlyList<DiagnosticRuleMetadata> All => Rules;

    public static bool TryGet(string code, out DiagnosticRuleMetadata? metadata) =>
        RulesByCode.TryGetValue(code, out metadata);

    public static DiagnosticRuleMetadata GetRequired(string code) =>
        RulesByCode.TryGetValue(code, out var metadata)
            ? metadata
            : throw new ArgumentException($"Unknown PackageMedic diagnostic code '{code}'.", nameof(code));
}
