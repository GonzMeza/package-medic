namespace PackageMedic.Core;

/// <summary>
/// Describes the highest verification stage requested by the caller. Higher levels include all
/// preceding stages: test includes build and build includes restore.
/// </summary>
public enum VerificationLevel
{
    Restore,
    Build,
    Test,
}

public enum VerificationStage
{
    Restore,
    Build,
    Test,
}

public enum VerificationStageStatus
{
    NotRequested,
    Passed,
    Failed,
    Incomplete,
}

public enum VerificationFailureKind
{
    PackageNotFound,
    VersionNotFound,
    DependencyConflict,
    LockedModeConflict,
    BuildFailed,
    TestsFailed,
    SourceUnavailable,
    AuthenticationFailed,
    TimedOut,
    Cancelled,
    ProcessStartFailed,
    OutputLimitExceeded,
    ResultLimitExceeded,
    TestResultsUnavailable,
    NoTestsDiscovered,
    UnsupportedRunner,
    UnsafeEnvironment,
    SnapshotUnavailable,
    AnalysisIncomplete,
    ComparisonIncomplete,
    InternalError,
    Unknown,
}

public enum VerificationEvidenceLevel
{
    None,
    RestoreOnly,
    BuildVerified,
    TestVerified,
}

public enum VerificationVerdict
{
    Pass,
    Reject,
    NoChange,
    Incomplete,
}

public enum VerificationSnapshotRole
{
    Baseline,
    Candidate,
}

public sealed record VerificationStageEvidence(
    VerificationStageStatus Status,
    VerificationFailureKind? FailureKind = null)
{
    public static VerificationStageEvidence NotRequested { get; } = new(
        VerificationStageStatus.NotRequested);

    public static VerificationStageEvidence Passed { get; } = new(
        VerificationStageStatus.Passed);

    public static VerificationStageEvidence Failed(VerificationFailureKind failureKind) => new(
        VerificationStageStatus.Failed,
        failureKind);

    public static VerificationStageEvidence Incomplete(VerificationFailureKind failureKind) => new(
        VerificationStageStatus.Incomplete,
        failureKind);

    internal void Validate(VerificationStage stage, string parameterName)
    {
        if (!Enum.IsDefined(Status))
        {
            throw new ArgumentOutOfRangeException(parameterName, Status, "Unknown verification stage status.");
        }

        if (FailureKind is { } failureKind && !Enum.IsDefined(failureKind))
        {
            throw new ArgumentOutOfRangeException(parameterName, failureKind, "Unknown verification failure kind.");
        }

        switch (Status)
        {
            case VerificationStageStatus.NotRequested:
            case VerificationStageStatus.Passed:
                if (FailureKind is not null)
                {
                    throw new ArgumentException(
                        $"A {Status} {stage} stage cannot include a failure kind.",
                        parameterName);
                }

                break;

            case VerificationStageStatus.Failed:
                if (FailureKind is null)
                {
                    throw new ArgumentException(
                        $"A failed {stage} stage must include a deterministic failure kind.",
                        parameterName);
                }

                if (!VerificationFailureKinds.IsDeterministicForStage(FailureKind.Value, stage))
                {
                    throw new ArgumentException(
                        $"Failure kind '{FailureKind}' cannot represent a deterministic {stage} failure.",
                        parameterName);
                }

                break;

            case VerificationStageStatus.Incomplete:
                if (FailureKind is null)
                {
                    throw new ArgumentException(
                        $"An incomplete {stage} stage must include an operational failure kind.",
                        parameterName);
                }

                if (!VerificationFailureKinds.IsOperational(FailureKind.Value))
                {
                    throw new ArgumentException(
                        $"Failure kind '{FailureKind}' cannot represent incomplete {stage} evidence.",
                        parameterName);
                }

                break;
        }
    }
}

public sealed record VerificationSnapshotEvidence(
    VerificationStageEvidence Restore,
    VerificationStageEvidence Build,
    VerificationStageEvidence Test)
{
    public VerificationEvidenceLevel EvidenceLevel => Restore.Status != VerificationStageStatus.Passed
        ? VerificationEvidenceLevel.None
        : Build.Status != VerificationStageStatus.Passed
            ? VerificationEvidenceLevel.RestoreOnly
            : Test.Status != VerificationStageStatus.Passed
                ? VerificationEvidenceLevel.BuildVerified
                : VerificationEvidenceLevel.TestVerified;

    internal void Validate(VerificationLevel level, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(Restore, $"{parameterName}.{nameof(Restore)}");
        ArgumentNullException.ThrowIfNull(Build, $"{parameterName}.{nameof(Build)}");
        ArgumentNullException.ThrowIfNull(Test, $"{parameterName}.{nameof(Test)}");

        Restore.Validate(VerificationStage.Restore, $"{parameterName}.{nameof(Restore)}");
        Build.Validate(VerificationStage.Build, $"{parameterName}.{nameof(Build)}");
        Test.Validate(VerificationStage.Test, $"{parameterName}.{nameof(Test)}");

        if (Restore.Status == VerificationStageStatus.NotRequested)
        {
            throw new ArgumentException("Restore evidence is required for every verification level.", parameterName);
        }

        if (level == VerificationLevel.Restore)
        {
            RequireNotRequested(Build, VerificationStage.Build, parameterName);
            RequireNotRequested(Test, VerificationStage.Test, parameterName);
            return;
        }

        if (Restore.Status != VerificationStageStatus.Passed)
        {
            RequireNotRequested(Build, VerificationStage.Build, parameterName);
            RequireNotRequested(Test, VerificationStage.Test, parameterName);
            return;
        }

        if (Build.Status == VerificationStageStatus.NotRequested)
        {
            throw new ArgumentException(
                "Build evidence is required after restore passes at build or test verification level.",
                parameterName);
        }

        if (level == VerificationLevel.Build)
        {
            RequireNotRequested(Test, VerificationStage.Test, parameterName);
            return;
        }

        if (Build.Status != VerificationStageStatus.Passed)
        {
            RequireNotRequested(Test, VerificationStage.Test, parameterName);
            return;
        }

        if (Test.Status == VerificationStageStatus.NotRequested)
        {
            throw new ArgumentException(
                "Test evidence is required after build passes at test verification level.",
                parameterName);
        }
    }

    private static void RequireNotRequested(
        VerificationStageEvidence evidence,
        VerificationStage stage,
        string parameterName)
    {
        if (evidence.Status != VerificationStageStatus.NotRequested)
        {
            throw new ArgumentException(
                $"The {stage} stage must not run when it was not requested or a prerequisite did not pass.",
                parameterName);
        }
    }
}

public sealed record VerificationComparisonInput(
    VerificationLevel Level,
    VerificationSnapshotEvidence Baseline,
    VerificationSnapshotEvidence Candidate,
    bool HasDependencyChange)
{
    /// <summary>
    /// Captures a failure outside a restore, build, or test process, such as snapshot
    /// materialization or graph comparison. It always makes the comparison incomplete.
    /// </summary>
    public VerificationFailureKind? OperationalFailure { get; init; }
}

public sealed record VerificationDecision(
    VerificationVerdict Verdict,
    VerificationEvidenceLevel BaselineEvidenceLevel,
    VerificationEvidenceLevel CandidateEvidenceLevel,
    VerificationEvidenceLevel CommonEvidenceLevel,
    VerificationSnapshotRole? BlockingSnapshot,
    VerificationStage? BlockingStage,
    VerificationFailureKind? FailureKind)
{
    public bool IsComplete => Verdict != VerificationVerdict.Incomplete;

    public bool IsAccepted => Verdict is VerificationVerdict.Pass or VerificationVerdict.NoChange;
}

/// <summary>
/// Portable build evidence. Targets are repository-relative identities; operational paths and
/// raw subprocess output are deliberately excluded.
/// </summary>
public sealed record VerificationBuildReport(
    VerificationStageEvidence Stage,
    int PlannedTargets,
    int CompletedTargets,
    string? BlockingTarget = null,
    string? Failure = null);

/// <summary>
/// Portable aggregate test evidence. Only a bounded set of stable failed-test identities is
/// retained; timings, output and machine-specific locations are excluded.
/// </summary>
public sealed record VerificationTestReport(
    VerificationStageEvidence Stage,
    int PlannedProjects,
    int CompletedProjects,
    long Total,
    long Passed,
    long Failed,
    long Skipped,
    IReadOnlyList<string> FailedTestIdentities,
    bool HasAdditionalFailedTests = false,
    string? Failure = null);

public sealed record VerificationSnapshotReport(
    VerificationStageEvidence Restore,
    VerificationBuildReport Build,
    VerificationTestReport Tests);

public sealed record VerificationComparisonReport(
    VerificationLevel Level,
    VerificationSnapshotReport Baseline,
    VerificationSnapshotReport Candidate,
    VerificationDecision Decision);

/// <summary>
/// Produces a conservative verdict from immutable baseline and candidate evidence. A candidate
/// can be rejected only after the requested baseline stages pass. Operational uncertainty always
/// wins over a candidate rejection, preventing environment failures from being reported as a
/// dependency regression.
/// </summary>
public static class VerificationVerdictEngine
{
    public static VerificationDecision Compare(VerificationComparisonInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Validate(input);

        var commonEvidence = Minimum(
            input.Baseline.EvidenceLevel,
            input.Candidate.EvidenceLevel);

        if (input.OperationalFailure is { } operationalFailure)
        {
            return Decision(
                VerificationVerdict.Incomplete,
                input,
                commonEvidence,
                failureKind: operationalFailure);
        }

        var baselineBlock = FindFirstBlockingStage(input.Baseline, input.Level);
        if (baselineBlock is { } baseline)
        {
            return Decision(
                VerificationVerdict.Incomplete,
                input,
                commonEvidence,
                VerificationSnapshotRole.Baseline,
                baseline.Stage,
                baseline.Evidence.FailureKind);
        }

        var candidateBlock = FindFirstBlockingStage(input.Candidate, input.Level);
        if (candidateBlock is { Evidence.Status: VerificationStageStatus.Incomplete } incomplete)
        {
            return Decision(
                VerificationVerdict.Incomplete,
                input,
                commonEvidence,
                VerificationSnapshotRole.Candidate,
                incomplete.Stage,
                incomplete.Evidence.FailureKind);
        }

        if (candidateBlock is { Evidence.Status: VerificationStageStatus.Failed } failed)
        {
            return Decision(
                VerificationVerdict.Reject,
                input,
                commonEvidence,
                VerificationSnapshotRole.Candidate,
                failed.Stage,
                failed.Evidence.FailureKind);
        }

        return Decision(
            input.HasDependencyChange ? VerificationVerdict.Pass : VerificationVerdict.NoChange,
            input,
            commonEvidence);
    }

    private static void Validate(VerificationComparisonInput input)
    {
        if (!Enum.IsDefined(input.Level))
        {
            throw new ArgumentOutOfRangeException(nameof(input), input.Level, "Unknown verification level.");
        }

        ArgumentNullException.ThrowIfNull(input.Baseline);
        ArgumentNullException.ThrowIfNull(input.Candidate);
        input.Baseline.Validate(input.Level, $"{nameof(input)}.{nameof(input.Baseline)}");
        input.Candidate.Validate(input.Level, $"{nameof(input)}.{nameof(input.Candidate)}");

        if (input.OperationalFailure is { } failure && !VerificationFailureKinds.IsOperational(failure))
        {
            throw new ArgumentException(
                $"Failure kind '{failure}' is not an operational comparison failure.",
                nameof(input));
        }
    }

    private static VerificationDecision Decision(
        VerificationVerdict verdict,
        VerificationComparisonInput input,
        VerificationEvidenceLevel commonEvidence,
        VerificationSnapshotRole? blockingSnapshot = null,
        VerificationStage? blockingStage = null,
        VerificationFailureKind? failureKind = null) => new(
            verdict,
            input.Baseline.EvidenceLevel,
            input.Candidate.EvidenceLevel,
            commonEvidence,
            blockingSnapshot,
            blockingStage,
            failureKind);

    private static BlockingStage? FindFirstBlockingStage(
        VerificationSnapshotEvidence snapshot,
        VerificationLevel level)
    {
        if (snapshot.Restore.Status != VerificationStageStatus.Passed)
        {
            return new BlockingStage(VerificationStage.Restore, snapshot.Restore);
        }

        if (level == VerificationLevel.Restore)
        {
            return null;
        }

        if (snapshot.Build.Status != VerificationStageStatus.Passed)
        {
            return new BlockingStage(VerificationStage.Build, snapshot.Build);
        }

        if (level == VerificationLevel.Build)
        {
            return null;
        }

        return snapshot.Test.Status == VerificationStageStatus.Passed
            ? null
            : new BlockingStage(VerificationStage.Test, snapshot.Test);
    }

    private static VerificationEvidenceLevel Minimum(
        VerificationEvidenceLevel first,
        VerificationEvidenceLevel second) => Rank(first) <= Rank(second) ? first : second;

    private static int Rank(VerificationEvidenceLevel level) => level switch
    {
        VerificationEvidenceLevel.None => 0,
        VerificationEvidenceLevel.RestoreOnly => 1,
        VerificationEvidenceLevel.BuildVerified => 2,
        VerificationEvidenceLevel.TestVerified => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown verification evidence level."),
    };

    private sealed record BlockingStage(
        VerificationStage Stage,
        VerificationStageEvidence Evidence);
}

internal static class VerificationFailureKinds
{
    public static bool IsDeterministicForStage(
        VerificationFailureKind failureKind,
        VerificationStage stage) => stage switch
        {
            VerificationStage.Restore => failureKind is
                VerificationFailureKind.PackageNotFound or
                VerificationFailureKind.VersionNotFound or
                VerificationFailureKind.DependencyConflict or
                VerificationFailureKind.LockedModeConflict,
            VerificationStage.Build => failureKind == VerificationFailureKind.BuildFailed,
            VerificationStage.Test => failureKind == VerificationFailureKind.TestsFailed,
            _ => false,
        };

    public static bool IsOperational(VerificationFailureKind failureKind) => failureKind is
        VerificationFailureKind.SourceUnavailable or
        VerificationFailureKind.AuthenticationFailed or
        VerificationFailureKind.TimedOut or
        VerificationFailureKind.Cancelled or
        VerificationFailureKind.ProcessStartFailed or
        VerificationFailureKind.OutputLimitExceeded or
        VerificationFailureKind.ResultLimitExceeded or
        VerificationFailureKind.TestResultsUnavailable or
        VerificationFailureKind.NoTestsDiscovered or
        VerificationFailureKind.UnsupportedRunner or
        VerificationFailureKind.UnsafeEnvironment or
        VerificationFailureKind.SnapshotUnavailable or
        VerificationFailureKind.AnalysisIncomplete or
        VerificationFailureKind.ComparisonIncomplete or
        VerificationFailureKind.InternalError or
        VerificationFailureKind.Unknown;
}
