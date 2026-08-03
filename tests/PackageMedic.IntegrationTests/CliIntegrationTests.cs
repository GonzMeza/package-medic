using System.Diagnostics;
using System.Text.Json;
using PackageMedic.Cli;
using PackageMedic.Core;

namespace PackageMedic.IntegrationTests;

public sealed class CliIntegrationTests
{
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
            Assert.Equal("versionChanged", change.GetProperty("kind").GetString());
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

    private sealed record CliResult(int ExitCode, string Output, string Error);
}
