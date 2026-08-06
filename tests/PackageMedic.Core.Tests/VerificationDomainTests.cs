namespace PackageMedic.Core.Tests;

public sealed class VerificationDomainTests
{
    [Fact]
    public void VerdictTruthTableIsConservativeForEveryRequestedStage()
    {
        var exercisedCases = 0;
        foreach (var level in Enum.GetValues<VerificationLevel>())
        {
            var outcomes = ValidOutcomes(level);
            foreach (var baseline in outcomes)
            {
                foreach (var candidate in outcomes)
                {
                    foreach (var hasDependencyChange in new[] { false, true })
                    {
                        foreach (var operationalFailure in new VerificationFailureKind?[]
                                 {
                                     null,
                                     VerificationFailureKind.ComparisonIncomplete,
                                 })
                        {
                            var input = new VerificationComparisonInput(
                                level,
                                baseline.Evidence,
                                candidate.Evidence,
                                hasDependencyChange)
                            {
                                OperationalFailure = operationalFailure,
                            };

                            var expected = operationalFailure is not null || baseline.Kind != OutcomeKind.Passed
                                ? VerificationVerdict.Incomplete
                                : candidate.Kind == OutcomeKind.OperationalFailure
                                    ? VerificationVerdict.Incomplete
                                    : candidate.Kind == OutcomeKind.DeterministicFailure
                                        ? VerificationVerdict.Reject
                                        : hasDependencyChange
                                            ? VerificationVerdict.Pass
                                            : VerificationVerdict.NoChange;

                            var decision = VerificationVerdictEngine.Compare(input);

                            Assert.True(
                                decision.Verdict == expected,
                                $"Expected {expected} for {level}; baseline={baseline.Kind}/{baseline.Stage}; " +
                                $"candidate={candidate.Kind}/{candidate.Stage}; changed={hasDependencyChange}; " +
                                $"operational={operationalFailure}, but received {decision.Verdict}.");
                            exercisedCases++;
                        }
                    }
                }
            }
        }

        Assert.Equal(332, exercisedCases);
    }

    [Fact]
    public void AllRequestedStagesPassingProducesPassAndExpectedEvidence()
    {
        var cases = new[]
        {
            new PassingCase(VerificationLevel.Restore, VerificationEvidenceLevel.RestoreOnly),
            new PassingCase(VerificationLevel.Build, VerificationEvidenceLevel.BuildVerified),
            new PassingCase(VerificationLevel.Test, VerificationEvidenceLevel.TestVerified),
        };

        foreach (var item in cases)
        {
            var decision = VerificationVerdictEngine.Compare(new VerificationComparisonInput(
                item.Level,
                Passing(item.Level),
                Passing(item.Level),
                HasDependencyChange: true));

            Assert.Equal(VerificationVerdict.Pass, decision.Verdict);
            Assert.Equal(item.EvidenceLevel, decision.BaselineEvidenceLevel);
            Assert.Equal(item.EvidenceLevel, decision.CandidateEvidenceLevel);
            Assert.Equal(item.EvidenceLevel, decision.CommonEvidenceLevel);
            Assert.Null(decision.BlockingSnapshot);
            Assert.Null(decision.BlockingStage);
            Assert.Null(decision.FailureKind);
            Assert.True(decision.IsComplete);
            Assert.True(decision.IsAccepted);
        }
    }

    [Fact]
    public void CompleteEvidenceWithoutADependencyChangeProducesNoChange()
    {
        foreach (var level in Enum.GetValues<VerificationLevel>())
        {
            var decision = VerificationVerdictEngine.Compare(new VerificationComparisonInput(
                level,
                Passing(level),
                Passing(level),
                HasDependencyChange: false));

            Assert.Equal(VerificationVerdict.NoChange, decision.Verdict);
            Assert.True(decision.IsComplete);
            Assert.True(decision.IsAccepted);
        }
    }

    [Fact]
    public void AnyUnusableBaselineProducesIncompleteInsteadOfReject()
    {
        var cases = new[]
        {
            new BlockingCase(
                VerificationLevel.Restore,
                VerificationStage.Restore,
                VerificationStageEvidence.Failed(VerificationFailureKind.VersionNotFound)),
            new BlockingCase(
                VerificationLevel.Build,
                VerificationStage.Build,
                VerificationStageEvidence.Failed(VerificationFailureKind.BuildFailed)),
            new BlockingCase(
                VerificationLevel.Test,
                VerificationStage.Test,
                VerificationStageEvidence.Failed(VerificationFailureKind.TestsFailed)),
            new BlockingCase(
                VerificationLevel.Restore,
                VerificationStage.Restore,
                VerificationStageEvidence.Incomplete(VerificationFailureKind.SourceUnavailable)),
            new BlockingCase(
                VerificationLevel.Build,
                VerificationStage.Build,
                VerificationStageEvidence.Incomplete(VerificationFailureKind.TimedOut)),
            new BlockingCase(
                VerificationLevel.Test,
                VerificationStage.Test,
                VerificationStageEvidence.Incomplete(VerificationFailureKind.TestResultsUnavailable)),
        };

        foreach (var item in cases)
        {
            var baseline = Blocking(item.Level, item.Stage, item.Evidence);
            var decision = VerificationVerdictEngine.Compare(new VerificationComparisonInput(
                item.Level,
                baseline,
                Passing(item.Level),
                HasDependencyChange: true));

            Assert.Equal(VerificationVerdict.Incomplete, decision.Verdict);
            Assert.Equal(VerificationSnapshotRole.Baseline, decision.BlockingSnapshot);
            Assert.Equal(item.Stage, decision.BlockingStage);
            Assert.Equal(item.Evidence.FailureKind, decision.FailureKind);
            Assert.False(decision.IsComplete);
            Assert.False(decision.IsAccepted);
        }
    }

    [Fact]
    public void CandidateDeterministicFailureAfterUsableBaselineProducesReject()
    {
        var cases = new[]
        {
            new BlockingCase(
                VerificationLevel.Restore,
                VerificationStage.Restore,
                VerificationStageEvidence.Failed(VerificationFailureKind.DependencyConflict)),
            new BlockingCase(
                VerificationLevel.Build,
                VerificationStage.Build,
                VerificationStageEvidence.Failed(VerificationFailureKind.BuildFailed)),
            new BlockingCase(
                VerificationLevel.Test,
                VerificationStage.Test,
                VerificationStageEvidence.Failed(VerificationFailureKind.TestsFailed)),
        };

        foreach (var item in cases)
        {
            var decision = VerificationVerdictEngine.Compare(new VerificationComparisonInput(
                item.Level,
                Passing(item.Level),
                Blocking(item.Level, item.Stage, item.Evidence),
                HasDependencyChange: true));

            Assert.Equal(VerificationVerdict.Reject, decision.Verdict);
            Assert.Equal(VerificationSnapshotRole.Candidate, decision.BlockingSnapshot);
            Assert.Equal(item.Stage, decision.BlockingStage);
            Assert.Equal(item.Evidence.FailureKind, decision.FailureKind);
            Assert.True(decision.IsComplete);
            Assert.False(decision.IsAccepted);
        }
    }

    [Fact]
    public void CandidateOperationalFailureProducesIncompleteInsteadOfReject()
    {
        var cases = new[]
        {
            new BlockingCase(
                VerificationLevel.Restore,
                VerificationStage.Restore,
                VerificationStageEvidence.Incomplete(VerificationFailureKind.AuthenticationFailed)),
            new BlockingCase(
                VerificationLevel.Build,
                VerificationStage.Build,
                VerificationStageEvidence.Incomplete(VerificationFailureKind.OutputLimitExceeded)),
            new BlockingCase(
                VerificationLevel.Test,
                VerificationStage.Test,
                VerificationStageEvidence.Incomplete(VerificationFailureKind.NoTestsDiscovered)),
        };

        foreach (var item in cases)
        {
            var decision = VerificationVerdictEngine.Compare(new VerificationComparisonInput(
                item.Level,
                Passing(item.Level),
                Blocking(item.Level, item.Stage, item.Evidence),
                HasDependencyChange: true));

            Assert.Equal(VerificationVerdict.Incomplete, decision.Verdict);
            Assert.Equal(VerificationSnapshotRole.Candidate, decision.BlockingSnapshot);
            Assert.Equal(item.Stage, decision.BlockingStage);
            Assert.Equal(item.Evidence.FailureKind, decision.FailureKind);
            Assert.False(decision.IsComplete);
        }
    }

    [Fact]
    public void OperationalComparisonFailureTakesPrecedenceOverCandidateRejectionOrNoChange()
    {
        var rejectedCandidate = Blocking(
            VerificationLevel.Build,
            VerificationStage.Build,
            VerificationStageEvidence.Failed(VerificationFailureKind.BuildFailed));

        var rejected = VerificationVerdictEngine.Compare(new VerificationComparisonInput(
            VerificationLevel.Build,
            Passing(VerificationLevel.Build),
            rejectedCandidate,
            HasDependencyChange: true)
        {
            OperationalFailure = VerificationFailureKind.ComparisonIncomplete,
        });
        var noChange = VerificationVerdictEngine.Compare(new VerificationComparisonInput(
            VerificationLevel.Restore,
            Passing(VerificationLevel.Restore),
            Passing(VerificationLevel.Restore),
            HasDependencyChange: false)
        {
            OperationalFailure = VerificationFailureKind.SnapshotUnavailable,
        });

        Assert.Equal(VerificationVerdict.Incomplete, rejected.Verdict);
        Assert.Null(rejected.BlockingSnapshot);
        Assert.Null(rejected.BlockingStage);
        Assert.Equal(VerificationFailureKind.ComparisonIncomplete, rejected.FailureKind);
        Assert.Equal(VerificationVerdict.Incomplete, noChange.Verdict);
        Assert.Equal(VerificationFailureKind.SnapshotUnavailable, noChange.FailureKind);
    }

    [Fact]
    public void CommonEvidenceIsTheHighestStagePassedByBothSnapshots()
    {
        var candidate = Blocking(
            VerificationLevel.Test,
            VerificationStage.Test,
            VerificationStageEvidence.Failed(VerificationFailureKind.TestsFailed));

        var decision = VerificationVerdictEngine.Compare(new VerificationComparisonInput(
            VerificationLevel.Test,
            Passing(VerificationLevel.Test),
            candidate,
            HasDependencyChange: true));

        Assert.Equal(VerificationEvidenceLevel.TestVerified, decision.BaselineEvidenceLevel);
        Assert.Equal(VerificationEvidenceLevel.BuildVerified, decision.CandidateEvidenceLevel);
        Assert.Equal(VerificationEvidenceLevel.BuildVerified, decision.CommonEvidenceLevel);
    }

    [Fact]
    public void PrerequisiteFailureAllowsLaterStagesToRemainNotRequested()
    {
        var baseline = new VerificationSnapshotEvidence(
            VerificationStageEvidence.Failed(VerificationFailureKind.LockedModeConflict),
            VerificationStageEvidence.NotRequested,
            VerificationStageEvidence.NotRequested);

        var decision = VerificationVerdictEngine.Compare(new VerificationComparisonInput(
            VerificationLevel.Test,
            baseline,
            Passing(VerificationLevel.Test),
            HasDependencyChange: true));

        Assert.Equal(VerificationVerdict.Incomplete, decision.Verdict);
        Assert.Equal(VerificationStage.Restore, decision.BlockingStage);
    }

    [Fact]
    public void EveryFailureKindHasExactlyOneClassification()
    {
        foreach (var failureKind in Enum.GetValues<VerificationFailureKind>())
        {
            var deterministicStages = Enum.GetValues<VerificationStage>()
                .Count(stage => VerificationFailureKinds.IsDeterministicForStage(failureKind, stage));
            var operational = VerificationFailureKinds.IsOperational(failureKind);

            Assert.True(
                deterministicStages == 1 ^ operational,
                $"Failure kind '{failureKind}' must be deterministic for exactly one stage or operational.");
        }
    }

    [Fact]
    public void PassedAndNotRequestedStagesRejectFailureKinds()
    {
        var passed = new VerificationSnapshotEvidence(
            new VerificationStageEvidence(
                VerificationStageStatus.Passed,
                VerificationFailureKind.DependencyConflict),
            VerificationStageEvidence.NotRequested,
            VerificationStageEvidence.NotRequested);
        var notRequested = new VerificationSnapshotEvidence(
            VerificationStageEvidence.Passed,
            new VerificationStageEvidence(
                VerificationStageStatus.NotRequested,
                VerificationFailureKind.BuildFailed),
            VerificationStageEvidence.NotRequested);

        Assert.Throws<ArgumentException>(() => CompareRestore(passed));
        Assert.Throws<ArgumentException>(() => VerificationVerdictEngine.Compare(new VerificationComparisonInput(
            VerificationLevel.Restore,
            notRequested,
            Passing(VerificationLevel.Restore),
            HasDependencyChange: true)));
    }

    [Fact]
    public void FailedAndIncompleteStagesRequireTheCorrectFailureClass()
    {
        var missingFailure = new VerificationSnapshotEvidence(
            new VerificationStageEvidence(VerificationStageStatus.Failed),
            VerificationStageEvidence.NotRequested,
            VerificationStageEvidence.NotRequested);
        var operationalAsFailure = new VerificationSnapshotEvidence(
            VerificationStageEvidence.Failed(VerificationFailureKind.TimedOut),
            VerificationStageEvidence.NotRequested,
            VerificationStageEvidence.NotRequested);
        var deterministicAsIncomplete = new VerificationSnapshotEvidence(
            VerificationStageEvidence.Incomplete(VerificationFailureKind.VersionNotFound),
            VerificationStageEvidence.NotRequested,
            VerificationStageEvidence.NotRequested);
        var wrongStage = new VerificationSnapshotEvidence(
            VerificationStageEvidence.Failed(VerificationFailureKind.BuildFailed),
            VerificationStageEvidence.NotRequested,
            VerificationStageEvidence.NotRequested);

        Assert.Throws<ArgumentException>(() => CompareRestore(missingFailure));
        Assert.Throws<ArgumentException>(() => CompareRestore(operationalAsFailure));
        Assert.Throws<ArgumentException>(() => CompareRestore(deterministicAsIncomplete));
        Assert.Throws<ArgumentException>(() => CompareRestore(wrongStage));
    }

    [Fact]
    public void RequestedStagesCannotDisappearAfterTheirPrerequisitesPass()
    {
        var missingBuild = new VerificationSnapshotEvidence(
            VerificationStageEvidence.Passed,
            VerificationStageEvidence.NotRequested,
            VerificationStageEvidence.NotRequested);
        var missingTest = new VerificationSnapshotEvidence(
            VerificationStageEvidence.Passed,
            VerificationStageEvidence.Passed,
            VerificationStageEvidence.NotRequested);

        Assert.Throws<ArgumentException>(() => VerificationVerdictEngine.Compare(new VerificationComparisonInput(
            VerificationLevel.Build,
            missingBuild,
            Passing(VerificationLevel.Build),
            HasDependencyChange: true)));
        Assert.Throws<ArgumentException>(() => VerificationVerdictEngine.Compare(new VerificationComparisonInput(
            VerificationLevel.Test,
            missingTest,
            Passing(VerificationLevel.Test),
            HasDependencyChange: true)));
    }

    [Fact]
    public void UnrequestedOrBlockedStagesCannotContainExecutionEvidence()
    {
        var unexpectedBuild = new VerificationSnapshotEvidence(
            VerificationStageEvidence.Passed,
            VerificationStageEvidence.Passed,
            VerificationStageEvidence.NotRequested);
        var testAfterBuildFailure = new VerificationSnapshotEvidence(
            VerificationStageEvidence.Passed,
            VerificationStageEvidence.Failed(VerificationFailureKind.BuildFailed),
            VerificationStageEvidence.Passed);

        Assert.Throws<ArgumentException>(() => VerificationVerdictEngine.Compare(new VerificationComparisonInput(
            VerificationLevel.Restore,
            unexpectedBuild,
            Passing(VerificationLevel.Restore),
            HasDependencyChange: true)));
        Assert.Throws<ArgumentException>(() => VerificationVerdictEngine.Compare(new VerificationComparisonInput(
            VerificationLevel.Test,
            testAfterBuildFailure,
            Passing(VerificationLevel.Test),
            HasDependencyChange: true)));
    }

    [Fact]
    public void ExternalFailureMustBeOperational()
    {
        var input = new VerificationComparisonInput(
            VerificationLevel.Restore,
            Passing(VerificationLevel.Restore),
            Passing(VerificationLevel.Restore),
            HasDependencyChange: true)
        {
            OperationalFailure = VerificationFailureKind.VersionNotFound,
        };

        Assert.Throws<ArgumentException>(() => VerificationVerdictEngine.Compare(input));
    }

    [Fact]
    public void InvalidEnumValuesAreRejected()
    {
        var invalidLevel = new VerificationComparisonInput(
            (VerificationLevel)999,
            Passing(VerificationLevel.Restore),
            Passing(VerificationLevel.Restore),
            HasDependencyChange: true);
        var invalidStatus = new VerificationSnapshotEvidence(
            new VerificationStageEvidence((VerificationStageStatus)999),
            VerificationStageEvidence.NotRequested,
            VerificationStageEvidence.NotRequested);
        var invalidFailure = new VerificationSnapshotEvidence(
            VerificationStageEvidence.Incomplete((VerificationFailureKind)999),
            VerificationStageEvidence.NotRequested,
            VerificationStageEvidence.NotRequested);

        Assert.Throws<ArgumentOutOfRangeException>(() => VerificationVerdictEngine.Compare(invalidLevel));
        Assert.Throws<ArgumentOutOfRangeException>(() => CompareRestore(invalidStatus));
        Assert.Throws<ArgumentOutOfRangeException>(() => CompareRestore(invalidFailure));
    }

    private static VerificationDecision CompareRestore(VerificationSnapshotEvidence baseline) =>
        VerificationVerdictEngine.Compare(new VerificationComparisonInput(
            VerificationLevel.Restore,
            baseline,
            Passing(VerificationLevel.Restore),
            HasDependencyChange: true));

    private static VerificationSnapshotEvidence Passing(VerificationLevel level) => level switch
    {
        VerificationLevel.Restore => new VerificationSnapshotEvidence(
            VerificationStageEvidence.Passed,
            VerificationStageEvidence.NotRequested,
            VerificationStageEvidence.NotRequested),
        VerificationLevel.Build => new VerificationSnapshotEvidence(
            VerificationStageEvidence.Passed,
            VerificationStageEvidence.Passed,
            VerificationStageEvidence.NotRequested),
        VerificationLevel.Test => new VerificationSnapshotEvidence(
            VerificationStageEvidence.Passed,
            VerificationStageEvidence.Passed,
            VerificationStageEvidence.Passed),
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };

    private static VerificationSnapshotEvidence Blocking(
        VerificationLevel level,
        VerificationStage stage,
        VerificationStageEvidence evidence) => stage switch
        {
            VerificationStage.Restore => new VerificationSnapshotEvidence(
                evidence,
                VerificationStageEvidence.NotRequested,
                VerificationStageEvidence.NotRequested),
            VerificationStage.Build when level is VerificationLevel.Build or VerificationLevel.Test =>
                new VerificationSnapshotEvidence(
                    VerificationStageEvidence.Passed,
                    evidence,
                    VerificationStageEvidence.NotRequested),
            VerificationStage.Test when level == VerificationLevel.Test => new VerificationSnapshotEvidence(
                VerificationStageEvidence.Passed,
                VerificationStageEvidence.Passed,
                evidence),
            _ => throw new ArgumentException("The blocking stage is not part of the requested level.", nameof(stage)),
        };

    private static IReadOnlyList<OutcomeCase> ValidOutcomes(VerificationLevel level)
    {
        var outcomes = new List<OutcomeCase>
        {
            new(Passing(level), OutcomeKind.Passed, null),
        };

        foreach (var stage in RequestedStages(level))
        {
            var deterministicFailure = stage switch
            {
                VerificationStage.Restore => VerificationFailureKind.VersionNotFound,
                VerificationStage.Build => VerificationFailureKind.BuildFailed,
                VerificationStage.Test => VerificationFailureKind.TestsFailed,
                _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null),
            };
            outcomes.Add(new OutcomeCase(
                Blocking(level, stage, VerificationStageEvidence.Failed(deterministicFailure)),
                OutcomeKind.DeterministicFailure,
                stage));
            outcomes.Add(new OutcomeCase(
                Blocking(level, stage, VerificationStageEvidence.Incomplete(VerificationFailureKind.TimedOut)),
                OutcomeKind.OperationalFailure,
                stage));
        }

        return outcomes;
    }

    private static IEnumerable<VerificationStage> RequestedStages(VerificationLevel level)
    {
        yield return VerificationStage.Restore;
        if (level is VerificationLevel.Build or VerificationLevel.Test)
        {
            yield return VerificationStage.Build;
        }

        if (level == VerificationLevel.Test)
        {
            yield return VerificationStage.Test;
        }
    }

    private sealed record PassingCase(
        VerificationLevel Level,
        VerificationEvidenceLevel EvidenceLevel);

    private sealed record BlockingCase(
        VerificationLevel Level,
        VerificationStage Stage,
        VerificationStageEvidence Evidence);

    private sealed record OutcomeCase(
        VerificationSnapshotEvidence Evidence,
        OutcomeKind Kind,
        VerificationStage? Stage);

    private enum OutcomeKind
    {
        Passed,
        DeterministicFailure,
        OperationalFailure,
    }
}
