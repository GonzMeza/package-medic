using System.Text.Json;
using PackageMedic.Core;

namespace PackageMedic.Core.Tests;

public sealed class BaselineTests
{
    [Fact]
    public void CreateAndSerializeAreDeterministicPortableAndDeduplicated()
    {
        var duplicate = DiagnosticFor("PM002", @"C:\repo\src\My Project\App.csproj", 9, "Newtonsoft.Json 12.0.1");
        var result = Result(
            DiagnosticFor("PM003", @"C:\repo\Directory.Packages.props", 3, "Serilog 2.0.0"),
            duplicate,
            duplicate with { Line = 99 });

        var baseline = BaselineSerializer.Create(result, @"C:\repo");
        var first = BaselineSerializer.Serialize(baseline);
        var second = BaselineSerializer.Serialize(BaselineSerializer.Create(result, @"C:\repo"));

        Assert.Equal(first, second);
        Assert.Equal(PackageMedicBaseline.CurrentSchemaVersion, baseline.SchemaVersion);
        Assert.Equal("0.3.0", baseline.ToolVersion);
        Assert.Equal(2, baseline.Entries.Count);
        Assert.Equal(
            baseline.Entries.OrderBy(entry => entry.Fingerprint, StringComparer.Ordinal),
            baseline.Entries);
        Assert.Contains("src/My%20Project/App.csproj", first, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\repo", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"line\"", first, StringComparison.Ordinal);
    }

    [Fact]
    public void FingerprintMatchesSarifAndSurvivesRootAndLineChanges()
    {
        var windows = DiagnosticFor(
            "PM005",
            @"C:\one\repo\src\App.csproj",
            10,
            @"Restore at C:\one\repo\src\App.csproj failed",
            originalCode: "NU1605");
        var unix = windows with
        {
            File = "/another/repo/src/App.csproj",
            Line = 400,
            Evidence = "Restore at /another/repo/src/App.csproj failed",
        };

        var windowsIdentity = DiagnosticFingerprint.Create(windows, @"C:\one\repo");
        var unixIdentity = DiagnosticFingerprint.Create(unix, "/another/repo");
        using var sarif = JsonDocument.Parse(SarifResultSerializer.Serialize(Result(windows), @"C:\one\repo"));
        var sarifFingerprint = sarif.RootElement
            .GetProperty("runs")[0]
            .GetProperty("results")[0]
            .GetProperty("partialFingerprints")
            .GetProperty(DiagnosticFingerprint.Algorithm)
            .GetString();

        Assert.Equal(windowsIdentity, unixIdentity);
        Assert.Equal(windowsIdentity.Fingerprint, sarifFingerprint);
        Assert.Matches("^[0-9a-f]{64}$", windowsIdentity.Fingerprint);
    }

    [Fact]
    public void CompareClassifiesNewExistingAndResolved()
    {
        var existingBefore = DiagnosticFor("PM001", @"C:\repo\src\App.csproj", 4, "PackageA 1.0.0");
        var resolved = DiagnosticFor("PM003", @"C:\repo\Directory.Packages.props", 8, "PackageB 2.0.0");
        var baseline = BaselineSerializer.Create(Result(existingBefore, resolved), @"C:\repo");
        var existingAfter = existingBefore with { File = "/work/repo/src/App.csproj", Line = 80 };
        var added = DiagnosticFor("PM004", "/work/repo/src/New.csproj", 12, "NU1107 conflict");

        var comparison = BaselineMatcher.Compare(Result(existingAfter, added), baseline, "/work/repo");

        Assert.Equal(1, comparison.NewCount);
        Assert.Equal(1, comparison.ExistingCount);
        Assert.Equal(1, comparison.ResolvedCount);
        Assert.Equal(BaselineDiagnosticState.Existing, comparison.Current[0].State);
        Assert.Equal(BaselineDiagnosticState.New, comparison.Current[1].State);
        Assert.Equal("PM003", Assert.Single(comparison.Resolved).RuleId);
        Assert.Equal("src/App.csproj", comparison.Current[0].RelativePath);
    }

    [Fact]
    public void SarifCarriesBaselineStatesWithoutChangingFingerprints()
    {
        var existing = DiagnosticFor("PM001", "/repo/App.csproj", 4, "PackageA 1.0.0");
        var added = DiagnosticFor("PM006", "/repo/New.csproj", 8, "PackageB 2.*");
        var result = Result(existing, added);
        var baseline = BaselineSerializer.Create(Result(existing), "/repo");
        var comparison = BaselineMatcher.Compare(result, baseline, "/repo");

        using var sarif = JsonDocument.Parse(SarifResultSerializer.Serialize(result, "/repo", comparison));
        var results = sarif.RootElement.GetProperty("runs")[0].GetProperty("results").EnumerateArray().ToArray();

        Assert.Equal("unchanged", results.Single(item => item.GetProperty("ruleId").GetString() == "PM001").GetProperty("baselineState").GetString());
        Assert.Equal("new", results.Single(item => item.GetProperty("ruleId").GetString() == "PM006").GetProperty("baselineState").GetString());
    }

    [Fact]
    public void SyntheticLargeBaselineComparisonRemainsDeterministic()
    {
        var previousDiagnostics = Enumerable.Range(0, 5_000)
            .Select(index => DiagnosticFor("PM001", $"/repo/src/P{index}.csproj", index + 1, $"Package{index} 1.0.0"))
            .ToArray();
        var currentDiagnostics = previousDiagnostics.Skip(100)
            .Concat(Enumerable.Range(5_000, 100)
                .Select(index => DiagnosticFor("PM006", $"/repo/src/P{index}.csproj", index + 1, $"Package{index} 1.*")))
            .ToArray();
        var baseline = BaselineSerializer.Create(Result(previousDiagnostics), "/repo");

        var first = BaselineMatcher.Compare(Result(currentDiagnostics), baseline, "/repo");
        var second = BaselineMatcher.Compare(Result(currentDiagnostics), baseline, "/repo");

        Assert.Equal(100, first.NewCount);
        Assert.Equal(4_900, first.ExistingCount);
        Assert.Equal(100, first.ResolvedCount);
        Assert.Equal(
            first.Current.Select(item => (item.Fingerprint, item.State)),
            second.Current.Select(item => (item.Fingerprint, item.State)));
        Assert.Equal(
            first.Resolved.Select(item => item.Fingerprint),
            second.Resolved.Select(item => item.Fingerprint));
    }

    [Fact]
    public void UpdateReplacesResolvedEntriesAndIncludesNewEntries()
    {
        var removed = DiagnosticFor("PM001", "/repo/Old.csproj", 1, "OldPackage");
        var added = DiagnosticFor("PM002", "/repo/New.csproj", 2, "NewPackage");
        var previous = BaselineSerializer.Create(Result(removed), "/repo");

        var updated = BaselineSerializer.Update(previous, Result(added), "/repo");

        var entry = Assert.Single(updated.Entries);
        Assert.Equal("PM002", entry.RuleId);
        Assert.Equal("New.csproj", entry.File);
        Assert.DoesNotContain(previous.Entries[0].Fingerprint, updated.Entries.Select(item => item.Fingerprint));
    }

    [Fact]
    public void DeserializeAndLoadReturnCanonicalBaseline()
    {
        var first = new string('A', 64);
        var second = new string('b', 64);
        var json = $$"""
            {
              "entries": [
                {
                  "title": "Second",
                  "severity": "WARNING",
                  "ruleId": "PM002",
                  "fingerprint": "{{second}}",
                  "file": "src/App.csproj"
                },
                {
                  "fingerprint": "{{first}}",
                  "ruleId": "PM001",
                  "severity": "information",
                  "title": "First"
                }
              ],
              "toolVersion": "0.3.0",
              "schemaVersion": 1
            }
            """;
        var path = Path.Combine(Path.GetTempPath(), $"packagemedic-baseline-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, json);
            var baseline = BaselineSerializer.Load(path);
            var serialized = BaselineSerializer.Serialize(baseline);

            Assert.Equal(first.ToLowerInvariant(), baseline.Entries[0].Fingerprint);
            Assert.Equal(second, baseline.Entries[1].Fingerprint);
            Assert.Equal(serialized, BaselineSerializer.Serialize(BaselineSerializer.Deserialize(serialized)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"toolVersion\":\"0.3.0\",\"entries\":[]}", "schemaVersion")]
    [InlineData("{\"schemaVersion\":1,\"toolVersion\":\"0.3.0\",\"entries\":{}}", "entries")]
    [InlineData("{\"schemaVersion\":1,\"toolVersion\":\"0.3.0\",\"entries\":[{\"fingerprint\":\"bad\",\"ruleId\":\"PM001\",\"severity\":\"warning\",\"title\":\"Bad\"}]}", "fingerprint")]
    [InlineData("{\"schemaVersion\":1,\"toolVersion\":\"0.3.0\",\"entries\":[{\"fingerprint\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"ruleId\":\"PM001\",\"severity\":\"warning\",\"title\":\"Bad\",\"file\":\"../secret.props\"}]}", "repository-relative")]
    public void RejectsUnsupportedOrUnsafeDocuments(string json, string expectedMessage)
    {
        var exception = Assert.Throws<InvalidDataException>(() => BaselineSerializer.Deserialize(json));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsDuplicateFingerprintsCaseInsensitively()
    {
        var fingerprint = new string('a', 64);
        var baseline = new PackageMedicBaseline(
            1,
            "0.3.0",
            [
                new(fingerprint, "PM001", DiagnosticSeverity.Warning, "One", null, null),
                new(fingerprint.ToUpperInvariant(), "PM001", DiagnosticSeverity.Warning, "Two", null, null),
            ]);

        var exception = Assert.Throws<InvalidDataException>(() => BaselineSerializer.Serialize(baseline));

        Assert.Contains("duplicated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static AnalysisResult Result(params Diagnostic[] diagnostics) => new(
        "0.3.0",
        "/repo",
        new ScanSummary(0, 1, 0, 0, 0, 0, 0),
        diagnostics,
        []);

    private static Diagnostic DiagnosticFor(
        string code,
        string? file,
        int? line,
        string evidence,
        string? originalCode = null) => new(
            code,
            code == "PM004" ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
            $"{code} title",
            $"{code} explanation",
            "App",
            file,
            line,
            evidence,
            "Review the package declaration.",
            DiagnosticConfidence.High,
            originalCode);
}
