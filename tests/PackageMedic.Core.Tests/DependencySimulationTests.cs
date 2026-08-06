using System.Text.Json;

namespace PackageMedic.Core.Tests;

public sealed class DependencySimulationTests
{
    [Fact]
    public async Task WorkingTreeInspectorUsesBoundedPorcelainStatusWithoutOptionalLocks()
    {
        var root = Directory.CreateTempSubdirectory("PackageMedic.CleanWorktree.");
        try
        {
            var runner = new RecordingProcessRunner(new ProcessResult(0, string.Empty, string.Empty));

            await new GitWorkingTreeInspector(runner).EnsureCleanAsync(
                root.FullName,
                TestContext.Current.CancellationToken);

            var call = Assert.Single(runner.Calls);
            Assert.Equal("git", call.FileName);
            Assert.Equal(
                ["--no-optional-locks", "status", "--porcelain=v1", "-z", "--untracked-files=all"],
                call.Arguments);
            Assert.Equal(root.FullName, call.WorkingDirectory);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WorkingTreeInspectorRejectsDirtyOutputWithoutEchoingRepositoryPaths()
    {
        var root = Directory.CreateTempSubdirectory("PackageMedic.DirtyWorktree.");
        try
        {
            const string hostilePath = " M token=secret-private.csproj\0";
            var runner = new RecordingProcessRunner(new ProcessResult(0, hostilePath, string.Empty));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new GitWorkingTreeInspector(runner).EnsureCleanAsync(
                    root.FullName,
                    TestContext.Current.CancellationToken));

            Assert.Contains("clean Git worktree", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret-private", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("token=", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WorkingTreeInspectorRejectsOversizedAndTruncatedStatusOutput()
    {
        var root = Directory.CreateTempSubdirectory("PackageMedic.StatusLimits.");
        try
        {
            var oversized = new RecordingProcessRunner(new ProcessResult(
                0,
                new string('x', GitWorkingTreeInspector.MaximumStatusOutputCharacters + 1),
                string.Empty));
            var oversizedError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new GitWorkingTreeInspector(oversized).EnsureCleanAsync(
                    root.FullName,
                    TestContext.Current.CancellationToken));
            Assert.Contains("safety limit", oversizedError.Message, StringComparison.OrdinalIgnoreCase);

            using var source = new StringReader(new string('x', 64));
            var truncatedText = await ProcessRunner.ReadBoundedAsync(
                source,
                8,
                TestContext.Current.CancellationToken);
            var truncated = new RecordingProcessRunner(new ProcessResult(0, truncatedText, string.Empty));
            var truncatedError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new GitWorkingTreeInspector(truncated).EnsureCleanAsync(
                    root.FullName,
                    TestContext.Current.CancellationToken));
            Assert.Contains("safety limit", truncatedError.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WorkingTreeInspectorRedactsGitFailuresAndHonorsItsTimeout()
    {
        var root = Directory.CreateTempSubdirectory("PackageMedic.StatusFailure.");
        try
        {
            var failed = new RecordingProcessRunner(new ProcessResult(
                128,
                string.Empty,
                "fatal: https://user:password@example.test token=private"));
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new GitWorkingTreeInspector(failed).EnsureCleanAsync(
                    root.FullName,
                    TestContext.Current.CancellationToken));
            Assert.Contains("[REDACTED]", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("password", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("private", failure.Message, StringComparison.Ordinal);

            var timeout = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new GitWorkingTreeInspector(new NeverCompletesProcessRunner(), TimeSpan.FromMilliseconds(20))
                    .EnsureCleanAsync(root.FullName, TestContext.Current.CancellationToken));
            Assert.Contains("timed out", timeout.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WorkingTreeInspectorRejectsArchiveTransformingAttributes()
    {
        var root = Directory.CreateTempSubdirectory("PackageMedic.ExportAttributes.");
        try
        {
            var rejected = new RecordingProcessRunner(new ProcessResult(
                0,
                ".gitattributes:1:artifacts/** export-ignore",
                string.Empty));
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new GitWorkingTreeInspector(rejected).EnsureArchiveSemanticsAreReproducibleAsync(
                    root.FullName,
                    TestContext.Current.CancellationToken));

            Assert.Contains("export-ignore", exception.Message, StringComparison.OrdinalIgnoreCase);

            var accepted = new RecordingProcessRunner(new ProcessResult(1, string.Empty, string.Empty));
            await new GitWorkingTreeInspector(accepted).EnsureArchiveSemanticsAreReproducibleAsync(
                root.FullName,
                "refs/remotes/origin/main",
                TestContext.Current.CancellationToken);
            Assert.Equal(2, accepted.Calls.Count);
            Assert.Contains("refs/remotes/origin/main", accepted.Calls[0].Arguments);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WorkingTreeInspectorRejectsRepositoryLocalArchiveAttributes()
    {
        var root = Directory.CreateTempSubdirectory("PackageMedic.InfoAttributes.");
        try
        {
            var info = Directory.CreateDirectory(Path.Combine(root.FullName, ".git", "info"));
            File.WriteAllText(Path.Combine(info.FullName, "attributes"), "artifacts/** export-ignore");
            var runner = new RecordingProcessRunner(new ProcessResult(1, string.Empty, string.Empty));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new GitWorkingTreeInspector(runner).EnsureArchiveSemanticsAreReproducibleAsync(
                    root.FullName,
                    TestContext.Current.CancellationToken));

            Assert.Contains(".git/info/attributes", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void SimulationContractIsDeterministicPortableAndExplicitlyRestoreOnly()
    {
        var report = CreateReport([
            "projects/Zeta/Zeta.csproj",
            "projects/Alpha/Alpha.csproj",
            "projects/Alpha/Alpha.csproj",
        ]);

        var first = DependencySimulationSerializer.SerializeJson(report);
        var second = DependencySimulationSerializer.SerializeJson(report);

        Assert.Equal(first, second);
        Assert.DoesNotContain(Path.GetTempPath(), first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token=private", first, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(first);
        var root = json.RootElement;
        Assert.Equal(DependencySimulationReport.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("dependencySimulation", root.GetProperty("kind").GetString());
        Assert.Equal(new string('a', 40), root.GetProperty("repository").GetProperty("headCommit").GetString());
        Assert.Equal("src/App/App.csproj", root.GetProperty("repository").GetProperty("analysisTarget").GetString());
        Assert.True(root.GetProperty("repository").GetProperty("workingTreeRequiredClean").GetBoolean());
        Assert.False(root.GetProperty("request").TryGetProperty("target", out _));
        Assert.Equal("restoreOnly", root.GetProperty("verification").GetProperty("evidenceLevel").GetString());
        Assert.Equal("notRun", root.GetProperty("verification").GetProperty("build").GetString());
        Assert.Equal("notRun", root.GetProperty("verification").GetProperty("tests").GetString());
        Assert.Equal(
            "notVerified",
            root.GetProperty("verification").GetProperty("runtimeCompatibility").GetString());
        Assert.Equal("enforced", root.GetProperty("verification").GetProperty("lockedMode").GetString());
        Assert.False(root.GetProperty("verification").TryGetProperty("restoreFailureKind", out _));
        Assert.True(root.GetProperty("verification").GetProperty("auditedVulnerabilities").GetBoolean());
        Assert.False(root.GetProperty("verification").GetProperty("auditedDeprecations").GetBoolean());
        Assert.True(root.GetProperty("comparison").GetProperty("isComplete").GetBoolean());
        Assert.True(root.GetProperty("isComplete").GetBoolean());
        Assert.Equal(
            ["projects/Alpha/Alpha.csproj", "projects/Zeta/Zeta.csproj"],
            root.GetProperty("mutation").GetProperty("affectedProjects")
                .EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.Empty(root.GetProperty("errors").EnumerateArray());
        Assert.Empty(root.GetProperty("rejectionReasons").EnumerateArray());

        var text = DependencySimulationSerializer.SerializeText(report);
        Assert.Contains("Evidence: restore only", text, StringComparison.Ordinal);
        Assert.Contains("Build: not run | Tests: not run", text, StringComparison.Ordinal);
        Assert.Contains("Verdict: pass", text, StringComparison.Ordinal);
        Assert.Contains("runtime behavior were not verified", text, StringComparison.Ordinal);
        Assert.Contains("repository-controlled logic", text, StringComparison.Ordinal);
        Assert.Contains("configured NuGet feeds", text, StringComparison.Ordinal);
        Assert.DoesNotContain("safe", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("is compatible", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetTempPath(), text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SimulationContractSeparatesCandidateRejectionFromOperationalErrors()
    {
        var rejected = CreateReport(["src/App/App.csproj"]) with
        {
            Verification = DependencySimulationVerification.RestoreOnly(
                DependencySimulationVerificationStatus.Failed,
                auditedVulnerabilities: false,
                auditedDeprecations: false,
                DependencySimulationLockedMode.NotEnabled,
                restoreFailureKind: DependencySimulationRestoreFailureKind.VersionNotFound),
            Comparison = CreateReport(["src/App/App.csproj"]).Comparison with
            {
                IsComplete = false,
                UnavailableReason = "Candidate restore failed before a graph was produced.",
            },
            Verdict = DependencySimulationVerdict.Reject,
            RejectionReasons = ["The requested package version was not found."],
            Errors = [],
        };

        var json = DependencySimulationSerializer.SerializeJson(rejected);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("reject", root.GetProperty("verdict").GetString());
        Assert.True(root.GetProperty("isComplete").GetBoolean());
        Assert.Equal(
            "versionNotFound",
            root.GetProperty("verification").GetProperty("restoreFailureKind").GetString());
        Assert.False(root.GetProperty("comparison").GetProperty("isComplete").GetBoolean());
        Assert.Single(root.GetProperty("rejectionReasons").EnumerateArray());
        Assert.Empty(root.GetProperty("errors").EnumerateArray());

        var text = DependencySimulationSerializer.SerializeText(rejected);
        Assert.Contains("[candidate rejected]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[simulation incomplete]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishedSimulationSchemaMatchesTheSerializedRequiredContract()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PackageMedic.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        using var schema = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(directory!.FullName, "schemas", "packagemedic-simulation.schema.json")));
        using var report = JsonDocument.Parse(DependencySimulationSerializer.SerializeJson(
            CreateReport(["src/App/App.csproj"])));

        var required = schema.RootElement.GetProperty("required")
            .EnumerateArray().Select(item => item.GetString()!).Order().ToArray();
        var serialized = report.RootElement.EnumerateObject()
            .Select(property => property.Name).Order().ToArray();
        Assert.Equal(required, serialized);

        var lockedModes = schema.RootElement.GetProperty("properties")
            .GetProperty("verification").GetProperty("properties")
            .GetProperty("lockedMode").GetProperty("enum")
            .EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Equal(["notEnabled", "enforced", "mixed"], lockedModes);
        Assert.DoesNotContain("temporarilyRelaxed", lockedModes);
    }

    [Fact]
    public void PublishedSimulationSchemaMatchesStructuredVerificationContract()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PackageMedic.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        using var schema = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(directory!.FullName, "schemas", "packagemedic-simulation.schema.json")));

        var restore = VerificationStageEvidence.Passed;
        var build = new VerificationBuildReport(
            VerificationStageEvidence.Passed,
            PlannedTargets: 1,
            CompletedTargets: 1);
        var tests = new VerificationTestReport(
            VerificationStageEvidence.Passed,
            PlannedProjects: 1,
            CompletedProjects: 1,
            Total: 3,
            Passed: 2,
            Failed: 0,
            Skipped: 1,
            FailedTestIdentities: []);
        var snapshot = new VerificationSnapshotReport(restore, build, tests);
        var decision = VerificationVerdictEngine.Compare(new VerificationComparisonInput(
            VerificationLevel.Test,
            new VerificationSnapshotEvidence(restore, build.Stage, tests.Stage),
            new VerificationSnapshotEvidence(restore, build.Stage, tests.Stage),
            HasDependencyChange: true));
        var executed = new VerificationComparisonReport(
            VerificationLevel.Test,
            snapshot,
            snapshot,
            decision);
        var original = CreateReport(["src/App/App.csproj"]);
        var report = original with
        {
            Verification = original.Verification with
            {
                Build = DependencySimulationVerificationStatus.Passed,
                Tests = DependencySimulationVerificationStatus.Passed,
                EvidenceLevel = DependencySimulationEvidenceLevel.TestVerified,
                RequestedLevel = VerificationLevel.Test,
                Executed = executed,
            },
        };

        using var serialized = JsonDocument.Parse(DependencySimulationSerializer.SerializeJson(report));
        var definitions = schema.RootElement.GetProperty("$defs");
        var serializedVerification = serialized.RootElement.GetProperty("verification");
        var serializedExecuted = serializedVerification.GetProperty("executed");

        Assert.Equal(2, schema.RootElement.GetProperty("properties")
            .GetProperty("schemaVersion").GetProperty("const").GetInt32());
        Assert.Equal("testVerified", serializedVerification.GetProperty("evidenceLevel").GetString());
        AssertRequiredPropertiesMatch(
            schema.RootElement.GetProperty("properties").GetProperty("verification"),
            serializedVerification);
        AssertRequiredPropertiesMatch(
            definitions.GetProperty("verificationComparison"),
            serializedExecuted);
        AssertRequiredPropertiesMatch(
            definitions.GetProperty("verificationSnapshot"),
            serializedExecuted.GetProperty("candidate"));
        AssertRequiredPropertiesMatch(
            definitions.GetProperty("verificationBuildReport"),
            serializedExecuted.GetProperty("candidate").GetProperty("build"));
        AssertRequiredPropertiesMatch(
            definitions.GetProperty("verificationTestReport"),
            serializedExecuted.GetProperty("candidate").GetProperty("tests"));
        AssertRequiredPropertiesMatch(
            definitions.GetProperty("verificationDecision"),
            serializedExecuted.GetProperty("decision"));
        AssertRequiredPropertiesMatch(
            definitions.GetProperty("stageEvidence"),
            serializedExecuted.GetProperty("candidate").GetProperty("restore"));
        Assert.Equal("pass", serializedExecuted.GetProperty("decision").GetProperty("verdict").GetString());
        Assert.True(serializedExecuted.GetProperty("decision").GetProperty("isComplete").GetBoolean());
        Assert.True(serializedExecuted.GetProperty("decision").GetProperty("isAccepted").GetBoolean());
    }

    [Fact]
    public void SimulationContractRequiresBothSourceHashes()
    {
        var report = CreateReport(["src/App/App.csproj"]);

        Assert.Throws<InvalidDataException>(() => DependencySimulationSerializer.SerializeJson(
            report with { Mutation = report.Mutation with { SourceSha256Before = null } }));
        Assert.Throws<InvalidDataException>(() => DependencySimulationSerializer.SerializeJson(
            report with { Mutation = report.Mutation with { SourceSha256After = string.Empty } }));
    }

    [Fact]
    public void SimulationContractDistinguishesNoChangeFromIncompleteExecution()
    {
        var noChange = CreateReport(["src/App/App.csproj"]) with
        {
            Mutation = CreateReport(["src/App/App.csproj"]).Mutation with { NoChange = true },
            Verdict = DependencySimulationVerdict.NoChange,
        };
        using var noChangeJson = JsonDocument.Parse(DependencySimulationSerializer.SerializeJson(noChange));
        Assert.Equal("noChange", noChangeJson.RootElement.GetProperty("verdict").GetString());

        var incomplete = CreateReport(["src/App/App.csproj"]) with
        {
            Verification = DependencySimulationVerification.RestoreOnly(
                DependencySimulationVerificationStatus.NotRun,
                auditedVulnerabilities: false,
                auditedDeprecations: false,
                DependencySimulationLockedMode.NotEnabled),
            Mutation = CreateReport(["src/App/App.csproj"]).Mutation with { NoChange = true },
            Comparison = CreateReport(["src/App/App.csproj"]).Comparison with
            {
                IsComplete = false,
                UnavailableReason = "Snapshot materialization did not complete.",
            },
            Verdict = DependencySimulationVerdict.Incomplete,
            Errors = ["token=private"],
        };
        using var incompleteJson = JsonDocument.Parse(DependencySimulationSerializer.SerializeJson(incomplete));
        var root = incompleteJson.RootElement;
        Assert.Equal("incomplete", root.GetProperty("verdict").GetString());
        Assert.False(root.GetProperty("isComplete").GetBoolean());
        Assert.Empty(root.GetProperty("rejectionReasons").EnumerateArray());
        Assert.Equal("token=[REDACTED]", root.GetProperty("errors")[0].GetString());
        var text = DependencySimulationSerializer.SerializeText(incomplete);
        Assert.Contains("[simulation incomplete]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[candidate rejected]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SimulationSerializerNormalizesSourceHashesToSchemaCasing()
    {
        var report = CreateReport(["src/App/App.csproj"]);
        var upper = report with
        {
            Mutation = report.Mutation with
            {
                SourceSha256Before = new string('A', 64),
                SourceSha256After = new string('B', 64),
            },
        };

        using var json = JsonDocument.Parse(DependencySimulationSerializer.SerializeJson(upper));
        Assert.Equal(
            new string('a', 64),
            json.RootElement.GetProperty("mutation").GetProperty("sourceSha256Before").GetString());
        Assert.Equal(
            new string('b', 64),
            json.RootElement.GetProperty("mutation").GetProperty("sourceSha256After").GetString());
    }

    [Fact]
    public void SimulationSerializerRejectsTemporaryOrParentTraversingPaths()
    {
        var rooted = CreateReport(["src/App/App.csproj"]) with
        {
            Repository = CreateReport(["src/App/App.csproj"]).Repository with
            {
                AnalysisTarget = Path.Combine(Path.GetTempPath(), "snapshot", "App.csproj"),
            },
        };
        var traversal = CreateReport(["src/App/App.csproj"]) with
        {
            Mutation = CreateReport(["src/App/App.csproj"]).Mutation with
            {
                File = "../Directory.Packages.props",
            },
        };

        Assert.Throws<InvalidDataException>(() => DependencySimulationSerializer.SerializeJson(rooted));
        Assert.Throws<InvalidDataException>(() => DependencySimulationSerializer.SerializeJson(traversal));
    }

    [Fact]
    public void SimulationMutationAdaptsThePackageEditorResult()
    {
        var edit = new PackageVersionEditResult(
            "Example.Package",
            "Directory.Packages.props",
            12,
            PackageVersionDeclarationKind.CentralPackageVersion,
            "1.0.0",
            "2.0.0",
            ["src/App/App.csproj"])
        {
            SourceSha256Before = new string('a', 64),
            SourceSha256After = new string('b', 64),
        };

        var mutation = DependencySimulationMutation.From(edit);

        Assert.Equal(edit.PackageId, mutation.PackageId);
        Assert.Equal(edit.File, mutation.File);
        Assert.Equal(edit.AffectedProjects, mutation.AffectedProjects);
        Assert.Equal(edit.SourceSha256Before, mutation.SourceSha256Before);
        Assert.Equal(edit.SourceSha256After, mutation.SourceSha256After);
    }

    private static DependencySimulationReport CreateReport(IReadOnlyList<string> affectedProjects) => new(
        DependencySimulationReport.CurrentSchemaVersion,
        "0.5.0",
        new DependencySimulationRepository(
            new string('A', 40),
            "src/App/App.csproj",
            WorkingTreeRequiredClean: true),
        new DependencySimulationRequest("Example.Package", "2.0.0"),
        new DependencySimulationMutation(
            "Example.Package",
            "Directory.Packages.props",
            12,
            PackageVersionDeclarationKind.CentralPackageVersion,
            "1.0.0",
            "2.0.0",
            affectedProjects)
        {
            SourceSha256Before = new string('1', 64),
            SourceSha256After = new string('2', 64),
        },
        DependencySimulationVerification.RestoreOnly(
            DependencySimulationVerificationStatus.Passed,
            auditedVulnerabilities: true,
            auditedDeprecations: false,
            DependencySimulationLockedMode.Enforced),
        new DependencySimulationComparison(
            new AnalysisDiffSummary(0, 1, 0),
            [],
            new PackageDiffSummary(1, 0, 2, 0, 0, 0, 0, 0),
            [],
            new DependencyRiskDiffSummary(0, 1, 0, 0),
            [],
            null),
        DependencySimulationVerdict.Pass,
        [],
        []);

    private static void AssertRequiredPropertiesMatch(JsonElement contract, JsonElement value)
    {
        var required = contract.GetProperty("required")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var serializedRequired = value.EnumerateObject()
            .Select(property => property.Name)
            .Where(required.Contains)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var allowed = contract.GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(required, serializedRequired);
        Assert.All(value.EnumerateObject(), property => Assert.Contains(property.Name, allowed));
    }

    private sealed class RecordingProcessRunner(ProcessResult result) : IProcessRunner
    {
        public List<ProcessCall> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            Calls.Add(new ProcessCall(fileName, arguments.ToArray(), workingDirectory));
            if (arguments.Contains("--git-path", StringComparer.Ordinal))
            {
                return Task.FromResult(new ProcessResult(
                    0,
                    Path.Combine(workingDirectory, ".git", "info", "attributes") + Environment.NewLine,
                    string.Empty));
            }

            return Task.FromResult(result);
        }
    }

    private sealed class NeverCompletesProcessRunner : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed record ProcessCall(
        string FileName,
        IReadOnlyList<string> Arguments,
        string WorkingDirectory);
}
