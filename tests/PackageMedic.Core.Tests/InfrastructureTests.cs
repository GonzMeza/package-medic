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
            "0.1.0-preview.1",
            "/repo",
            new ScanSummary(1, 1, 0, 0, 0, 0, 0),
            [],
            []);

        var first = ResultJsonSerializer.Serialize(result);
        var second = ResultJsonSerializer.Serialize(result);

        Assert.Equal(first, second);
        using var document = JsonDocument.Parse(first);
        Assert.Equal("0.1.0-preview.1", document.RootElement.GetProperty("version").GetString());
    }
}
