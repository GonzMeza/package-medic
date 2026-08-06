using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PackageMedic.Cli;
using PackageMedic.Core;

namespace PackageMedic.IntegrationTests;

public sealed class CliIntegrationTests
{
    [Fact]
    public void VerificationOptionsAreExplicitOrderedAndScopedToDiffOrSimulation()
    {
        var diff = CliOptions.Parse([
            "diff", "HEAD~1", ".", "--verify", "build", "--build-timeout", "600",
            "--verification-configuration", "Continuous_Integration",
        ]);
        var simulation = CliOptions.Parse([
            "simulate", "Example.Package", "--to", "2.0.0", "--verify=test",
            "--build-timeout=600", "--test-timeout", "1200",
        ]);

        Assert.Equal(VerificationLevel.Build, diff.Verify);
        Assert.Equal(600, diff.BuildTimeoutSeconds);
        Assert.Equal("Continuous_Integration", diff.VerificationConfiguration);
        Assert.Equal(VerificationLevel.Test, simulation.Verify);
        Assert.Equal(1200, simulation.TestTimeoutSeconds);
        Assert.Throws<UsageException>(() => CliOptions.Parse(["doctor", ".", "--verify", "build"]));
        Assert.Throws<UsageException>(() => CliOptions.Parse([
            "diff", "HEAD", ".", "--verify", "build", "--no-restore",
        ]));
        Assert.Throws<UsageException>(() => CliOptions.Parse([
            "diff", "HEAD", ".", "--verification-configuration", "Release",
        ]));
        Assert.Throws<UsageException>(() => CliOptions.Parse([
            "diff", "HEAD", ".", "--verify", "restore", "--build-timeout", "60",
        ]));
        Assert.Throws<UsageException>(() => CliOptions.Parse([
            "simulate", "Example.Package", "--to", "2.0.0", "--verify", "build",
            "--test-timeout", "60",
        ]));
        Assert.Throws<UsageException>(() => CliOptions.Parse([
            "diff", "HEAD", ".", "--verify", "build", "--verify", "test",
        ]));
        var provenance = CliOptions.Parse([
            "diff", "HEAD~1", ".", "--verify", "test", "--provenance-output", "evidence.intoto.json",
        ]);
        Assert.Equal("evidence.intoto.json", provenance.ProvenanceOutputPath);
        Assert.Throws<UsageException>(() => CliOptions.Parse([
            "diff", "HEAD", ".", "--provenance-output", "evidence.intoto.json",
        ]));
        Assert.Throws<UsageException>(() => CliOptions.Parse([
            "doctor", ".", "--provenance-output", "evidence.intoto.json",
        ]));
    }

    [Fact]
    public void ParsesCycloneDxOutputForSupportedAnalysisCommandsOnly()
    {
        var doctor = CliOptions.Parse(["doctor", ".", "--sbom-output", "artifacts/bom.cdx.json"]);
        var diff = CliOptions.Parse(["diff", "HEAD", ".", "--sbom-output=artifacts/diff.cdx.json"]);
        var sbom = CliOptions.Parse(["sbom", ".", "--output", "artifacts/standalone.cdx.json"]);

        Assert.Equal("artifacts/bom.cdx.json", doctor.SbomOutputPath);
        Assert.Equal("artifacts/diff.cdx.json", diff.SbomOutputPath);
        Assert.Equal(CliCommand.Sbom, sbom.Command);
        Assert.Null(sbom.OutputPath);
        Assert.Equal("artifacts/standalone.cdx.json", sbom.SbomOutputPath);
        Assert.Throws<UsageException>(() => CliOptions.Parse(["sbom", "."]));
        Assert.Throws<UsageException>(() => CliOptions.Parse([
            "simulate", "Example.Package", "--to", "2.0.0", "--sbom-output", "candidate.cdx.json",
        ]));
    }

    [Fact]
    public void SimulationOptionsRequireAnExactCandidateAndRejectIncompatibleModes()
    {
        var parsed = CliOptions.Parse([
            "simulate", "Example.Package", "src/App.csproj", "--to", "2.0.0",
            "--audit", "--deprecated", "--format", "json",
            "--credential-env", "CONTOSO_FEED_TOKEN",
            "--credential-env", "VSS_NUGET_EXTERNAL_FEED_ENDPOINTS",
        ]);

        Assert.Equal(CliCommand.Simulate, parsed.Command);
        Assert.Equal("Example.Package", parsed.SimulationPackageId);
        Assert.Equal("2.0.0", parsed.SimulationTargetVersion);
        Assert.Equal("src/App.csproj", parsed.Path);
        Assert.True(parsed.AuditVulnerabilities);
        Assert.True(parsed.AuditDeprecatedPackages);
        Assert.Equal(
            ["CONTOSO_FEED_TOKEN", "VSS_NUGET_EXTERNAL_FEED_ENDPOINTS"],
            parsed.SimulationCredentialEnvironmentVariables);
        Assert.Throws<UsageException>(() => CliOptions.Parse(["simulate", "Example.Package"]));
        Assert.Throws<UsageException>(() => CliOptions.Parse([
            "simulate", "Example.Package", "--to", "2.0.0", "--no-restore",
        ]));
        Assert.Throws<UsageException>(() => CliOptions.Parse([
            "simulate", "Example.Package", "--to", "2.0.0", "--format", "sarif",
        ]));
        Assert.Throws<UsageException>(() => CliOptions.Parse([
            "simulate", "Example.Package", "--to", "2.0.0",
            "--credential-env", "TOKEN=unsafe",
        ]));
        Assert.Throws<UsageException>(() => CliOptions.Parse([
            "simulate", "Example.Package", "--to", "2.0.0",
            "--credential-env", "TOKEN", "--credential-env", "token",
        ]));
    }

    [Fact]
    public void DeprecationAndTransitiveAuditOptionsPreserveExplicitIntent()
    {
        var deprecated = CliOptions.Parse(["doctor", ".", "--deprecated", "--include-transitive"]);
        var legacyShorthand = CliOptions.Parse(["doctor", ".", "--include-transitive"]);
        var combined = CliOptions.Parse(["audit", ".", "--deprecated"]);
        var split = CliOptions.Parse([
            "audit", ".", "--include-transitive-audit", "--deprecated",
        ]);

        Assert.True(deprecated.AuditDeprecatedPackages);
        Assert.True(deprecated.IncludeTransitiveDeprecatedPackages);
        Assert.False(deprecated.IncludeTransitive);
        Assert.False(deprecated.AuditVulnerabilities);
        Assert.True(legacyShorthand.AuditVulnerabilities);
        Assert.True(legacyShorthand.IncludeTransitive);
        Assert.True(combined.AuditVulnerabilities);
        Assert.True(combined.AuditDeprecatedPackages);
        Assert.True(split.IncludeTransitive);
        Assert.False(split.IncludeTransitiveDeprecatedPackages);
    }

    [Fact]
    public void SimulationRestoreClassificationKeepsOperationalFailuresIncomplete()
    {
        var mixedAuthentication = new RestoreExecutionResult(
            [RestoreDiagnostic("NU1102")],
            ["The source returned 401 Unauthorized."],
            RestoreProcessFailureKind.Rejected);
        var unavailable = new RestoreExecutionResult(
            [RestoreDiagnostic("NU1301")],
            ["NU1301: The service index could not be loaded."],
            RestoreProcessFailureKind.Rejected);
        var unknown = new RestoreExecutionResult(
            [],
            ["A repository-defined restore target failed."],
            RestoreProcessFailureKind.Rejected);
        var missingVersion = new RestoreExecutionResult(
            [RestoreDiagnostic("NU1102")],
            ["NU1102: The requested version is absent."],
            RestoreProcessFailureKind.Rejected);

        var authenticationKind = Program.ClassifySimulationRestoreFailure(
            mixedAuthentication,
            DependencySimulationLockedMode.NotEnabled);
        var unavailableKind = Program.ClassifySimulationRestoreFailure(
            unavailable,
            DependencySimulationLockedMode.NotEnabled);
        var unknownKind = Program.ClassifySimulationRestoreFailure(
            unknown,
            DependencySimulationLockedMode.NotEnabled);
        var missingVersionKind = Program.ClassifySimulationRestoreFailure(
            missingVersion,
            DependencySimulationLockedMode.NotEnabled);

        Assert.Equal(DependencySimulationRestoreFailureKind.AuthenticationFailed, authenticationKind);
        Assert.Equal(DependencySimulationRestoreFailureKind.SourceUnavailable, unavailableKind);
        Assert.Equal(DependencySimulationRestoreFailureKind.Unknown, unknownKind);
        Assert.False(Program.IsDeterministicCandidateRejection(authenticationKind));
        Assert.False(Program.IsDeterministicCandidateRejection(unavailableKind));
        Assert.False(Program.IsDeterministicCandidateRejection(unknownKind));
        Assert.True(Program.IsDeterministicCandidateRejection(missingVersionKind));
    }

    [Fact]
    public void SimulationReportsLockedModeOnlyForAffectedProjects()
    {
        var root = Path.Combine(Path.GetTempPath(), "packagemedic-lock-state");
        var settings = new[]
        {
            new ProjectPackageSettings(Path.Combine(root, "src", "App.csproj"), false, false)
            {
                RestoreLockedMode = false,
            },
            new ProjectPackageSettings(Path.Combine(root, "src", "Locked.csproj"), false, false)
            {
                RestoreLockedMode = true,
            },
        };

        Assert.Equal(
            DependencySimulationLockedMode.NotEnabled,
            Program.ResolveSimulationLockedMode(settings, root, ["src/App.csproj"]));
        Assert.Equal(
            DependencySimulationLockedMode.Enforced,
            Program.ResolveSimulationLockedMode(settings, root, ["src/Locked.csproj"]));
        Assert.Equal(
            DependencySimulationLockedMode.Mixed,
            Program.ResolveSimulationLockedMode(settings, root, ["src/App.csproj", "src/Locked.csproj"]));
    }

    [Fact]
    public async Task ParallelismOptionIsValidatedAndAccepted()
    {
        var rejected = await RunAsync(
            "doctor", Fixture("clean"), "--no-restore", "--max-parallelism", "0", "--verbosity", "quiet");
        var accepted = await RunAsync(
            "doctor", Fixture("clean"), "--no-restore", "--max-parallelism", "2", "--verbosity", "quiet");

        Assert.Equal(2, rejected.ExitCode);
        Assert.Contains("between 1 and 32", rejected.Error, StringComparison.Ordinal);
        Assert.Equal(0, accepted.ExitCode);
    }

    [Fact]
    public async Task VersionCommandSucceeds()
    {
        var result = await RunAsync("--version");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(PackageMedicAnalyzer.Version, result.Output.Trim());
    }

    [Fact]
    public async Task CleanSlnxProducesDeterministicValidJson()
    {
        var target = Fixture("clean", "Clean.slnx");

        var first = await RunAsync("doctor", target, "--no-restore", "--format", "json", "--verbosity", "quiet");
        var second = await RunAsync("doctor", target, "--no-restore", "--format=json", "--verbosity=quiet");

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(first.Output, second.Output);
        using var json = JsonDocument.Parse(first.Output);
        Assert.Equal(1, json.RootElement.GetProperty("summary").GetProperty("projects").GetInt32());
        Assert.Empty(json.RootElement.GetProperty("diagnostics").EnumerateArray());
    }

    [Fact]
    public async Task WarningThresholdControlsExitCode()
    {
        var target = Fixture("unused-central");

        var defaultThreshold = await RunAsync("doctor", target, "--no-restore", "--verbosity", "quiet");
        var errorThreshold = await RunAsync("doctor", target, "--no-restore", "--fail-on", "error", "--verbosity", "quiet");
        var disabledThreshold = await RunAsync("doctor", target, "--no-restore", "--fail-on", "none", "--verbosity", "quiet");

        Assert.Equal(1, defaultThreshold.ExitCode);
        Assert.Equal(0, errorThreshold.ExitCode);
        Assert.Equal(0, disabledThreshold.ExitCode);
    }

    [Theory]
    [InlineData("version-drift", "PM002")]
    [InlineData("cpm-bypass", "PM003")]
    [InlineData("duplicate-central", "PM004")]
    public async Task FixtureEmitsExpectedDiagnostic(string fixture, string code)
    {
        var result = await RunAsync(
            "doctor",
            Fixture(fixture),
            "--no-restore",
            "--format",
            "json",
            "--fail-on",
            "none",
            "--verbosity",
            "quiet");

        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Contains(json.RootElement.GetProperty("diagnostics").EnumerateArray(), item => item.GetProperty("code").GetString() == code);
    }

    [Theory]
    [InlineData("shared-package")]
    [InlineData("transitive-pinning")]
    [InlineData("multi-target")]
    public async Task FalsePositiveFixturesStayClean(string fixture)
    {
        var result = await RunAsync("doctor", Fixture(fixture), "--no-restore", "--verbosity", "quiet");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("0 warnings", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingAssetsWithNoRestoreIsOperationalError()
    {
        var result = await RunAsync("doctor", Fixture("missing-assets"), "--no-restore", "--verbosity", "quiet");

        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task SarifOutputIsValidAndPreservesExitThresholdBehavior()
    {
        var target = Fixture("unused-central");

        var reportOnly = await RunAsync(
            "doctor",
            target,
            "--no-restore",
            "--format",
            "sarif",
            "--fail-on",
            "none",
            "--verbosity",
            "quiet");
        var gated = await RunAsync(
            "doctor",
            target,
            "--no-restore",
            "--format=sarif",
            "--verbosity=quiet");

        Assert.Equal(0, reportOnly.ExitCode);
        Assert.Equal(1, gated.ExitCode);
        using var sarif = JsonDocument.Parse(reportOnly.Output);
        Assert.Equal("2.1.0", sarif.RootElement.GetProperty("version").GetString());
        var run = Assert.Single(sarif.RootElement.GetProperty("runs").EnumerateArray());
        Assert.Contains(
            run.GetProperty("results").EnumerateArray(),
            item => item.GetProperty("ruleId").GetString() == "PM001");
    }

    [Theory]
    [InlineData("--output")]
    [InlineData("-o")]
    public async Task OutputOptionWritesReportAndLeavesStandardOutputClean(string option)
    {
        var directory = Directory.CreateTempSubdirectory("PackageMedic.Output.");
        try
        {
            var reportPath = Path.Combine(directory.FullName, "nested", "report.json");
            var result = await RunAsync(
                "doctor",
                Fixture("clean", "Clean.slnx"),
                "--no-restore",
                "--format",
                "json",
                option,
                reportPath,
                "--fail-on",
                "none",
                "--verbosity",
                "quiet");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.Output);
            Assert.Equal(string.Empty, result.Error);
            Assert.True(File.Exists(reportPath));
            using var report = JsonDocument.Parse(
                await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken));
            Assert.Equal(PackageMedicAnalyzer.Version, report.RootElement.GetProperty("version").GetString());
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(reportPath)!, "*.tmp"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task OutputOptionAtomicallyReplacesAnExistingReport()
    {
        var reportPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(reportPath, "stale", TestContext.Current.CancellationToken);
            var result = await RunAsync(
                "doctor",
                Fixture("clean", "Clean.slnx"),
                "--no-restore",
                "--format=json",
                $"--output={reportPath}",
                "--fail-on=none",
                "--verbosity=quiet");

            Assert.Equal(0, result.ExitCode);
            Assert.NotEqual(
                "stale",
                await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken));
            using var report = JsonDocument.Parse(
                await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken));
            Assert.Equal(1, report.RootElement.GetProperty("summary").GetProperty("projects").GetInt32());
        }
        finally
        {
            File.Delete(reportPath);
        }
    }

    [Fact]
    public async Task OutputOptionWritesTheDefaultTextFormat()
    {
        var reportPath = Path.Combine(Path.GetTempPath(), $"PackageMedic.{Guid.NewGuid():N}.txt");
        try
        {
            var result = await RunAsync(
                "doctor",
                Fixture("clean", "Clean.slnx"),
                "--no-restore",
                "--output",
                reportPath,
                "--fail-on",
                "none",
                "--verbosity",
                "quiet");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.Output);
            var report = await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken);
            Assert.Contains("Summary:", report, StringComparison.Ordinal);
            Assert.Contains("0 warnings", report, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(reportPath);
        }
    }

    [Fact]
    public async Task OneAnalysisCanWriteJsonAndSarifReports()
    {
        var directory = Directory.CreateTempSubdirectory("PackageMedic.MultiOutput.");
        try
        {
            var jsonPath = Path.Combine(directory.FullName, "report.json");
            var sarifPath = Path.Combine(directory.FullName, "report.sarif");
            var result = await RunAsync(
                "doctor",
                Fixture("unused-central"),
                "--no-restore",
                "--format",
                "json",
                "--output",
                jsonPath,
                "--sarif-output",
                sarifPath,
                "--fail-on",
                "none",
                "--verbosity",
                "quiet");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.Output);
            using var json = JsonDocument.Parse(
                await File.ReadAllTextAsync(jsonPath, TestContext.Current.CancellationToken));
            using var sarif = JsonDocument.Parse(
                await File.ReadAllTextAsync(sarifPath, TestContext.Current.CancellationToken));
            Assert.Contains(
                json.RootElement.GetProperty("diagnostics").EnumerateArray(),
                item => item.GetProperty("code").GetString() == "PM001");
            Assert.Contains(
                sarif.RootElement.GetProperty("runs")[0].GetProperty("results").EnumerateArray(),
                item => item.GetProperty("ruleId").GetString() == "PM001");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task OneAnalysisCanWriteAValidCycloneDxSbom()
    {
        var directory = Directory.CreateTempSubdirectory("PackageMedic.CycloneDxOutput.");
        try
        {
            var sbomPath = Path.Combine(directory.FullName, "bom.cdx.json");
            var result = await RunAsync(
                "doctor",
                Fixture("version-drift"),
                "--no-restore",
                "--sbom-output",
                sbomPath,
                "--fail-on",
                "none",
                "--verbosity",
                "quiet");

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(sbomPath));
            using var sbom = JsonDocument.Parse(
                await File.ReadAllTextAsync(sbomPath, TestContext.Current.CancellationToken));
            Assert.Equal("CycloneDX", sbom.RootElement.GetProperty("bomFormat").GetString());
            Assert.Equal("1.7", sbom.RootElement.GetProperty("specVersion").GetString());
            Assert.NotEmpty(sbom.RootElement.GetProperty("components").EnumerateArray());
            Assert.Contains(
                sbom.RootElement.GetProperty("metadata").GetProperty("properties").EnumerateArray(),
                property => property.GetProperty("name").GetString() == "packagemedic:completeness" &&
                    property.GetProperty("value").GetString() == "incomplete");
            Assert.Empty(Directory.EnumerateFiles(directory.FullName, "*.tmp"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SbomCommandWritesOnlyTheRequestedCycloneDxDocument()
    {
        var directory = Directory.CreateTempSubdirectory("PackageMedic.SbomCommand.");
        try
        {
            var sbomPath = Path.Combine(directory.FullName, "standalone.cdx.json");
            var result = await RunAsync(
                "sbom",
                Fixture("version-drift"),
                "--no-restore",
                "--output",
                sbomPath,
                "--verbosity",
                "quiet");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.Output);
            Assert.Equal(string.Empty, result.Error);
            using var sbom = JsonDocument.Parse(
                await File.ReadAllTextAsync(sbomPath, TestContext.Current.CancellationToken));
            Assert.Equal("CycloneDX", sbom.RootElement.GetProperty("bomFormat").GetString());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task OutputPathsMustBeDifferent()
    {
        var reportPath = Path.GetTempFileName();
        try
        {
            var result = await RunAsync(
                "doctor",
                Fixture("clean", "Clean.slnx"),
                "--no-restore",
                "--output",
                reportPath,
                "--sarif-output",
                reportPath,
                "--verbosity",
                "quiet");

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("must use different paths", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(reportPath);
        }
    }

    [Fact]
    public async Task AtomicOutputCleansTemporaryFileWhenReplacementFails()
    {
        var directory = Directory.CreateTempSubdirectory("PackageMedic.AtomicFailure.");
        try
        {
            var destination = Path.Combine(directory.FullName, "report.json");
            Directory.CreateDirectory(destination);

            var exception = await Record.ExceptionAsync(() =>
                AtomicOutputFile.WriteAsync(destination, "report", CancellationToken.None));

            Assert.True(exception is IOException or UnauthorizedAccessException, exception?.ToString());
            Assert.True(Directory.Exists(destination));
            Assert.Empty(Directory.EnumerateFiles(directory.FullName, "*.tmp"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("--output=")]
    [InlineData("--sarif-output=")]
    [InlineData("--sbom-output=")]
    public async Task EmptyOutputPathIsAUsageError(string option)
    {
        var result = await RunAsync("doctor", option);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("non-empty path", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorPerformsRealRestoreBeforeAnalysis()
    {
        var directory = Directory.CreateTempSubdirectory("PackageMedic.Integration.");
        try
        {
            var project = Path.Combine(directory.FullName, "RestoreSmoke.csproj");
            await File.WriteAllTextAsync(
                project,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <RestoreIgnoreFailedSources>true</RestoreIgnoreFailedSources>
                  </PropertyGroup>
                </Project>
                """,
                TestContext.Current.CancellationToken);

            var result = await RunAsync(
                "doctor",
                project,
                "--format",
                "json",
                "--fail-on",
                "none",
                "--verbosity",
                "quiet");

            Assert.True(
                result.ExitCode == 0,
                $"doctor failed with exit code {result.ExitCode}: {result.Error}\n{result.Output}");
            using var json = JsonDocument.Parse(result.Output);
            Assert.Equal(1, json.RootElement.GetProperty("summary").GetProperty("projects").GetInt32());
            Assert.Empty(json.RootElement.GetProperty("analysisErrors").EnumerateArray());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RulesAndExplainExposeTheDiagnosticCatalog()
    {
        var rules = await RunAsync("rules");
        var explanation = await RunAsync("explain", "PM007");

        Assert.Equal(0, rules.ExitCode);
        Assert.Contains("PM006", rules.Output, StringComparison.Ordinal);
        Assert.Contains("PM007", rules.Output, StringComparison.Ordinal);
        Assert.Contains("FloatingPackageVersion", rules.Output, StringComparison.Ordinal);
        Assert.Equal(0, explanation.ExitCode);
        Assert.Contains("vulnerab", explanation.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task JsonIncludesPortableStructuredPackageInventory()
    {
        var result = await RunAsync(
            "doctor", Fixture("version-drift"), "--no-restore", "--format", "json",
            "--fail-on", "none", "--verbosity", "quiet");

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain(Fixture("version-drift"), result.Output, StringComparison.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(result.Output);
        var packages = json.RootElement.GetProperty("packages").EnumerateArray().ToArray();
        Assert.Equal(2, packages.Length);
        Assert.All(packages, package =>
        {
            Assert.Equal("direct", package.GetProperty("dependencyKind").GetString());
            Assert.Equal("project", package.GetProperty("versionSource").GetString());
            Assert.DoesNotContain(Directory.GetCurrentDirectory(), package.GetProperty("project").GetString()!, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains(packages, package => package.GetProperty("resolvedVersion").GetString() == "2.6.2");
        Assert.Contains(packages, package => package.GetProperty("resolvedVersion").GetString() == "2.9.2");
    }

    [Fact]
    public async Task DiffRejectsBaselineOptionsBeforeRunningGit()
    {
        var baseline = await RunAsync("diff", "HEAD", ".", "--baseline", "known.json");
        var failOnNew = await RunAsync("diff", "HEAD", ".", "--fail-on-new", "warning");

        Assert.Equal(2, baseline.ExitCode);
        Assert.Contains("does not accept --baseline", baseline.Error, StringComparison.Ordinal);
        Assert.Equal(2, failOnNew.ExitCode);
        Assert.Contains("does not accept --fail-on-new", failOnNew.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiffComparesTheWorkingGraphWithoutSwitchingTheRepository()
    {
        var repository = Directory.CreateTempSubdirectory("PackageMedic.DiffIntegration.");
        try
        {
            var project = Path.Combine(repository.FullName, "App.csproj");
            var assetsDirectory = Path.Combine(repository.FullName, ".assets");
            var assets = Path.Combine(assetsDirectory, "project.assets.json");
            Directory.CreateDirectory(assetsDirectory);
            await WritePackageProjectAsync(project, "1.0.0");
            await WriteAssetsAsync(assets, "1.0.0");
            await RunGitAsync(repository.FullName, "init");
            await RunGitAsync(repository.FullName, "config", "user.name", "PackageMedic Tests");
            await RunGitAsync(repository.FullName, "config", "user.email", "packagemedic@example.invalid");
            await RunGitAsync(repository.FullName, "add", ".");
            await RunGitAsync(repository.FullName, "commit", "-m", "baseline");

            await WritePackageProjectAsync(project, "2.0.0");
            await WriteAssetsAsync(assets, "2.0.0");
            var result = await RunAsync(
                "diff", "HEAD", repository.FullName, "--no-restore", "--format", "json",
                "--fail-on", "none", "--verbosity", "quiet");

            Assert.Equal(0, result.ExitCode);
            using var json = JsonDocument.Parse(result.Output);
            var change = Assert.Single(json.RootElement.GetProperty("diff").GetProperty("packageChanges").EnumerateArray());
            Assert.Equal("upgraded", change.GetProperty("kind").GetString());
            Assert.Equal("1.0.0", change.GetProperty("before").GetProperty("resolvedVersion").GetString());
            Assert.Equal("2.0.0", change.GetProperty("after").GetProperty("resolvedVersion").GetString());
            Assert.Contains(
                "2.0.0",
                await File.ReadAllTextAsync(project, TestContext.Current.CancellationToken),
                StringComparison.Ordinal);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(repository.FullName, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            repository.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DiffReportsBaseAnalysisErrorsWithoutPublishingPartialChanges()
    {
        var repository = Directory.CreateTempSubdirectory("PackageMedic.DiffIncomplete.");
        try
        {
            var project = Path.Combine(repository.FullName, "App.csproj");
            var assetsDirectory = Path.Combine(repository.FullName, ".assets");
            var assets = Path.Combine(assetsDirectory, "project.assets.json");
            await WritePackageProjectAsync(project, "1.0.0");
            await RunGitAsync(repository.FullName, "init");
            await RunGitAsync(repository.FullName, "config", "user.name", "PackageMedic Tests");
            await RunGitAsync(repository.FullName, "config", "user.email", "packagemedic@example.invalid");
            await RunGitAsync(repository.FullName, "add", "App.csproj");
            await RunGitAsync(repository.FullName, "commit", "-m", "baseline without assets");

            Directory.CreateDirectory(assetsDirectory);
            await WriteAssetsAsync(assets, "1.0.0");
            var result = await RunAsync(
                "diff", "HEAD", repository.FullName, "--no-restore", "--format", "json",
                "--fail-on", "none", "--verbosity", "quiet");

            Assert.Equal(2, result.ExitCode);
            using var json = JsonDocument.Parse(result.Output);
            var diff = json.RootElement.GetProperty("diff");
            Assert.False(diff.GetProperty("isComplete").GetBoolean());
            Assert.NotEmpty(diff.GetProperty("baselineAnalysisErrors").EnumerateArray());
            Assert.Empty(diff.GetProperty("currentAnalysisErrors").EnumerateArray());
            Assert.Empty(diff.GetProperty("changes").EnumerateArray());
            Assert.Empty(diff.GetProperty("packageChanges").EnumerateArray());
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(repository.FullName, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            repository.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DiffUsesIndependentNuGetCachesForSameIdentityPackageContent()
    {
        var repository = Directory.CreateTempSubdirectory("PackageMedic.DiffCacheIsolation.");
        try
        {
            var baselineFeed = Directory.CreateDirectory(Path.Combine(repository.FullName, "baseline-feed"));
            var currentFeed = Directory.CreateDirectory(Path.Combine(repository.FullName, "current-feed"));
            CreatePackage(baselineFeed.FullName, "Example.Package", "1.0.0", contentMarker: "baseline");
            CreatePackage(currentFeed.FullName, "Example.Package", "1.0.0", contentMarker: "current-different");
            var project = Path.Combine(repository.FullName, "App.csproj");
            await File.WriteAllTextAsync(
                project,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
                  <ItemGroup><PackageReference Include="Example.Package" Version="1.0.0" /></ItemGroup>
                </Project>
                """,
                TestContext.Current.CancellationToken);
            var config = Path.Combine(repository.FullName, "NuGet.Config");
            await File.WriteAllTextAsync(
                config,
                "<configuration><packageSources><clear /><add key=\"local\" value=\"./baseline-feed\" /></packageSources></configuration>",
                TestContext.Current.CancellationToken);
            await RunGitAsync(repository.FullName, "init");
            await RunGitAsync(repository.FullName, "config", "user.name", "PackageMedic Tests");
            await RunGitAsync(repository.FullName, "config", "user.email", "packagemedic@example.invalid");
            await RunGitAsync(repository.FullName, "add", ".");
            await RunGitAsync(repository.FullName, "commit", "-m", "baseline package content");
            await File.WriteAllTextAsync(
                config,
                "<configuration><packageSources><clear /><add key=\"local\" value=\"./current-feed\" /></packageSources></configuration>",
                TestContext.Current.CancellationToken);

            var result = await RunAsync(
                "diff", "HEAD", repository.FullName, "--format", "json",
                "--fail-on", "none", "--verbosity", "quiet");

            Assert.True(
                result.ExitCode == 1,
                $"diff failed with exit code {result.ExitCode}: {result.Error}\n{result.Output}");
            using var json = JsonDocument.Parse(result.Output);
            var impact = json.RootElement.GetProperty("diff").GetProperty("impact");
            Assert.Equal(1, impact.GetProperty("summary").GetProperty("contentChanges").GetInt32());
            Assert.Contains(
                impact.GetProperty("violations").EnumerateArray(),
                violation => violation.GetProperty("code").GetString() == "PMI010");
        }
        finally
        {
            DeleteTemporaryRepository(repository);
        }
    }

    [Fact]
    public async Task VerifiedDiffRejectsAnIntroducedBuildFailureWithoutWritingTheCheckout()
    {
        var repository = Directory.CreateTempSubdirectory("PackageMedic.VerifiedDiff.");
        var provenancePath = Path.Combine(
            Path.GetTempPath(),
            $"PackageMedic.VerifiedDiff.{Guid.NewGuid():N}.intoto.json");
        try
        {
            var project = Path.Combine(repository.FullName, "App.csproj");
            var source = Path.Combine(repository.FullName, "Program.cs");
            const string configuration = "{\"schemaVersion\":1,\"failOn\":\"none\"}";
            await File.WriteAllTextAsync(
                project,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                source,
                "System.Console.WriteLine(\"baseline builds\");",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(repository.FullName, ".packagemedic.json"),
                configuration,
                TestContext.Current.CancellationToken);
            await RunGitAsync(repository.FullName, "init");
            await RunGitAsync(repository.FullName, "config", "user.name", "PackageMedic Tests");
            await RunGitAsync(repository.FullName, "config", "user.email", "packagemedic@example.invalid");
            await RunGitAsync(repository.FullName, "add", ".");
            await RunGitAsync(repository.FullName, "commit", "-m", "buildable baseline");

            await File.WriteAllTextAsync(
                source,
                "this is not valid C#;",
                TestContext.Current.CancellationToken);
            await RunGitAsync(repository.FullName, "add", "Program.cs");
            await RunGitAsync(repository.FullName, "commit", "-m", "broken candidate");

            var result = await RunAsync(
                "diff",
                "HEAD~1",
                repository.FullName,
                "--verify",
                "build",
                "--format",
                "json",
                "--provenance-output",
                provenancePath,
                "--fail-on",
                "none",
                "--verbosity",
                "quiet");

            Assert.True(
                result.ExitCode == 1,
                $"verified diff returned {result.ExitCode}: {result.Error}\n{result.Output}");
            using var json = JsonDocument.Parse(result.Output);
            var diff = json.RootElement.GetProperty("diff");
            Assert.Equal(AnalysisDiffReport.CurrentSchemaVersion, diff.GetProperty("schemaVersion").GetInt32());
            var verification = diff.GetProperty("verification");
            Assert.Equal("build", verification.GetProperty("level").GetString());
            Assert.Equal(
                "passed",
                verification.GetProperty("baseline").GetProperty("build").GetProperty("stage").GetProperty("status").GetString());
            Assert.Equal(
                "failed",
                verification.GetProperty("candidate").GetProperty("build").GetProperty("stage").GetProperty("status").GetString());
            Assert.Equal("reject", verification.GetProperty("decision").GetProperty("verdict").GetString());
            Assert.Equal(
                "buildFailed",
                verification.GetProperty("decision").GetProperty("failureKind").GetString());
            Assert.True(File.Exists(provenancePath));
            using var provenance = JsonDocument.Parse(
                await File.ReadAllTextAsync(provenancePath, TestContext.Current.CancellationToken));
            Assert.Equal(
                InTotoEvidenceSerializer.StatementType,
                provenance.RootElement.GetProperty("_type").GetString());
            Assert.Matches(
                "^[0-9a-f]{40,64}$",
                provenance.RootElement
                    .GetProperty("subject")[0]
                    .GetProperty("digest")
                    .GetProperty("gitCommit")
                    .GetString());
            var predicate = provenance.RootElement.GetProperty("predicate");
            Assert.Equal("build", predicate.GetProperty("verification").GetProperty("level").GetString());
            Assert.Equal("reject", predicate.GetProperty("verification").GetProperty("status").GetString());
            Assert.Equal("complete", predicate.GetProperty("verification").GetProperty("completeness").GetString());
            Assert.Equal("sha256", predicate.GetProperty("configuration").GetProperty("state").GetString());
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configuration))).ToLowerInvariant(),
                predicate.GetProperty("configuration").GetProperty("sha256").GetString());
            Assert.Matches(
                "^[0-9a-f]{64}$",
                predicate.GetProperty("sbom").GetProperty("digest").GetString());
            Assert.False(Directory.Exists(Path.Combine(repository.FullName, "bin")));
            Assert.False(Directory.Exists(Path.Combine(repository.FullName, "obj")));
        }
        finally
        {
            if (File.Exists(provenancePath))
            {
                File.Delete(provenancePath);
            }

            DeleteTemporaryRepository(repository);
        }
    }

    [Fact]
    public async Task VerifiedDiffRunsNativeMicrosoftTestingPlatformWithDotNet10()
    {
        var requireNativeMtp = string.Equals(
            Environment.GetEnvironmentVariable("PACKAGEMEDIC_REQUIRE_NATIVE_MTP_E2E"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        var repository = Directory.CreateTempSubdirectory("PackageMedic.NativeMtp.");
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(repository.FullName, "global.json"),
                """
                {
                  "test": {
                    "runner": "Microsoft.Testing.Platform"
                  }
                }
                """,
                TestContext.Current.CancellationToken);
            var sdkVersion = await GetDotNetVersionAsync(repository.FullName);
            if (sdkVersion.Major < 10)
            {
                Assert.False(
                    requireNativeMtp,
                    $"Native MTP E2E requires .NET 10 or newer, but dotnet resolved to {sdkVersion}.");
                return;
            }

            await File.WriteAllTextAsync(
                Path.Combine(repository.FullName, "NuGet.Config"),
                """
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                  </packageSources>
                </configuration>
                """,
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(repository.FullName, "NativeMtp.Tests.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                    <IsPackable>false</IsPackable>
                    <IsTestProject>true</IsTestProject>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.8.1" />
                    <PackageReference Include="Microsoft.Testing.Extensions.TrxReport" Version="1.9.1" />
                    <PackageReference Include="xunit.v3" Version="3.2.2" />
                  </ItemGroup>
                </Project>
                """,
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(repository.FullName, "SmokeTests.cs"),
                """
                using Xunit;

                public sealed class SmokeTests
                {
                    [Fact]
                    public void Passes() => Assert.True(true);
                }
                """,
                TestContext.Current.CancellationToken);
            await RunGitAsync(repository.FullName, "init");
            await RunGitAsync(repository.FullName, "config", "user.name", "PackageMedic Tests");
            await RunGitAsync(repository.FullName, "config", "user.email", "packagemedic@example.invalid");
            await RunGitAsync(repository.FullName, "add", ".");
            await RunGitAsync(repository.FullName, "commit", "-m", "native MTP fixture");

            var result = await RunAsync(
                "diff",
                "HEAD",
                repository.FullName,
                "--verify",
                "test",
                "--verification-configuration",
                "Release",
                "--build-timeout",
                "300",
                "--test-timeout",
                "300",
                "--fail-on",
                "none",
                "--format",
                "json",
                "--verbosity",
                "quiet");

            Assert.True(
                result.ExitCode == 0,
                $"native MTP verified diff returned {result.ExitCode}: {result.Error}\n{result.Output}");
            using var report = JsonDocument.Parse(result.Output);
            var verification = report.RootElement.GetProperty("diff").GetProperty("verification");
            Assert.Equal("test", verification.GetProperty("level").GetString());
            Assert.Equal("noChange", verification.GetProperty("decision").GetProperty("verdict").GetString());
            foreach (var side in new[] { "baseline", "candidate" })
            {
                var tests = verification.GetProperty(side).GetProperty("tests");
                Assert.Equal("passed", tests.GetProperty("stage").GetProperty("status").GetString());
                Assert.Equal(1, tests.GetProperty("total").GetInt32());
                Assert.Equal(1, tests.GetProperty("passed").GetInt32());
                Assert.Equal(0, tests.GetProperty("failed").GetInt32());
            }

            Assert.False(Directory.Exists(Path.Combine(repository.FullName, "bin")));
            Assert.False(Directory.Exists(Path.Combine(repository.FullName, "obj")));
        }
        finally
        {
            DeleteTemporaryRepository(repository);
        }
    }

    [Fact]
    public async Task InitCreatesAValidConfigurationWithoutOverwritingByDefault()
    {
        var directory = Directory.CreateTempSubdirectory("PackageMedic.Init.");
        try
        {
            var created = await RunAsync("init", directory.FullName);
            var repeated = await RunAsync("init", directory.FullName);
            var configurationPath = Path.Combine(directory.FullName, ".packagemedic.json");

            Assert.Equal(0, created.ExitCode);
            Assert.Equal(2, repeated.ExitCode);
            var configuration = PackageMedicConfigurationLoader.Load(configurationPath);
            Assert.Equal(PackageMedicConfiguration.CurrentSchemaVersion, configuration.SchemaVersion);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task InitTreatsAnExistingJsonNamedDirectoryAsADirectory()
    {
        var root = Directory.CreateTempSubdirectory("PackageMedic.InitJsonDirectory.");
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root.FullName, "settings.json"));

            var result = await RunAsync("init", directory.FullName);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(Path.Combine(directory.FullName, ".packagemedic.json")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ConfigurationCanSuppressAWarningWithAVisibleReason()
    {
        var configurationPath = Path.Combine(Path.GetTempPath(), $"PackageMedic.{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(
                configurationPath,
                """
                {
                  "schemaVersion": 1,
                  "failOn": "warning",
                  "suppressions": [
                    { "rule": "PM001", "reason": "Accepted until the next dependency cleanup" }
                  ]
                }
                """,
                TestContext.Current.CancellationToken);

            var result = await RunAsync(
                "doctor", Fixture("unused-central"), "--no-restore", "--config", configurationPath,
                "--format", "json", "--verbosity", "quiet");

            Assert.Equal(0, result.ExitCode);
            using var json = JsonDocument.Parse(result.Output);
            Assert.Empty(json.RootElement.GetProperty("diagnostics").EnumerateArray());
            Assert.Equal(1, json.RootElement.GetProperty("policy").GetProperty("suppressed").GetInt32());
            Assert.Contains(
                "Accepted until",
                json.RootElement.GetProperty("suppressedDiagnostics")[0].GetProperty("reason").GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(configurationPath);
        }
    }

    [Fact]
    public async Task BaselineMakesKnownDiagnosticsExistingAndFailOnNewPasses()
    {
        var baselinePath = Path.Combine(Path.GetTempPath(), $"PackageMedic.{Guid.NewGuid():N}.baseline.json");
        try
        {
            var created = await RunAsync(
                "baseline", "create", Fixture("unused-central"), "--no-restore", "--output", baselinePath);
            var compared = await RunAsync(
                "doctor", Fixture("unused-central"), "--no-restore", "--baseline", baselinePath,
                "--fail-on", "none", "--fail-on-new", "warning", "--format", "json", "--verbosity", "quiet");
            var resolved = await RunAsync(
                "doctor", Fixture("clean", "Clean.slnx"), "--no-restore", "--baseline", baselinePath,
                "--fail-on", "none", "--format", "json", "--verbosity", "quiet");
            var updated = await RunAsync(
                "baseline", "update", Fixture("unused-central"), "--no-restore", "--baseline", baselinePath);

            Assert.Equal(0, created.ExitCode);
            Assert.Equal(0, compared.ExitCode);
            Assert.Equal(0, resolved.ExitCode);
            Assert.Equal(0, updated.ExitCode);
            using var json = JsonDocument.Parse(compared.Output);
            Assert.True(json.RootElement.GetProperty("baseline").GetProperty("existing").GetInt32() > 0);
            Assert.Equal(0, json.RootElement.GetProperty("baseline").GetProperty("new").GetInt32());
            Assert.All(
                json.RootElement.GetProperty("diagnostics").EnumerateArray(),
                item => Assert.Equal("existing", item.GetProperty("baselineState").GetString()));
            using var resolvedJson = JsonDocument.Parse(resolved.Output);
            Assert.True(resolvedJson.RootElement.GetProperty("baseline").GetProperty("resolved").GetInt32() > 0);
            Assert.NotEmpty(resolvedJson.RootElement.GetProperty("resolvedDiagnostics").EnumerateArray());
        }
        finally
        {
            File.Delete(baselinePath);
        }
    }

    [Fact]
    public async Task InvalidBaselineIsAHandledOperationalError()
    {
        var baselinePath = Path.Combine(Path.GetTempPath(), $"PackageMedic.{Guid.NewGuid():N}.invalid-baseline.json");
        try
        {
            await File.WriteAllTextAsync(
                baselinePath,
                """
                { "schemaVersion": 2, "toolVersion": "0.3.0", "entries": [] }
                """,
                TestContext.Current.CancellationToken);

            var result = await RunAsync(
                "doctor", Fixture("clean", "Clean.slnx"), "--no-restore", "--no-config",
                "--baseline", baselinePath, "--format", "json", "--verbosity", "quiet");

            Assert.Equal(2, result.ExitCode);
            Assert.Equal(string.Empty, result.Output);
            Assert.Contains("Unsupported baseline schemaVersion", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("Unhandled exception", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(baselinePath);
        }
    }

    [Fact]
    public async Task CleanOnlyProducesAnExplicitDryRunPlan()
    {
        var rejected = await RunAsync("clean", Fixture("unused-central"), "--no-restore");
        var planned = await RunAsync("clean", Fixture("unused-central"), "--no-restore", "--dry-run");

        Assert.Equal(2, rejected.ExitCode);
        Assert.Equal(0, planned.ExitCode);
        Assert.Contains("No dependency files were modified", planned.Output, StringComparison.Ordinal);
        Assert.Contains("Would review/remove", planned.Output, StringComparison.Ordinal);
        Assert.Contains("apply is intentionally unavailable", planned.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SimulationComparesARealLocalPackageWithoutChangingTheRepository()
    {
        var repository = Directory.CreateTempSubdirectory("PackageMedic.SimulationIntegration.");
        try
        {
            var feed = Directory.CreateDirectory(Path.Combine(repository.FullName, "feed"));
            CreatePackage(feed.FullName, "Example.Package", "1.0.0");
            CreatePackage(feed.FullName, "Example.Package", "2.0.0");
            var project = Path.Combine(repository.FullName, "App.csproj");
            await File.WriteAllTextAsync(
                Path.Combine(repository.FullName, "NuGet.Config"),
                """
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="local" value="./feed" />
                  </packageSources>
                  <packageSourceMapping>
                    <packageSource key="local"><package pattern="*" /></packageSource>
                  </packageSourceMapping>
                </configuration>
                """,
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                project,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Example.Package" Version="1.0.0" />
                  </ItemGroup>
                </Project>
                """,
                TestContext.Current.CancellationToken);
            await RunGitAsync(repository.FullName, "init");
            await RunGitAsync(repository.FullName, "config", "user.name", "PackageMedic Tests");
            await RunGitAsync(repository.FullName, "config", "user.email", "packagemedic@example.invalid");
            await RunGitAsync(repository.FullName, "add", ".");
            await RunGitAsync(repository.FullName, "commit", "-m", "simulation baseline");

            var result = await RunAsync(
                "simulate", "Example.Package", "--to", "2.0.0", project,
                "--format", "json", "--fail-on", "none", "--verbosity", "quiet");
            var repeated = await RunAsync(
                "simulate", "Example.Package", "--to", "2.0.0", project,
                "--format", "json", "--fail-on", "none", "--verbosity", "quiet");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.Error);
            Assert.Equal(0, repeated.ExitCode);
            Assert.Equal(string.Empty, repeated.Error);
            Assert.Equal(result.Output, repeated.Output);
            Assert.DoesNotContain(repository.FullName, result.Output, StringComparison.OrdinalIgnoreCase);
            using var json = JsonDocument.Parse(result.Output);
            Assert.Equal("dependencySimulation", json.RootElement.GetProperty("kind").GetString());
            Assert.True(json.RootElement.GetProperty("isComplete").GetBoolean());
            Assert.Equal("pass", json.RootElement.GetProperty("verdict").GetString());
            Assert.Equal("restoreOnly", json.RootElement.GetProperty("verification").GetProperty("evidenceLevel").GetString());
            Assert.Equal("notRun", json.RootElement.GetProperty("verification").GetProperty("build").GetString());
            Assert.Equal("App.csproj", json.RootElement.GetProperty("repository").GetProperty("analysisTarget").GetString());
            Assert.Equal("1.0.0", json.RootElement.GetProperty("mutation").GetProperty("beforeVersion").GetString());
            Assert.Equal("2.0.0", json.RootElement.GetProperty("mutation").GetProperty("candidateVersion").GetString());
            Assert.Contains(
                json.RootElement.GetProperty("comparison").GetProperty("packageChanges").EnumerateArray(),
                change => change.GetProperty("kind").GetString() == "upgraded");
            Assert.Contains(
                "Version=\"1.0.0\"",
                await File.ReadAllTextAsync(project, TestContext.Current.CancellationToken),
                StringComparison.Ordinal);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(repository.FullName, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            repository.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SimulationTreatsAMissingCandidateAsACompleteRejection()
    {
        var (repository, project) = await CreateSimulationRepositoryAsync("1.0.0");
        try
        {
            var result = await RunAsync(
                "simulate", "Example.Package", "--to", "9.9.9", project,
                "--format", "json", "--fail-on", "none", "--verbosity", "quiet");

            Assert.True(
                result.ExitCode == 1,
                $"missing-candidate simulation returned {result.ExitCode}: {result.Error}{Environment.NewLine}{result.Output}");
            using var json = JsonDocument.Parse(result.Output);
            Assert.True(json.RootElement.GetProperty("isComplete").GetBoolean());
            Assert.Equal("reject", json.RootElement.GetProperty("verdict").GetString());
            Assert.Equal("failed", json.RootElement.GetProperty("verification").GetProperty("restore").GetString());
            Assert.Equal("versionNotFound", json.RootElement.GetProperty("verification").GetProperty("restoreFailureKind").GetString());
            Assert.False(json.RootElement.GetProperty("comparison").GetProperty("isComplete").GetBoolean());
            Assert.NotEmpty(json.RootElement.GetProperty("rejectionReasons").EnumerateArray());
            Assert.Empty(json.RootElement.GetProperty("errors").EnumerateArray());
            Assert.Contains(
                "Version=\"1.0.0\"",
                await File.ReadAllTextAsync(project, TestContext.Current.CancellationToken),
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryRepository(repository);
        }
    }

    [Fact]
    public async Task SimulationCanVerifyBuildsInIndependentSnapshots()
    {
        var (repository, project) = await CreateSimulationRepositoryAsync("1.0.0", "2.0.0");
        try
        {
            var result = await RunAsync(
                "simulate",
                "Example.Package",
                "--to",
                "2.0.0",
                project,
                "--verify",
                "build",
                "--format",
                "json",
                "--fail-on",
                "none",
                "--verbosity",
                "quiet");

            Assert.True(
                result.ExitCode == 0,
                $"verified simulation returned {result.ExitCode}: {result.Error}\n{result.Output}");
            using var json = JsonDocument.Parse(result.Output);
            var root = json.RootElement;
            Assert.Equal(DependencySimulationReport.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("buildVerified", root.GetProperty("verification").GetProperty("evidenceLevel").GetString());
            Assert.Equal("passed", root.GetProperty("verification").GetProperty("build").GetString());
            var executed = root.GetProperty("verification").GetProperty("executed");
            Assert.Equal("passed", executed.GetProperty("baseline").GetProperty("build").GetProperty("stage").GetProperty("status").GetString());
            Assert.Equal("passed", executed.GetProperty("candidate").GetProperty("build").GetProperty("stage").GetProperty("status").GetString());
            Assert.Equal("pass", executed.GetProperty("decision").GetProperty("verdict").GetString());
            Assert.False(Directory.Exists(Path.Combine(repository.FullName, "bin")));
            Assert.False(Directory.Exists(Path.Combine(repository.FullName, "obj")));
        }
        finally
        {
            DeleteTemporaryRepository(repository);
        }
    }

    [Fact]
    public async Task SimulationTreatsAnUnrestorableBaselineAsOperationallyIncomplete()
    {
        var (repository, project) = await CreateSimulationRepositoryAsync("0.5.0");
        try
        {
            var result = await RunAsync(
                "simulate", "Example.Package", "--to", "2.0.0", project,
                "--format", "json", "--verbosity", "quiet");

            Assert.Equal(2, result.ExitCode);
            Assert.Equal(string.Empty, result.Output);
            Assert.Contains("baseline restore or analysis was incomplete", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTemporaryRepository(repository);
        }
    }

    [Fact]
    public async Task SimulationReportsNoChangeForANuGetEquivalentVersion()
    {
        var (repository, project) = await CreateSimulationRepositoryAsync("1.0.0");
        try
        {
            var result = await RunAsync(
                "simulate", "example.package", "--to", "1.0", project,
                "--format", "json", "--fail-on", "none", "--verbosity", "quiet");

            Assert.Equal(0, result.ExitCode);
            using var json = JsonDocument.Parse(result.Output);
            Assert.True(
                json.RootElement.GetProperty("verdict").GetString() == "noChange",
                result.Output);
            Assert.True(json.RootElement.GetProperty("mutation").GetProperty("noChange").GetBoolean());
            Assert.True(json.RootElement.GetProperty("comparison").GetProperty("isComplete").GetBoolean());
        }
        finally
        {
            DeleteTemporaryRepository(repository);
        }
    }

    [Fact]
    public async Task SimulationReportsVisibleRequestedVersionAndDiagnosticChangesEvenWhenResolutionMatches()
    {
        var (repository, project) = await CreateSimulationRepositoryAsync("2.0.0");
        try
        {
            var result = await RunAsync(
                "simulate", "Example.Package", "--to", "2.0.0", project,
                "--format", "json", "--fail-on", "none", "--verbosity", "quiet");

            Assert.Equal(0, result.ExitCode);
            using var json = JsonDocument.Parse(result.Output);
            Assert.Equal("pass", json.RootElement.GetProperty("verdict").GetString());
            Assert.False(json.RootElement.GetProperty("mutation").GetProperty("noChange").GetBoolean());
            Assert.True(json.RootElement.GetProperty("comparison").GetProperty("isComplete").GetBoolean());
            Assert.NotEmpty(json.RootElement.GetProperty("comparison").GetProperty("diagnosticChanges").EnumerateArray());
            Assert.NotEmpty(json.RootElement.GetProperty("comparison").GetProperty("packageChanges").EnumerateArray());
        }
        finally
        {
            DeleteTemporaryRepository(repository);
        }
    }

    [Fact]
    public async Task SimulationRejectsARealCandidateThatExceedsTheTransitiveImpactBudget()
    {
        var (repository, project) = await CreateSimulationRepositoryAsync("1.0.0", "2.0.0");
        try
        {
            var feed = Path.Combine(repository.FullName, "feed");
            File.Delete(Path.Combine(feed, "Example.Package.2.0.0.nupkg"));
            CreatePackage(feed, "Transitive.Package", "1.0.0");
            CreatePackage(
                feed,
                "Example.Package",
                "2.0.0",
                dependencyId: "Transitive.Package",
                dependencyVersion: "1.0.0");
            await File.WriteAllTextAsync(
                Path.Combine(repository.FullName, ".packagemedic.json"),
                """
                {
                  "schemaVersion": 1,
                  "impact": {
                    "maxAddedTransitivePackages": 0
                  }
                }
                """,
                TestContext.Current.CancellationToken);
            await RunGitAsync(repository.FullName, "add", ".");
            await RunGitAsync(repository.FullName, "commit", "--amend", "--no-edit");

            var result = await RunAsync(
                "simulate", "Example.Package", "--to", "2.0.0", project,
                "--format", "json", "--fail-on", "none", "--verbosity", "quiet");

            Assert.Equal(1, result.ExitCode);
            using var json = JsonDocument.Parse(result.Output);
            Assert.True(json.RootElement.GetProperty("isComplete").GetBoolean());
            Assert.Equal("reject", json.RootElement.GetProperty("verdict").GetString());
            var impact = json.RootElement
                .GetProperty("comparison")
                .GetProperty("impact");
            Assert.False(impact.GetProperty("gatePassed").GetBoolean());
            Assert.Contains(
                impact.GetProperty("violations").EnumerateArray(),
                item => item.GetProperty("code").GetString() == "PMI004");
            Assert.Contains(
                impact.GetProperty("packages").EnumerateArray(),
                item => item.GetProperty("packageId").GetString() == "Transitive.Package" &&
                    item.GetProperty("dependencyKind").GetString() == "transitive");
            Assert.NotEmpty(json.RootElement.GetProperty("rejectionReasons").EnumerateArray());
            Assert.Contains(
                "Version=\"1.0.0\"",
                await File.ReadAllTextAsync(project, TestContext.Current.CancellationToken),
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryRepository(repository);
        }
    }

    [Fact]
    public async Task SimulationRefusesADirtyRepositoryBeforeMaterializingSnapshots()
    {
        var (repository, project) = await CreateSimulationRepositoryAsync("1.0.0", "2.0.0");
        try
        {
            await File.AppendAllTextAsync(
                project,
                Environment.NewLine + "<!-- dirty -->",
                TestContext.Current.CancellationToken);

            var result = await RunAsync(
                "simulate", "Example.Package", "--to", "2.0.0", project,
                "--format", "json", "--verbosity", "quiet");

            Assert.Equal(2, result.ExitCode);
            Assert.Equal(string.Empty, result.Output);
            Assert.Contains("clean Git worktree", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryRepository(repository);
        }
    }

    [Fact]
    public async Task SimulationDistinguishesALockedModeConflictFromCandidateIncompatibility()
    {
        var repository = Directory.CreateTempSubdirectory("PackageMedic.SimulationLock.");
        try
        {
            var feed = Directory.CreateDirectory(Path.Combine(repository.FullName, "feed"));
            CreatePackage(feed.FullName, "Example.Package", "1.0.0");
            CreatePackage(feed.FullName, "Example.Package", "2.0.0");
            var project = Path.Combine(repository.FullName, "App.csproj");
            var config = Path.Combine(repository.FullName, "NuGet.Config");
            var packages = Path.Combine(repository.FullName, "temporary-packages");
            await File.WriteAllTextAsync(
                config,
                """
                <configuration>
                  <packageSources><clear /><add key="local" value="./feed" /></packageSources>
                </configuration>
                """,
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                project,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
                    <RestoreLockedMode>false</RestoreLockedMode>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Example.Package" Version="1.0.0" />
                  </ItemGroup>
                </Project>
                """,
                TestContext.Current.CancellationToken);
            await RunDotNetAsync(
                repository.FullName,
                "restore", project, "--configfile", config, "--packages", packages, "--nologo");
            var unlocked = await File.ReadAllTextAsync(project, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                project,
                unlocked.Replace(
                    "<RestoreLockedMode>false</RestoreLockedMode>",
                    "<RestoreLockedMode>true</RestoreLockedMode>",
                    StringComparison.Ordinal),
                TestContext.Current.CancellationToken);
            Directory.Delete(Path.Combine(repository.FullName, "obj"), recursive: true);
            Directory.Delete(packages, recursive: true);
            await RunGitAsync(repository.FullName, "init");
            await RunGitAsync(repository.FullName, "config", "user.name", "PackageMedic Tests");
            await RunGitAsync(repository.FullName, "config", "user.email", "packagemedic@example.invalid");
            await RunGitAsync(repository.FullName, "add", ".");
            await RunGitAsync(repository.FullName, "commit", "-m", "locked simulation fixture");

            var result = await RunAsync(
                "simulate", "Example.Package", "--to", "2.0.0", project,
                "--format", "json", "--fail-on", "none", "--verbosity", "quiet");

            Assert.Equal(1, result.ExitCode);
            using var json = JsonDocument.Parse(result.Output);
            Assert.True(json.RootElement.GetProperty("isComplete").GetBoolean());
            Assert.Equal("reject", json.RootElement.GetProperty("verdict").GetString());
            var verification = json.RootElement.GetProperty("verification");
            Assert.Equal("failed", verification.GetProperty("restore").GetString());
            Assert.Equal("lockedModeConflict", verification.GetProperty("restoreFailureKind").GetString());
            Assert.Equal("enforced", verification.GetProperty("lockedMode").GetString());
            Assert.Contains(
                json.RootElement.GetProperty("rejectionReasons").EnumerateArray(),
                item => item.GetString()!.Contains("regenerating", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteTemporaryRepository(repository);
        }
    }

    private static async Task<CliResult> RunAsync(params string[] arguments)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await Program.ExecuteAsync(
            arguments,
            output,
            error,
            TestContext.Current.CancellationToken);
        return new CliResult(exitCode, output.ToString(), error.ToString());
    }

    private static string Fixture(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PackageMedic.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, "fixtures", .. parts]);
    }

    private static Task WritePackageProjectAsync(string path, string version) => File.WriteAllTextAsync(
        path,
        $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
            <ProjectAssetsFile>$(MSBuildThisFileDirectory).assets/project.assets.json</ProjectAssetsFile>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Example.Package" Version="{{version}}" />
          </ItemGroup>
        </Project>
        """,
        TestContext.Current.CancellationToken);

    private static Task WriteAssetsAsync(string path, string version) => File.WriteAllTextAsync(
        path,
        $$"""
        {
          "version": 3,
          "targets": { "net8.0": { "Example.Package/{{version}}": {} } },
          "libraries": { "Example.Package/{{version}}": { "type": "package" } },
          "project": { "frameworks": { "net8.0": { "dependencies": { "Example.Package": { "target": "Package", "version": "[{{version}}, )" } } } } },
          "logs": []
        }
        """,
        TestContext.Current.CancellationToken);

    private static void CreatePackage(
        string feed,
        string packageId,
        string version,
        string? dependencyId = null,
        string? dependencyVersion = null,
        string? contentMarker = null)
    {
        if ((dependencyId is null) != (dependencyVersion is null))
        {
            throw new ArgumentException("A package dependency requires both an ID and a version.");
        }

        var dependencies = dependencyId is null
            ? string.Empty
            : $$"""
                    <dependencies>
                      <group targetFramework="net8.0">
                        <dependency id="{{dependencyId}}" version="[{{dependencyVersion}}]" />
                      </group>
                    </dependencies>
                """;
        var destination = Path.Combine(feed, $"{packageId}.{version}.nupkg");
        using var archive = ZipFile.Open(destination, ZipArchiveMode.Create);
        var nuspec = archive.CreateEntry($"{packageId}.nuspec");
        using (var stream = nuspec.Open())
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(
                $$"""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata>
                    <id>{{packageId}}</id>
                    <version>{{version}}</version>
                    <authors>PackageMedic Tests</authors>
                    <description>Local integration fixture.</description>
                {{dependencies}}
                  </metadata>
                </package>
                """);
        }

        if (contentMarker is not null)
        {
            var marker = archive.CreateEntry("content/marker.txt");
            using var markerStream = marker.Open();
            using var markerWriter = new StreamWriter(markerStream);
            markerWriter.Write(contentMarker);
        }
    }

    private static Diagnostic RestoreDiagnostic(string code) => new(
        "PM005",
        DiagnosticSeverity.Error,
        "NuGet restore diagnostic",
        "Restore reported a NuGet diagnostic.",
        null,
        null,
        null,
        code,
        "Review restore output.",
        OriginalCode: code);

    private static async Task<(DirectoryInfo Repository, string Project)> CreateSimulationRepositoryAsync(
        params string[] versions)
    {
        var repository = Directory.CreateTempSubdirectory("PackageMedic.SimulationFixture.");
        var feed = Directory.CreateDirectory(Path.Combine(repository.FullName, "feed"));
        foreach (var version in versions)
        {
            CreatePackage(feed.FullName, "Example.Package", version);
        }

        var project = Path.Combine(repository.FullName, "App.csproj");
        await File.WriteAllTextAsync(
            Path.Combine(repository.FullName, "NuGet.Config"),
            """
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="./feed" />
              </packageSources>
            </configuration>
            """,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            project,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Example.Package" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """,
            TestContext.Current.CancellationToken);
        await RunGitAsync(repository.FullName, "init");
        await RunGitAsync(repository.FullName, "config", "user.name", "PackageMedic Tests");
        await RunGitAsync(repository.FullName, "config", "user.email", "packagemedic@example.invalid");
        await RunGitAsync(repository.FullName, "add", ".");
        await RunGitAsync(repository.FullName, "commit", "-m", "simulation fixture");
        return (repository, project);
    }

    private static void DeleteTemporaryRepository(DirectoryInfo repository)
    {
        foreach (var file in Directory.EnumerateFiles(repository.FullName, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        repository.Delete(recursive: true);
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(' ', arguments)} failed: {await standardError} {await standardOutput}");
    }

    private static async Task<Version> GetDotNetVersionAsync(string workingDirectory)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet", "--version")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, $"dotnet --version failed: {error}");
        Assert.True(Version.TryParse(output.Trim(), out var version), $"Invalid dotnet version: {output}");
        return version!;
    }

    private static async Task RunDotNetAsync(string workingDirectory, params string[] arguments)
    {
        var environmentRoot = Directory.CreateTempSubdirectory("PackageMedic.DotNetTestEnvironment.");
        try
        {
            var environment = ProcessEnvironment.CreateIsolatedDotNet(environmentRoot.FullName);
            IProcessRunner runner = new EnvironmentScopedProcessRunner(new ProcessRunner(), environment);
            var result = await runner.RunAsync(
                "dotnet",
                arguments,
                workingDirectory,
                TestContext.Current.CancellationToken);
            Assert.True(
                result.ExitCode == 0,
                $"dotnet {string.Join(' ', arguments)} failed: {result.StandardError} {result.StandardOutput}");
        }
        finally
        {
            environmentRoot.Delete(recursive: true);
        }
    }

    private sealed record CliResult(int ExitCode, string Output, string Error);
}
