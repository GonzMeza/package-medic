using System.Text.Json;
using PackageMedic.Core;

namespace PackageMedic.Core.Tests;

public sealed class InfrastructureTests
{
    [Fact]
    public void ReadsMultiTargetAssetsAndNuGetLogs()
    {
        var temporaryFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(
                temporaryFile,
                """
                {
                  "targets": {
                    "net8.0": { "Direct/1.0.0": {}, "Transitive/2.0.0": {} },
                    "net9.0": { "Direct/1.0.0": {} }
                  },
                  "libraries": {
                    "Direct/1.0.0": { "type": "package" },
                    "Transitive/2.0.0": { "type": "package" }
                  },
                  "project": {
                    "frameworks": {
                      "net8.0": { "dependencies": { "Direct": {} } },
                      "net9.0": { "dependencies": { "Direct": {} } }
                    }
                  },
                  "logs": [
                    { "code": "NU1605", "level": "Warning", "message": "Detected package downgrade", "file": "App.csproj", "lineNumber": 12 }
                  ]
                }
                """);

            var result = new AssetsFileReader().Read(temporaryFile, "App.csproj");

            Assert.Contains("Direct", result.ResolvedPackages);
            Assert.Contains("Transitive", result.TransitivePackages);
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal("NU1605", diagnostic.OriginalCode);
        }
        finally
        {
            File.Delete(temporaryFile);
        }
    }

    [Fact]
    public void ParsesRestoreDiagnosticAndPreservesOriginalCode()
    {
        const string output = "App.csproj : warning NU1107: Version conflict detected [App.sln]";

        var diagnostic = Assert.Single(RestoreRunner.ParseNuGetDiagnostics(output, "fallback.csproj"));

        Assert.Equal("PM005", diagnostic.Code);
        Assert.Equal("NU1107", diagnostic.OriginalCode);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
    }

    [Fact]
    public void JsonOutputIsStableAndValid()
    {
        var result = new AnalysisResult(
            PackageMedicAnalyzer.Version,
            "/repo",
            new ScanSummary(1, 1, 0, 0, 0, 0, 0),
            [],
            []);

        var first = ResultJsonSerializer.Serialize(result);
        var second = ResultJsonSerializer.Serialize(result);

        Assert.Equal(first, second);
        using var document = JsonDocument.Parse(first);
        Assert.Equal(PackageMedicAnalyzer.Version, document.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public void ProcessOutputRedactsFeedCredentialsAndSecretAssignments()
    {
        const string raw =
            "https://build-user:super-secret@packages.example.test/v3/index.json " +
            "token=abc123 password=hunter2 api_key=xyz789";

        var redacted = ProcessRunner.RedactSecrets(raw);

        Assert.DoesNotContain("build-user", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("xyz789", redacted, StringComparison.Ordinal);
        Assert.Contains("https://[REDACTED]@packages.example.test", redacted, StringComparison.Ordinal);
        Assert.Contains("token=[REDACTED]", redacted, StringComparison.Ordinal);
    }
}
