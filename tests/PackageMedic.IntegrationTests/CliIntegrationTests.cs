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
