using System.Text.Json;
using PackageMedic.Cli;

namespace PackageMedic.IntegrationTests;

public sealed class FloatingVersionIntegrationTests
{
    [Fact]
    public async Task FloatingVersionFixtureReportsOnlyTheThreeFloatingDeclarations()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await Program.ExecuteAsync(
            [
                "doctor",
                Fixture("floating-version"),
                "--no-restore",
                "--format=json",
                "--fail-on=none",
                "--verbosity=quiet",
            ],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        var diagnostics = json.RootElement.GetProperty("diagnostics")
            .EnumerateArray()
            .Where(item => item.GetProperty("code").GetString() == "PM006")
            .ToArray();

        Assert.Equal(3, diagnostics.Length);
        Assert.All(diagnostics, item => Assert.True(item.GetProperty("line").GetInt32() > 0));
        Assert.Contains(diagnostics, item => item.GetProperty("evidence").GetString()!.Contains("PackageVersion Central.Float", StringComparison.Ordinal));
        Assert.Contains(diagnostics, item => item.GetProperty("evidence").GetString()!.Contains("Version='2.*'", StringComparison.Ordinal));
        Assert.Contains(diagnostics, item => item.GetProperty("evidence").GetString()!.Contains("VersionOverride='3.0.0-rc.*'", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, item => item.GetProperty("evidence").GetString()!.Contains("Fixed.Range", StringComparison.Ordinal));
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
}
