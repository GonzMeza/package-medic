using System.Text.Json;
using PackageMedic.Core;

namespace PackageMedic.Core.Tests;

public sealed class SarifResultSerializerTests
{
    [Fact]
    public void ProducesDeterministicSarif21Document()
    {
        var result = CreateResult(
            DiagnosticFor("PM002", DiagnosticSeverity.Warning, @"C:\repo\src\App.csproj", 18));

        var first = SarifResultSerializer.Serialize(result, @"C:\repo");
        var second = SarifResultSerializer.Serialize(result, @"C:\repo");

        Assert.Equal(first, second);
        using var document = JsonDocument.Parse(first);
        Assert.Equal("2.1.0", document.RootElement.GetProperty("version").GetString());
        Assert.Equal(
            "https://json.schemastore.org/sarif-2.1.0.json",
            document.RootElement.GetProperty("$schema").GetString());
        Assert.Single(document.RootElement.GetProperty("runs").EnumerateArray());
    }

    [Fact]
    public void EmitsCentralMetadataForEveryPackageMedicRule()
    {
        using var document = Serialize(CreateResult());
        var driver = GetRun(document).GetProperty("tool").GetProperty("driver");
        var rules = driver.GetProperty("rules").EnumerateArray().ToArray();

        Assert.Equal("PackageMedic", driver.GetProperty("name").GetString());
        Assert.Equal("0.2.0", driver.GetProperty("semanticVersion").GetString());
        Assert.Equal(["PM001", "PM002", "PM003", "PM004", "PM005", "PM006", "PM007", "PM008"], rules.Select(RuleId).ToArray());
        Assert.Equal(
            ["warning", "warning", "warning", "error", "warning", "warning", "warning", "warning"],
            rules.Select(rule => rule.GetProperty("defaultConfiguration").GetProperty("level").GetString()!).ToArray());
        Assert.All(rules, rule =>
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.GetProperty("name").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(rule.GetProperty("shortDescription").GetProperty("text").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(rule.GetProperty("fullDescription").GetProperty("text").GetString()));
            Assert.StartsWith(
                "https://github.com/GonzMeza/package-medic/",
                rule.GetProperty("helpUri").GetString(),
                StringComparison.Ordinal);
        });

        Assert.Equal(
            ["PM001", "PM002", "PM003", "PM004", "PM005", "PM006", "PM007", "PM008"],
            DiagnosticRuleCatalog.All.Select(rule => rule.Code).ToArray());
    }

    [Fact]
    public void MapsResultLevelsRuleIndexesAndProperties()
    {
        var result = CreateResult(
            DiagnosticFor("PM001", DiagnosticSeverity.Information, "src/One.csproj", 1),
            DiagnosticFor("PM004", DiagnosticSeverity.Error, "src/Four.csproj", 2),
            DiagnosticFor(
                "PM005",
                DiagnosticSeverity.Warning,
                "src/Five.csproj",
                3,
                confidence: DiagnosticConfidence.Medium,
                originalCode: "NU1605"));

        using var document = Serialize(result);
        var results = GetRun(document).GetProperty("results").EnumerateArray().ToArray();

        Assert.Equal(["PM004", "PM005", "PM001"], results.Select(ResultRuleId).ToArray());
        Assert.Equal(["error", "warning", "note"], results.Select(ResultLevel).ToArray());
        Assert.Equal([3, 4, 0], results.Select(item => item.GetProperty("ruleIndex").GetInt32()).ToArray());
        var nuget = results.Single(item => ResultRuleId(item) == "PM005");
        Assert.Equal("medium", nuget.GetProperty("properties").GetProperty("confidence").GetString());
        Assert.Equal("NU1605", nuget.GetProperty("properties").GetProperty("originalCode").GetString());
    }

    [Theory]
    [InlineData(@"C:\agent\repo", @"c:\AGENT\repo\src\My Project\Café.csproj", "src/My%20Project/Caf%C3%A9.csproj")]
    [InlineData("/home/runner/repo", "/home/runner/repo/src/Δelta project.csproj", "src/%CE%94elta%20project.csproj")]
    [InlineData("/home/runner/repo", @"src\nested\App.csproj", "src/nested/App.csproj")]
    [InlineData(@"C:\", @"C:\src\App.csproj", "src/App.csproj")]
    [InlineData("/", "/src/App.csproj", "src/App.csproj")]
    public void EmitsPortableSourceRootLocations(string repositoryRoot, string file, string expectedUri)
    {
        using var document = Serialize(
            CreateResult(DiagnosticFor("PM003", DiagnosticSeverity.Warning, file, 27)),
            repositoryRoot);
        var physicalLocation = GetFirstResult(document)
            .GetProperty("locations")[0]
            .GetProperty("physicalLocation");
        var location = physicalLocation.GetProperty("artifactLocation");

        Assert.Equal(expectedUri, location.GetProperty("uri").GetString());
        Assert.Equal("%SRCROOT%", location.GetProperty("uriBaseId").GetString());
        Assert.Equal(27, physicalLocation.GetProperty("region").GetProperty("startLine").GetInt32());
    }

    [Fact]
    public void FingerprintIsStableAcrossRootsAndLineMovement()
    {
        var firstDiagnostic = DiagnosticFor(
            "PM002",
            DiagnosticSeverity.Warning,
            @"C:\one\repo\src\App.csproj",
            10,
            evidence: @"Package reference at C:\one\repo\src\App.csproj");
        var secondDiagnostic = firstDiagnostic with
        {
            File = "/another/repo/src/App.csproj",
            Line = 200,
            Evidence = "Package reference at /another/repo/src/App.csproj",
        };

        using var first = Serialize(CreateResult(firstDiagnostic), @"C:\one\repo");
        using var second = Serialize(CreateResult(secondDiagnostic), "/another/repo");

        Assert.Equal(GetResultUri(first), GetResultUri(second));
        Assert.Equal(GetResultMessage(first), GetResultMessage(second));
        Assert.Equal(GetFingerprint(first), GetFingerprint(second));
        Assert.Matches("^[0-9a-f]{64}$", GetFingerprint(first));
        Assert.Equal(GetFingerprint(first), GetPrimaryLocationLineHash(first));
    }

    [Fact]
    public void OmitsMissingUnsafeAndExternalLocations()
    {
        var result = CreateResult(
            DiagnosticFor("PM001", DiagnosticSeverity.Warning, null, null),
            DiagnosticFor("PM002", DiagnosticSeverity.Warning, "../outside/Secrets.props", 2),
            DiagnosticFor("PM003", DiagnosticSeverity.Warning, @"D:\private\Secrets.props", 3));

        using var document = Serialize(result, @"C:\repo");
        var results = GetRun(document).GetProperty("results").EnumerateArray().ToArray();

        Assert.All(results, item => Assert.False(item.TryGetProperty("locations", out _)));
        var json = SarifResultSerializer.Serialize(result, @"C:\repo");
        Assert.DoesNotContain(@"D:\private", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("../outside", json, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactsCredentialsAndRepositoryAbsolutePathsFromMessages()
    {
        var diagnostic = DiagnosticFor(
            "PM005",
            DiagnosticSeverity.Error,
            @"C:\agent\repo\App.csproj",
            null,
            evidence:
                @"Restore at C:\agent\repo\App.csproj and D:\private\Secrets.props or /home/alice/work/secret.props used https://build-user:super-secret@feed.example/v3/index.json token=abc123 password=hunter2",
            originalCode: "NU1107");

        var json = SarifResultSerializer.Serialize(CreateResult(diagnostic), @"C:\agent\repo");

        Assert.DoesNotContain(@"C:\agent\repo", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"D:\private", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/home/alice", json, StringComparison.Ordinal);
        Assert.DoesNotContain("build-user", json, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", json, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", json, StringComparison.Ordinal);
        Assert.Contains("%SRCROOT%", json, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", json, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsUnknownRulesAndNonAbsoluteRoots()
    {
        var unknown = CreateResult(DiagnosticFor("PM999", DiagnosticSeverity.Warning, null, null));

        Assert.Throws<InvalidOperationException>(() => SarifResultSerializer.Serialize(unknown, "/repo"));
        Assert.Throws<ArgumentException>(() => SarifResultSerializer.Serialize(CreateResult(), "relative/repo"));
    }

    private static AnalysisResult CreateResult(params Diagnostic[] diagnostics) => new(
        "0.2.0",
        "/repo",
        new ScanSummary(0, 1, 0, 0, 0, 0, 0),
        diagnostics,
        []);

    private static Diagnostic DiagnosticFor(
        string code,
        DiagnosticSeverity severity,
        string? file,
        int? line,
        string evidence = "Package evidence",
        DiagnosticConfidence? confidence = DiagnosticConfidence.High,
        string? originalCode = null) => new(
            code,
            severity,
            $"{code} title",
            $"{code} explanation",
            "App",
            file,
            line,
            evidence,
            "Review the package declaration.",
            confidence,
            originalCode);

    private static JsonDocument Serialize(AnalysisResult result, string repositoryRoot = "/repo") =>
        JsonDocument.Parse(SarifResultSerializer.Serialize(result, repositoryRoot));

    private static JsonElement GetRun(JsonDocument document) => document.RootElement.GetProperty("runs")[0];

    private static JsonElement GetFirstResult(JsonDocument document) =>
        GetRun(document).GetProperty("results")[0];

    private static string GetFingerprint(JsonDocument document) =>
        GetFirstResult(document)
            .GetProperty("partialFingerprints")
            .GetProperty("packageMedicDiagnostic/v1")
            .GetString()!;

    private static string GetResultUri(JsonDocument document) =>
        GetFirstResult(document)
            .GetProperty("locations")[0]
            .GetProperty("physicalLocation")
            .GetProperty("artifactLocation")
            .GetProperty("uri")
            .GetString()!;

    private static string GetPrimaryLocationLineHash(JsonDocument document) =>
        GetFirstResult(document)
            .GetProperty("partialFingerprints")
            .GetProperty("primaryLocationLineHash")
            .GetString()!;

    private static string GetResultMessage(JsonDocument document) =>
        GetFirstResult(document).GetProperty("message").GetProperty("text").GetString()!;

    private static string RuleId(JsonElement rule) => rule.GetProperty("id").GetString()!;

    private static string ResultRuleId(JsonElement result) => result.GetProperty("ruleId").GetString()!;

    private static string ResultLevel(JsonElement result) => result.GetProperty("level").GetString()!;
}
