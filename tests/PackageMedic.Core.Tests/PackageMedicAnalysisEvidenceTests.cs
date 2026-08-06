using System.Text;
using System.Text.Json;
using PackageMedic.Core;

namespace PackageMedic.Core.Tests;

public sealed class PackageMedicAnalysisEvidenceTests
{
    private const string GitSha = "0123456789abcdef0123456789abcdef01234567";
    private const string Sha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string OtherSha256 = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public void PublishedSchemaMatchesTheVersionedAnalysisPredicateContract()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "schemas")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        using var schema = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(directory!.FullName, "schemas", "packagemedic-analysis-attestation.schema.json")));
        var root = schema.RootElement;
        Assert.Equal(InTotoEvidenceSerializer.StatementType, root.GetProperty("properties").GetProperty("_type").GetProperty("const").GetString());
        Assert.Equal(
            InTotoEvidenceSerializer.PackageMedicAnalysisPredicateType,
            root.GetProperty("properties").GetProperty("predicateType").GetProperty("const").GetString());
        Assert.Equal(
            InTotoEvidenceSerializer.PackageMedicAnalysisSchemaVersion,
            root.GetProperty("properties").GetProperty("predicate").GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32());
    }

    [Fact]
    public void WritesVersionedPortableAnalysisEvidenceBoundToAnImmutableGitSubject()
    {
        var evidence = new PackageMedicAnalysisEvidence(
            "src/My App/PackageMedic.sln",
            GitSha,
            Sha256,
            "0.6.0-preview.1+build.5",
            VerificationLevel.Test,
            VerificationVerdict.Pass,
            PackageMedicAnalysisCompleteness.Complete,
            PackageMedicConfigurationFingerprint.FromSha256(Sha256))
        {
            SbomSha256 = OtherSha256,
        };

        var json = InTotoEvidenceSerializer.SerializePackageMedicAnalysisStatement(GitSha, evidence);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var subject = Assert.Single(root.GetProperty("subject").EnumerateArray());
        var predicate = root.GetProperty("predicate");
        Assert.Equal(InTotoEvidenceSerializer.StatementType, root.GetProperty("_type").GetString());
        Assert.Equal(
            InTotoEvidenceSerializer.PackageMedicAnalysisPredicateType,
            root.GetProperty("predicateType").GetString());
        Assert.Equal("repository", subject.GetProperty("name").GetString());
        Assert.Equal(GitSha, subject.GetProperty("digest").GetProperty("gitCommit").GetString());
        Assert.Equal(1, predicate.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("src/My App/PackageMedic.sln", predicate.GetProperty("target").GetString());
        Assert.Equal("PackageMedic", predicate.GetProperty("tool").GetProperty("name").GetString());
        Assert.Equal("0.6.0-preview.1+build.5", predicate.GetProperty("tool").GetProperty("version").GetString());
        Assert.Equal("sha256", predicate.GetProperty("configuration").GetProperty("state").GetString());
        Assert.Equal(Sha256, predicate.GetProperty("configuration").GetProperty("sha256").GetString());
        Assert.Equal(GitSha, predicate.GetProperty("comparison").GetProperty("baselineGitCommit").GetString());
        Assert.Equal(Sha256, predicate.GetProperty("comparison").GetProperty("sha256").GetString());
        Assert.Equal("test", predicate.GetProperty("verification").GetProperty("level").GetString());
        Assert.Equal("pass", predicate.GetProperty("verification").GetProperty("status").GetString());
        Assert.Equal("complete", predicate.GetProperty("verification").GetProperty("completeness").GetString());
        Assert.Equal("sha256", predicate.GetProperty("sbom").GetProperty("algorithm").GetString());
        Assert.Equal(OtherSha256, predicate.GetProperty("sbom").GetProperty("digest").GetString());
        Assert.DoesNotContain("timestamp", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signature", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("slsa", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RepresentsAbsentConfigurationAndSbomExplicitlyWithoutInventingEvidence()
    {
        var evidence = Evidence(
            target: ".",
            level: VerificationLevel.Restore,
            status: VerificationVerdict.NoChange,
            configuration: PackageMedicConfigurationFingerprint.None);

        var json = InTotoEvidenceSerializer.SerializePackageMedicAnalysisStatement(GitSha, evidence);

        using var document = JsonDocument.Parse(json);
        var predicate = document.RootElement.GetProperty("predicate");
        var configuration = predicate.GetProperty("configuration");
        Assert.Equal("none", configuration.GetProperty("state").GetString());
        Assert.Single(configuration.EnumerateObject());
        Assert.False(predicate.TryGetProperty("sbom", out _));
        Assert.Equal("noChange", predicate.GetProperty("verification").GetProperty("status").GetString());
    }

    [Fact]
    public void ProducesIdenticalUtf8BytesForEquivalentDigestCasing()
    {
        var firstEvidence = Evidence(
            configuration: PackageMedicConfigurationFingerprint.FromSha256(Sha256.ToUpperInvariant())) with
        {
            SbomSha256 = OtherSha256.ToUpperInvariant(),
        };
        var secondEvidence = firstEvidence with
        {
            ConfigurationFingerprint = PackageMedicConfigurationFingerprint.FromSha256(Sha256),
            SbomSha256 = OtherSha256,
        };

        var first = InTotoEvidenceSerializer.SerializePackageMedicAnalysisStatementUtf8(
            GitSha.ToUpperInvariant(),
            firstEvidence);
        var second = InTotoEvidenceSerializer.SerializePackageMedicAnalysisStatementUtf8(
            GitSha,
            secondEvidence);
        var text = InTotoEvidenceSerializer.SerializePackageMedicAnalysisStatement(GitSha, secondEvidence);

        Assert.Equal(first, second);
        Assert.Equal(Encoding.UTF8.GetBytes(text), second);
        Assert.Equal(text, Encoding.UTF8.GetString(first));
    }

    [Fact]
    public void BindsEvidenceBytesToTheBaselineCommitAndComparisonReport()
    {
        var original = Evidence();
        var differentBaseline = original with
        {
            BaselineGitCommit = "abcdef0123456789abcdef0123456789abcdef01",
        };
        var differentComparison = original with
        {
            ComparisonSha256 = OtherSha256,
        };

        var originalBytes = InTotoEvidenceSerializer.SerializePackageMedicAnalysisStatementUtf8(GitSha, original);
        var baselineBytes = InTotoEvidenceSerializer.SerializePackageMedicAnalysisStatementUtf8(GitSha, differentBaseline);
        var comparisonBytes = InTotoEvidenceSerializer.SerializePackageMedicAnalysisStatementUtf8(GitSha, differentComparison);

        Assert.NotEqual(originalBytes, baselineBytes);
        Assert.NotEqual(originalBytes, comparisonBytes);
        Assert.NotEqual(baselineBytes, comparisonBytes);
    }

    [Theory]
    [InlineData(VerificationLevel.Restore, "restore")]
    [InlineData(VerificationLevel.Build, "build")]
    [InlineData(VerificationLevel.Test, "test")]
    public void WritesEveryVerificationLevel(VerificationLevel level, string expected)
    {
        var json = InTotoEvidenceSerializer.SerializePackageMedicAnalysisStatement(
            GitSha,
            Evidence(level: level));
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            expected,
            document.RootElement
                .GetProperty("predicate")
                .GetProperty("verification")
                .GetProperty("level")
                .GetString());
    }

    [Theory]
    [InlineData(VerificationVerdict.Pass, "pass")]
    [InlineData(VerificationVerdict.Reject, "reject")]
    [InlineData(VerificationVerdict.NoChange, "noChange")]
    [InlineData(VerificationVerdict.Incomplete, "incomplete")]
    public void WritesEveryVerificationStatus(VerificationVerdict status, string expected)
    {
        var completeness = status == VerificationVerdict.Incomplete
            ? PackageMedicAnalysisCompleteness.Incomplete
            : PackageMedicAnalysisCompleteness.Complete;
        var json = InTotoEvidenceSerializer.SerializePackageMedicAnalysisStatement(
            GitSha,
            Evidence(status: status, completeness: completeness));
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            expected,
            document.RootElement
                .GetProperty("predicate")
                .GetProperty("verification")
                .GetProperty("status")
                .GetString());
    }

    [Fact]
    public void RejectsContradictoryCompletenessOrUnknownVerificationValues()
    {
        Assert.Throws<ArgumentException>(() =>
            InTotoEvidenceSerializer.SerializePackageMedicAnalysisStatement(
                GitSha,
                Evidence(
                    status: VerificationVerdict.Pass,
                    completeness: PackageMedicAnalysisCompleteness.Incomplete)));
        Assert.Throws<ArgumentException>(() =>
            InTotoEvidenceSerializer.SerializePackageMedicAnalysisStatement(
                GitSha,
                Evidence(
                    status: VerificationVerdict.Incomplete,
                    completeness: PackageMedicAnalysisCompleteness.Complete)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InTotoEvidenceSerializer.SerializePackageMedicAnalysisStatement(
                GitSha,
                Evidence(level: (VerificationLevel)100)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InTotoEvidenceSerializer.SerializePackageMedicAnalysisStatement(
                GitSha,
                Evidence(status: (VerificationVerdict)100)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InTotoEvidenceSerializer.SerializePackageMedicAnalysisStatement(
                GitSha,
                Evidence(completeness: (PackageMedicAnalysisCompleteness)100)));
    }

    [Fact]
    public void RequiresGitAndConfigurationOrSbomDigestsWithExactLengths()
    {
        Assert.Throws<ArgumentException>(() =>
            InTotoEvidenceSerializer.SerializePackageMedicAnalysisStatement("abc", Evidence()));
        Assert.Throws<ArgumentException>(() =>
            InTotoEvidenceSerializer.SerializePackageMedicAnalysisStatement(
                new string('g', 40),
                Evidence()));
        Assert.Throws<ArgumentException>(() => PackageMedicConfigurationFingerprint.FromSha256(GitSha));
        Assert.Throws<ArgumentException>(() => PackageMedicConfigurationFingerprint.FromSha256(new string('g', 64)));
        Assert.Throws<ArgumentException>(() =>
            InTotoEvidenceSerializer.SerializePackageMedicAnalysisStatement(
                GitSha,
                Evidence() with { SbomSha256 = GitSha }));
        Assert.Throws<ArgumentException>(() =>
            InTotoEvidenceSerializer.SerializePackageMedicAnalysisStatement(
                GitSha,
                Evidence() with { SbomSha256 = new string('g', 64) }));
    }

    [Theory]
    [InlineData("C:\\Users\\alice\\repo")]
    [InlineData("/home/alice/repo")]
    [InlineData("../repo")]
    [InlineData("src/../repo")]
    [InlineData("src\\App.sln")]
    [InlineData("https://example.test/repo")]
    [InlineData("https://alice:password@example.test/repo?token=secret")]
    [InlineData("src/App.sln?token=secret")]
    [InlineData("src/App.sln#fragment")]
    [InlineData("src/%2e%2e/App.sln")]
    [InlineData("~/repo")]
    [InlineData("token=secret")]
    [InlineData("src/api-key=secret/App.sln")]
    public void RejectsNonPortableTargets(string target)
    {
        Assert.Throws<ArgumentException>(() =>
            InTotoEvidenceSerializer.SerializePackageMedicAnalysisStatement(
                GitSha,
                Evidence(target: target)));
    }

    [Fact]
    public void BoundsTargetsAndRequiresAValidToolSemanticVersion()
    {
        Assert.Throws<ArgumentException>(() =>
            InTotoEvidenceSerializer.SerializePackageMedicAnalysisStatement(
                GitSha,
                Evidence(target: new string('a', 513))));
        Assert.Throws<ArgumentException>(() =>
            InTotoEvidenceSerializer.SerializePackageMedicAnalysisStatement(
                GitSha,
                Evidence(version: "v0.6")));
        Assert.Throws<ArgumentException>(() =>
            InTotoEvidenceSerializer.SerializePackageMedicAnalysisStatement(
                GitSha,
                Evidence(version: "01.6.0")));
        Assert.Throws<ArgumentException>(() =>
            InTotoEvidenceSerializer.SerializePackageMedicAnalysisStatement(
                GitSha,
                Evidence(version: "0.6.0-01")));
        Assert.Throws<ArgumentException>(() =>
            InTotoEvidenceSerializer.SerializePackageMedicAnalysisStatement(
                GitSha,
                Evidence(version: "0.6.0?token=secret")));
        Assert.Throws<ArgumentException>(() =>
            InTotoEvidenceSerializer.SerializePackageMedicAnalysisStatement(
                GitSha,
                Evidence(version: $"1.0.0+{new string('a', 129)}")));
    }

    private static PackageMedicAnalysisEvidence Evidence(
        string target = "PackageMedic.sln",
        string version = "0.6.0",
        VerificationLevel level = VerificationLevel.Build,
        VerificationVerdict status = VerificationVerdict.Pass,
        PackageMedicAnalysisCompleteness completeness = PackageMedicAnalysisCompleteness.Complete,
        PackageMedicConfigurationFingerprint? configuration = null) => new(
        target,
        GitSha,
        Sha256,
        version,
        level,
        status,
        completeness,
        configuration ?? PackageMedicConfigurationFingerprint.None);
}
