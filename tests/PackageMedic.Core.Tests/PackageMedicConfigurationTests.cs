using PackageMedic.Core;

namespace PackageMedic.Core.Tests;

public sealed class PackageMedicConfigurationTests
{
    [Fact]
    public void ParsesAndNormalizesSchemaVersionOneConfiguration()
    {
        var configuration = PackageMedicConfigurationLoader.Parse(
            """
            {
              "$schema": "https://example.test/packagemedic.schema.json",
              "schemaVersion": 1,
              "failOn": "error",
              "failOnNew": "warning",
              "baseline": " .config/baseline.json ",
              "exclude": ["src\\Generated\\**", "./artifacts/**", "ARTIFACTS/**"],
              "rules": {
                "PM001": { "enabled": false },
                "pm002": { "severity": "error" }
              },
              "suppressions": [
                {
                  "rule": "pm003",
                  "path": ".\\src\\Legacy\\**",
                  "package": "Example.Legacy",
                  "reason": " Migration tracked in issue 42. "
                }
              ],
              "timeouts": {
                "restoreSeconds": 120,
                "evaluationSeconds": 30
              }
            }
            """,
            "test-config.json");

        Assert.Equal(PackageMedicConfiguration.CurrentSchemaVersion, configuration.SchemaVersion);
        Assert.Equal(PolicyFailureLevel.Error, configuration.FailOn);
        Assert.Equal(PolicyFailureLevel.Warning, configuration.FailOnNew);
        Assert.Equal(".config/baseline.json", configuration.Baseline);
        Assert.Equal(["artifacts/**", "src/Generated/**"], configuration.Exclude);
        Assert.False(configuration.Rules["PM001"].Enabled);
        Assert.Equal(DiagnosticSeverity.Error, configuration.Rules["PM002"].Severity);
        var suppression = Assert.Single(configuration.Suppressions);
        Assert.Equal("PM003", suppression.Rule);
        Assert.Equal("src/Legacy/**", suppression.Path);
        Assert.Equal("Example.Legacy", suppression.Package);
        Assert.Equal("Migration tracked in issue 42.", suppression.Reason);
        Assert.Equal(120, configuration.Timeouts.RestoreSeconds);
        Assert.Equal(30, configuration.Timeouts.EvaluationSeconds);
    }

    [Fact]
    public void ResolvesCliOverridesBeforeConfigurationAndDefaults()
    {
        var configuration = PackageMedicConfigurationLoader.Parse(
            """
            {
              "schemaVersion": 1,
              "failOn": "error",
              "failOnNew": "error",
              "baseline": "config-baseline.json",
              "timeouts": { "restoreSeconds": 90, "evaluationSeconds": 45 }
            }
            """);
        var directory = Path.Combine(Path.GetTempPath(), "package-medic-policy");

        var policy = AnalysisPolicyResolver.Resolve(
            configuration,
            directory,
            new AnalysisPolicyOverrides(
                FailOn: PolicyFailureLevel.None,
                FailOnNew: PolicyFailureLevel.Warning,
                Baseline: "cli-baseline.json",
                RestoreTimeoutSeconds: 15));

        Assert.Equal(PolicyFailureLevel.None, policy.FailOn);
        Assert.Equal(PolicyFailureLevel.Warning, policy.FailOnNew);
        Assert.Equal(Path.GetFullPath("cli-baseline.json", directory), policy.BaselinePath);
        Assert.Equal(TimeSpan.FromSeconds(15), policy.Timeouts.Restore);
        Assert.Equal(TimeSpan.FromSeconds(45), policy.Timeouts.Evaluation);

        var defaults = AnalysisPolicyResolver.Resolve(PackageMedicConfiguration.Default, directory);
        Assert.Equal(PolicyFailureLevel.Warning, defaults.FailOn);
        Assert.Null(defaults.FailOnNew);
        Assert.Null(defaults.BaselinePath);
        Assert.Equal(PolicyTimeouts.Default, defaults.Timeouts);
    }

    [Fact]
    public void AppliesRuleOverridesExclusionsAndJustifiedSuppressions()
    {
        var configuration = PackageMedicConfigurationLoader.Parse(
            """
            {
              "schemaVersion": 1,
              "exclude": ["**/Generated/**"],
              "rules": {
                "PM001": { "enabled": false },
                "PM002": { "severity": "error" }
              },
              "suppressions": [
                {
                  "rule": "PM003",
                  "path": "src/Legacy/**",
                  "package": "Example.Legacy",
                  "reason": "Intentional compatibility exception"
                }
              ]
            }
            """);
        var root = Path.Combine(Path.GetTempPath(), "policy-target");
        var policy = AnalysisPolicyResolver.Resolve(configuration, root);
        var diagnostics = new[]
        {
            CreateDiagnostic("PM001", Path.Combine(root, "src", "App.csproj"), "Unused.Package"),
            CreateDiagnostic("PM002", Path.Combine(root, "src", "App.csproj"), "Drift.Package"),
            CreateDiagnostic("PM003", Path.Combine(root, "src", "Generated", "Generated.csproj"), "Generated.Package"),
            CreateDiagnostic("PM003", Path.Combine(root, "src", "Legacy", "Legacy.csproj"), "Example.Legacy"),
            CreateDiagnostic("PM004", Path.Combine(root, "src", "App.csproj"), "Duplicate.Package"),
        };

        var applied = policy.Apply(diagnostics, root);

        Assert.Equal(2, applied.Diagnostics.Count);
        Assert.Equal(DiagnosticSeverity.Error, applied.Diagnostics.Single(item => item.Code == "PM002").Severity);
        Assert.Contains(applied.Diagnostics, item => item.Code == "PM004");
        Assert.Equal("PM001", Assert.Single(applied.DisabledDiagnostics).Code);
        Assert.Contains("Generated", Assert.Single(applied.ExcludedDiagnostics).File, StringComparison.Ordinal);
        var suppressed = Assert.Single(applied.SuppressedDiagnostics);
        Assert.Equal("Example.Legacy", suppressed.Suppression.Package);
        Assert.NotEmpty(suppressed.Suppression.Reason);
    }

    [Theory]
    [InlineData("{}", "schemaVersion must be 1")]
    [InlineData("{\"schemaVersion\":2}", "schemaVersion must be 1")]
    [InlineData("{\"schemaVersion\":1,\"unknown\":true}", "unknown")]
    [InlineData("{\"schemaVersion\":1,\"failOn\":1}", "could not be converted")]
    [InlineData("{\"schemaVersion\":1,\"rules\":{\"PM999\":{}}}", "unknown diagnostic code")]
    [InlineData("{\"schemaVersion\":1,\"suppressions\":[{\"rule\":\"PM001\"}]}", "reason")]
    [InlineData("{\"schemaVersion\":1,\"exclude\":[\"../outside/**\"]}", "repository-relative")]
    [InlineData("{\"schemaVersion\":1,\"baseline\":\"../outside.json\"}", "repository-relative")]
    [InlineData("{\"schemaVersion\":1,\"timeouts\":{\"restoreSeconds\":0}}", "between 1 and 3600")]
    public void RejectsInvalidConfiguration(string json, string expectedMessage)
    {
        var exception = Assert.Throws<PackageMedicConfigurationException>(
            () => PackageMedicConfigurationLoader.Parse(json, "invalid.json"));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("invalid.json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsDuplicatePropertiesCaseInsensitively()
    {
        const string json = "{\"schemaVersion\":1,\"failOn\":\"warning\",\"FailOn\":\"error\"}";

        var exception = Assert.Throws<PackageMedicConfigurationException>(
            () => PackageMedicConfigurationLoader.Parse(json));

        Assert.Contains("duplicate property", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadsConfigurationFromDisk()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"package-medic-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ".packagemedic.json");
        try
        {
            File.WriteAllText(path, "{\"schemaVersion\":1,\"failOn\":\"none\"}");

            var configuration = PackageMedicConfigurationLoader.Load(path);

            Assert.Equal(PolicyFailureLevel.None, configuration.FailOn);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Diagnostic CreateDiagnostic(string code, string file, string package) => new(
        code,
        DiagnosticSeverity.Warning,
        $"Finding for {package}",
        $"'{package}' needs review.",
        file,
        file,
        1,
        $"PackageReference {package}",
        "Review it.",
        DiagnosticConfidence.High);
}
