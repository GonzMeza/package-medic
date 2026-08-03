using System.Text.Json;
using PackageMedic.Cli;
using PackageMedic.Core;

namespace PackageMedic.IntegrationTests;

public sealed class CliIntegrationTests
{
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
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
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
            await File.WriteAllTextAsync(reportPath, "stale");
            var result = await RunAsync(
                "doctor",
                Fixture("clean", "Clean.slnx"),
                "--no-restore",
                "--format=json",
                $"--output={reportPath}",
                "--fail-on=none",
                "--verbosity=quiet");

            Assert.Equal(0, result.ExitCode);
            Assert.NotEqual("stale", await File.ReadAllTextAsync(reportPath));
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
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
            var report = await File.ReadAllTextAsync(reportPath);
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
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath));
            using var sarif = JsonDocument.Parse(await File.ReadAllTextAsync(sarifPath));
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
                """);

            var result = await RunAsync(
                "doctor",
                project,
                "--format",
                "json",
                "--fail-on",
                "none",
                "--verbosity",
                "quiet");

            Assert.Equal(0, result.ExitCode);
            using var json = JsonDocument.Parse(result.Output);
            Assert.Equal(1, json.RootElement.GetProperty("summary").GetProperty("projects").GetInt32());
            Assert.Empty(json.RootElement.GetProperty("analysisErrors").EnumerateArray());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task<CliResult> RunAsync(params string[] arguments)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await Program.ExecuteAsync(arguments, output, error);
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

    private sealed record CliResult(int ExitCode, string Output, string Error);
}
