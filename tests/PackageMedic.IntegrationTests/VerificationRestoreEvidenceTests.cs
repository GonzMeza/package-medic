using PackageMedic.Cli;
using PackageMedic.Core;

namespace PackageMedic.IntegrationTests;

public sealed class VerificationRestoreEvidenceTests
{
    [Theory]
    [InlineData("NU1101", VerificationFailureKind.PackageNotFound)]
    [InlineData("NU1102", VerificationFailureKind.VersionNotFound)]
    [InlineData("NU1605", VerificationFailureKind.DependencyConflict)]
    public void CandidateAttributableRestoreFailuresRemainDeterministic(
        string code,
        VerificationFailureKind expected)
    {
        var restore = new RestoreExecutionResult(
            [RestoreDiagnostic(code)],
            [$"restore rejected with {code}"],
            RestoreProcessFailureKind.Rejected);

        var evidence = Program.CreateRestoreVerificationEvidence(restore, true, []);

        Assert.Equal(VerificationStageStatus.Failed, evidence.Status);
        Assert.Equal(expected, evidence.FailureKind);
    }

    [Fact]
    public void LockedModeConflictIsDeterministicOnlyWithLockedEvidence()
    {
        var restore = new RestoreExecutionResult(
            [RestoreDiagnostic("NU1004")],
            ["locked restore rejected with NU1004"],
            RestoreProcessFailureKind.Rejected);
        var settings = new ProjectPackageSettings("App.csproj", true, false)
        {
            RestoreLockedMode = true,
        };

        var evidence = Program.CreateRestoreVerificationEvidence(restore, true, [settings]);

        Assert.Equal(VerificationStageStatus.Failed, evidence.Status);
        Assert.Equal(VerificationFailureKind.LockedModeConflict, evidence.FailureKind);
    }

    [Fact]
    public void OperationalAndPostRestoreAnalysisFailuresStayIncomplete()
    {
        var timeout = Program.CreateRestoreVerificationEvidence(
            new RestoreExecutionResult([], ["timeout"], RestoreProcessFailureKind.TimedOut),
            true,
            []);
        var analysis = Program.CreateRestoreVerificationEvidence(
            new RestoreExecutionResult([], [], RestoreProcessFailureKind.None),
            true,
            []);

        Assert.Equal(VerificationStageStatus.Incomplete, timeout.Status);
        Assert.Equal(VerificationFailureKind.TimedOut, timeout.FailureKind);
        Assert.Equal(VerificationStageStatus.Incomplete, analysis.Status);
        Assert.Equal(VerificationFailureKind.AnalysisIncomplete, analysis.FailureKind);
    }

    [Fact]
    public void MixedDeterministicAndOperationalRestoreFailuresStayIncomplete()
    {
        var restore = new RestoreExecutionResult(
            [RestoreDiagnostic("NU1102")],
            [
                "Project A restore rejected with NU1102.",
                "Project B restore failed in a repository-defined MSBuild target with MSB4018.",
            ],
            RestoreProcessFailureKind.Rejected)
        {
            RejectedTargets =
            [
                RestoreRejectionEvidenceKind.VersionNotFound,
                RestoreRejectionEvidenceKind.Unknown,
            ],
        };

        var evidence = Program.CreateRestoreVerificationEvidence(restore, true, []);

        Assert.Equal(VerificationStageStatus.Incomplete, evidence.Status);
        Assert.Equal(VerificationFailureKind.Unknown, evidence.FailureKind);
        Assert.Equal(
            DependencySimulationRestoreFailureKind.Unknown,
            Program.ClassifySimulationRestoreFailure(restore, DependencySimulationLockedMode.NotEnabled));
    }

    private static Diagnostic RestoreDiagnostic(string code) => new(
        "PM005",
        DiagnosticSeverity.Error,
        "NuGet restore problem",
        "NuGet restore failed.",
        null,
        "App.csproj",
        null,
        "restore failed",
        "Review restore.",
        DiagnosticConfidence.High,
        code);
}
