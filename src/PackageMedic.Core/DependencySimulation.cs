using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PackageMedic.Core;

public enum DependencySimulationVerdict
{
    Pass,
    Reject,
    NoChange,
    Incomplete,
}

public enum DependencySimulationVerificationStatus
{
    Passed,
    Failed,
    NotRun,
}

public enum DependencySimulationRestoreFailureKind
{
    PackageNotFound,
    VersionNotFound,
    DependencyConflict,
    LockedModeConflict,
    SourceUnavailable,
    AuthenticationFailed,
    TimedOut,
    OutputLimitExceeded,
    Unknown,
}

public enum DependencySimulationRuntimeCompatibilityStatus
{
    NotVerified,
}

public enum DependencySimulationEvidenceLevel
{
    RestoreOnly,
}

public enum DependencySimulationLockedMode
{
    NotEnabled,
    Enforced,
    Mixed,
}

public sealed record DependencySimulationRepository(
    string HeadCommit,
    string AnalysisTarget,
    bool WorkingTreeRequiredClean);

public sealed record DependencySimulationRequest(
    string PackageId,
    string CandidateVersion);

public sealed record DependencySimulationMutation(
    string PackageId,
    string File,
    int Line,
    PackageVersionDeclarationKind Kind,
    string BeforeVersion,
    string CandidateVersion,
    IReadOnlyList<string> AffectedProjects)
{
    public bool NoChange { get; init; }

    public string? SourceSha256Before { get; init; }

    public string? SourceSha256After { get; init; }

    public static DependencySimulationMutation From(PackageVersionEditResult edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return new DependencySimulationMutation(
            edit.PackageId,
            edit.File,
            edit.Line,
            edit.Kind,
            edit.BeforeVersion,
            edit.CandidateVersion,
            edit.AffectedProjects)
        {
            NoChange = edit.NoChange,
            SourceSha256Before = NullIfEmpty(edit.SourceSha256Before),
            SourceSha256After = NullIfEmpty(edit.SourceSha256After),
        };
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

public sealed record DependencySimulationVerification(
    DependencySimulationVerificationStatus Restore,
    DependencySimulationRestoreFailureKind? RestoreFailureKind,
    DependencySimulationVerificationStatus Build,
    DependencySimulationVerificationStatus Tests,
    DependencySimulationRuntimeCompatibilityStatus RuntimeCompatibility,
    DependencySimulationEvidenceLevel EvidenceLevel,
    bool AuditedVulnerabilities,
    bool AuditedDeprecations,
    DependencySimulationLockedMode LockedMode)
{
    public static DependencySimulationVerification RestoreOnly(
        DependencySimulationVerificationStatus restore,
        bool auditedVulnerabilities,
        bool auditedDeprecations,
        DependencySimulationLockedMode lockedMode,
        DependencySimulationRestoreFailureKind? restoreFailureKind = null) => new(
            restore,
            restore == DependencySimulationVerificationStatus.Failed
                ? restoreFailureKind ?? DependencySimulationRestoreFailureKind.Unknown
                : null,
            DependencySimulationVerificationStatus.NotRun,
            DependencySimulationVerificationStatus.NotRun,
            DependencySimulationRuntimeCompatibilityStatus.NotVerified,
            DependencySimulationEvidenceLevel.RestoreOnly,
            auditedVulnerabilities,
            auditedDeprecations,
            lockedMode);

}

public sealed record DependencySimulationComparison(
    AnalysisDiffSummary DiagnosticSummary,
    IReadOnlyList<DiagnosticChange> DiagnosticChanges,
    PackageDiffSummary PackageSummary,
    IReadOnlyList<PackageChange> PackageChanges,
    DependencyRiskDiffSummary RiskSummary,
    IReadOnlyList<ProjectSettingsChange> ProjectSettingsChanges,
    DependencyImpactReport? Impact)
{
    public bool IsComplete { get; init; } = true;

    public string? UnavailableReason { get; init; }
}

public sealed record DependencySimulationReport(
    int SchemaVersion,
    string ToolVersion,
    DependencySimulationRepository Repository,
    DependencySimulationRequest Request,
    DependencySimulationMutation Mutation,
    DependencySimulationVerification Verification,
    DependencySimulationComparison Comparison,
    DependencySimulationVerdict Verdict,
    IReadOnlyList<string> RejectionReasons,
    IReadOnlyList<string> Errors)
{
    public const int CurrentSchemaVersion = 1;

    public string Kind => "dependencySimulation";

    public bool IsComplete => Verdict != DependencySimulationVerdict.Incomplete;

}

public static class DependencySimulationSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string SerializeJson(DependencySimulationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var normalized = NormalizeAndValidate(report);
        var contract = new SerializableReport(
            normalized.SchemaVersion,
            normalized.Kind,
            normalized.ToolVersion,
            normalized.Repository,
            normalized.Request,
            normalized.Mutation,
            normalized.Verification,
            normalized.Comparison,
            normalized.IsComplete,
            normalized.Verdict,
            normalized.RejectionReasons,
            normalized.Errors);
        return JsonSerializer.Serialize(contract, JsonOptions);
    }

    public static string SerializeText(DependencySimulationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var normalized = NormalizeAndValidate(report);
        var builder = new StringBuilder();
        builder.AppendLine("PackageMedic Dependency Time Machine");
        builder.Append("Package: ").Append(normalized.Mutation.PackageId)
            .Append(' ').Append(normalized.Mutation.BeforeVersion)
            .Append(" -> ").AppendLine(normalized.Mutation.CandidateVersion);
        builder.Append("Target: ").AppendLine(normalized.Repository.AnalysisTarget);
        builder.Append("HEAD: ")
            .AppendLine(normalized.Repository.HeadCommit[..Math.Min(12, normalized.Repository.HeadCommit.Length)]);
        builder.Append("Restore: ").Append(Status(normalized.Verification.Restore));
        if (normalized.Verification.RestoreFailureKind is { } restoreFailure)
        {
            builder.Append(" (").Append(ToWords(restoreFailure.ToString())).Append(')');
        }

        builder.AppendLine();
        builder.Append("Build: ").Append(Status(normalized.Verification.Build))
            .Append(" | Tests: ").Append(Status(normalized.Verification.Tests))
            .Append(" | Runtime compatibility: ")
            .AppendLine(ToWords(normalized.Verification.RuntimeCompatibility.ToString()));
        builder.Append("Evidence: restore only | Vulnerability audit: ")
            .Append(normalized.Verification.AuditedVulnerabilities ? "included" : "not requested")
            .Append(" | Deprecation audit: ")
            .AppendLine(normalized.Verification.AuditedDeprecations ? "included" : "not requested");
        builder.Append("Locked mode: ")
            .AppendLine(ToWords(normalized.Verification.LockedMode.ToString()));

        if (normalized.Comparison.IsComplete)
        {
            builder.Append("Packages +").Append(normalized.Comparison.PackageSummary.Added)
                .Append(" -").Append(normalized.Comparison.PackageSummary.Removed)
                .Append(" | Upgraded: ").Append(normalized.Comparison.PackageSummary.Upgraded)
                .Append(" | Downgraded: ").AppendLine(normalized.Comparison.PackageSummary.Downgraded.ToString());
            builder.Append("Vulnerabilities +").Append(normalized.Comparison.RiskSummary.VulnerabilitiesIntroduced)
                .Append(" -").Append(normalized.Comparison.RiskSummary.VulnerabilitiesResolved)
                .Append(" | Deprecations +").Append(normalized.Comparison.RiskSummary.DeprecationsIntroduced)
                .Append(" -").AppendLine(normalized.Comparison.RiskSummary.DeprecationsResolved.ToString());
            if (normalized.Comparison.Impact is { } impact)
            {
                builder.Append("Impact gate: ").Append(impact.GatePassed ? "pass" : "reject")
                    .Append(" | Violations: ").Append(impact.Summary.Violations)
                    .Append(" | Maximum blast radius: ").AppendLine(impact.Summary.MaximumBlastRadius.ToString());
            }
        }
        else
        {
            builder.Append("Comparison unavailable: ").AppendLine(normalized.Comparison.UnavailableReason);
        }

        foreach (var reason in normalized.RejectionReasons)
        {
            builder.Append("! [candidate rejected] ").AppendLine(reason);
        }

        foreach (var error in normalized.Errors)
        {
            builder.Append("! [simulation incomplete] ").AppendLine(error);
        }

        builder.Append("Verdict: ").AppendLine(Verdict(normalized.Verdict));
        builder.AppendLine(
            "Evidence is limited to dependency restore and graph analysis; build, tests, and runtime behavior were not verified.");
        builder.AppendLine(
            "Restore and MSBuild may execute repository-controlled logic and contact configured NuGet feeds.");
        return builder.ToString();
    }

    private static DependencySimulationReport NormalizeAndValidate(DependencySimulationReport report)
    {
        if (report.SchemaVersion != DependencySimulationReport.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported dependency simulation schema version '{report.SchemaVersion}'.");
        }

        if ((report.Repository.HeadCommit.Length is not 40 and not 64) ||
            !report.Repository.HeadCommit.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("The dependency simulation HEAD commit is invalid.");
        }

        if (!report.Repository.WorkingTreeRequiredClean)
        {
            throw new InvalidDataException("Dependency simulation schema 1 requires a clean Git working tree.");
        }

        if (string.IsNullOrWhiteSpace(report.ToolVersion) ||
            string.IsNullOrWhiteSpace(report.Request.PackageId) ||
            report.Request.PackageId.Length > 100 ||
            string.IsNullOrWhiteSpace(report.Request.CandidateVersion) ||
            report.Request.CandidateVersion.Length > 256 ||
            string.IsNullOrWhiteSpace(report.Mutation.PackageId) ||
            report.Mutation.PackageId.Length > 100 ||
            string.IsNullOrWhiteSpace(report.Mutation.BeforeVersion) ||
            report.Mutation.BeforeVersion.Length > 256 ||
            string.IsNullOrWhiteSpace(report.Mutation.CandidateVersion) ||
            report.Mutation.CandidateVersion.Length > 256 ||
            report.Mutation.Line < 1 ||
            report.Mutation.AffectedProjects.Count == 0)
        {
            throw new InvalidDataException("The dependency simulation contains invalid required identity or mutation evidence.");
        }

        EnsurePortablePath(report.Repository.AnalysisTarget, "analysis target");
        EnsurePortablePath(report.Mutation.File, "package mutation");
        foreach (var project in report.Mutation.AffectedProjects)
        {
            EnsurePortablePath(project, "affected project");
        }

        if (!report.Request.PackageId.Equals(report.Mutation.PackageId, StringComparison.OrdinalIgnoreCase) ||
            !report.Request.CandidateVersion.Equals(report.Mutation.CandidateVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The simulation request and isolated mutation disagree.");
        }

        ValidateSha256(report.Mutation.SourceSha256Before);
        ValidateSha256(report.Mutation.SourceSha256After);

        if (report.Verification.EvidenceLevel != DependencySimulationEvidenceLevel.RestoreOnly ||
            report.Verification.Build != DependencySimulationVerificationStatus.NotRun ||
            report.Verification.Tests != DependencySimulationVerificationStatus.NotRun ||
            report.Verification.RuntimeCompatibility != DependencySimulationRuntimeCompatibilityStatus.NotVerified)
        {
            throw new InvalidDataException(
                "Dependency Time Machine 0.5 can report only restore evidence with build, tests, and runtime compatibility unverified.");
        }

        if (report.Verification.Restore != DependencySimulationVerificationStatus.Failed &&
            report.Verification.RestoreFailureKind is not null ||
            report.Verification.Restore == DependencySimulationVerificationStatus.Failed &&
            report.Verification.RestoreFailureKind is null)
        {
            throw new InvalidDataException("The restore status and restore failure kind disagree.");
        }

        if (report.Comparison.IsComplete == !string.IsNullOrWhiteSpace(report.Comparison.UnavailableReason))
        {
            throw new InvalidDataException("The comparison completion state and unavailable reason disagree.");
        }

        if (report.Verdict == DependencySimulationVerdict.Reject &&
            !report.RejectionReasons.Any(reason => !string.IsNullOrWhiteSpace(reason)))
        {
            throw new InvalidDataException("A rejected dependency candidate must include at least one rejection reason.");
        }

        if (report.Verdict == DependencySimulationVerdict.Reject && report.Errors.Count > 0)
        {
            throw new InvalidDataException("A rejected dependency candidate cannot contain operational errors.");
        }

        if (report.Verdict is DependencySimulationVerdict.Pass or DependencySimulationVerdict.NoChange &&
            (report.RejectionReasons.Count > 0 || report.Errors.Count > 0 ||
             report.Verification.Restore != DependencySimulationVerificationStatus.Passed ||
             !report.Comparison.IsComplete))
        {
            throw new InvalidDataException(
                "A pass or no-change simulation cannot contain rejection reasons, operational errors, or incomplete restore evidence.");
        }

        if (report.Verdict == DependencySimulationVerdict.Incomplete &&
            !report.Errors.Any(error => !string.IsNullOrWhiteSpace(error)))
        {
            throw new InvalidDataException("An incomplete dependency simulation must include at least one operational error.");
        }

        if (report.Verdict == DependencySimulationVerdict.Incomplete && report.RejectionReasons.Count > 0)
        {
            throw new InvalidDataException("An incomplete dependency simulation cannot contain candidate rejection reasons.");
        }

        return report with
        {
            Repository = report.Repository with
            {
                HeadCommit = report.Repository.HeadCommit.ToLowerInvariant(),
                AnalysisTarget = NormalizePortablePath(report.Repository.AnalysisTarget),
            },
            Request = new DependencySimulationRequest(
                report.Request.PackageId,
                report.Request.CandidateVersion),
            Mutation = report.Mutation with
            {
                File = NormalizePortablePath(report.Mutation.File),
                SourceSha256Before = report.Mutation.SourceSha256Before!.ToLowerInvariant(),
                SourceSha256After = report.Mutation.SourceSha256After!.ToLowerInvariant(),
                AffectedProjects = report.Mutation.AffectedProjects
                    .Select(NormalizePortablePath)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
            },
            Comparison = report.Comparison with
            {
                UnavailableReason = NormalizeOptionalText(report.Comparison.UnavailableReason),
            },
            RejectionReasons = NormalizeMessages(report.RejectionReasons),
            Errors = NormalizeMessages(report.Errors),
        };
    }

    private static IReadOnlyList<string> NormalizeMessages(IReadOnlyList<string> messages) => messages
        .Select(ProcessRunner.RedactSecrets)
        .Where(message => !string.IsNullOrWhiteSpace(message))
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static string? NormalizeOptionalText(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : ProcessRunner.RedactSecrets(value);

    private static void EnsurePortablePath(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            Path.IsPathFullyQualified(value) ||
            value.Equals("..", StringComparison.Ordinal) ||
            value.Replace('\\', '/').Split('/').Any(segment => segment == ".."))
        {
            throw new InvalidDataException($"The {description} must be a repository-portable relative path.");
        }
    }

    private static string NormalizePortablePath(string value) => value.Replace('\\', '/');

    private static void ValidateSha256(string? value)
    {
        if (value is null || value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("A dependency simulation source hash must be a SHA-256 hexadecimal value.");
        }
    }

    private static string Status(DependencySimulationVerificationStatus status) => status switch
    {
        DependencySimulationVerificationStatus.Passed => "passed",
        DependencySimulationVerificationStatus.Failed => "failed",
        DependencySimulationVerificationStatus.NotRun => "not run",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static string Verdict(DependencySimulationVerdict verdict) => verdict switch
    {
        DependencySimulationVerdict.Pass => "pass",
        DependencySimulationVerdict.Reject => "reject",
        DependencySimulationVerdict.NoChange => "no change",
        DependencySimulationVerdict.Incomplete => "incomplete",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict)),
    };

    private static string ToWords(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (index > 0 && char.IsUpper(character))
            {
                builder.Append(' ');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private sealed record SerializableReport(
        int SchemaVersion,
        string Kind,
        string ToolVersion,
        DependencySimulationRepository Repository,
        DependencySimulationRequest Request,
        DependencySimulationMutation Mutation,
        DependencySimulationVerification Verification,
        DependencySimulationComparison Comparison,
        bool IsComplete,
        DependencySimulationVerdict Verdict,
        IReadOnlyList<string> RejectionReasons,
        IReadOnlyList<string> Errors);
}
