using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PackageMedic.Core;

namespace PackageMedic.Core.Tests;

public sealed class InTotoEvidenceSerializerTests
{
    private const string GitSha = "0123456789abcdef0123456789abcdef01234567";
    private const string Sha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void CycloneDxStatementUsesV1ContractAndEmbedsTheBomObject()
    {
        var json = InTotoEvidenceSerializer.SerializeCycloneDxStatement(
            [InTotoResourceDescriptor.FromGitCommit("repository", GitSha)],
            Utf8("""
                {
                  "specVersion": "1.7",
                  "bomFormat": "CycloneDX",
                  "version": 1,
                  "components": []
                }
                """));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(InTotoEvidenceSerializer.StatementType, root.GetProperty("_type").GetString());
        Assert.Equal(
            InTotoEvidenceSerializer.CycloneDxPredicateType,
            root.GetProperty("predicateType").GetString());
        Assert.Equal("CycloneDX", root.GetProperty("predicate").GetProperty("bomFormat").GetString());
        Assert.Equal("1.7", root.GetProperty("predicate").GetProperty("specVersion").GetString());
        Assert.Equal(
            GitSha,
            Assert.Single(root.GetProperty("subject").EnumerateArray())
                .GetProperty("digest")
                .GetProperty("gitCommit")
                .GetString());
        Assert.DoesNotContain("signature", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("slsa", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("timestamp", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CycloneDxStatementIsDeterministicAcrossSubjectAndObjectPropertyOrder()
    {
        var repository = InTotoResourceDescriptor.FromGitCommit("repository", GitSha.ToUpperInvariant());
        var package = InTotoResourceDescriptor.FromSha256("PackageMedic.Tool.nupkg", Sha256.ToUpperInvariant());
        var first = InTotoEvidenceSerializer.SerializeCycloneDxStatement(
            [repository, package],
            Utf8("""{"specVersion":"1.7","version":1,"bomFormat":"CycloneDX","components":[]}"""));
        var second = InTotoEvidenceSerializer.SerializeCycloneDxStatement(
            [package, repository],
            Utf8("""{"components":[],"bomFormat":"CycloneDX","version":1,"specVersion":"1.7"}"""));

        Assert.Equal(first, second);
        Assert.True(
            first.IndexOf("PackageMedic.Tool.nupkg", StringComparison.Ordinal) <
            first.IndexOf("repository", StringComparison.Ordinal));
        Assert.Contains(Sha256, first, StringComparison.Ordinal);
        Assert.DoesNotContain(Sha256.ToUpperInvariant(), first, StringComparison.Ordinal);
    }

    [Fact]
    public void CycloneDxStatementRejectsInvalidOrSensitivePredicates()
    {
        var subject = InTotoResourceDescriptor.FromSha256("artifact", Sha256);

        Assert.Throws<ArgumentException>(() => InTotoEvidenceSerializer.SerializeCycloneDxStatement(
            [subject],
            Utf8("[]")));
        Assert.Throws<ArgumentException>(() => InTotoEvidenceSerializer.SerializeCycloneDxStatement(
            [subject],
            Utf8("{\"name\":\"not-a-bom\"}")));
        Assert.Throws<ArgumentException>(() => InTotoEvidenceSerializer.SerializeCycloneDxStatement(
            [subject],
            Utf8("{\"bomFormat\":\"CycloneDX\",\"timestamp\":\"2026-01-01T00:00:00Z\"}")));
        Assert.Throws<ArgumentException>(() => InTotoEvidenceSerializer.SerializeCycloneDxStatement(
            [subject],
            Utf8("{\"bomFormat\":\"CycloneDX\",\"path\":\"C:\\\\Users\\\\alice\\\\repo\"}")));
        Assert.Throws<ArgumentException>(() => InTotoEvidenceSerializer.SerializeCycloneDxStatement(
            [subject],
            Utf8("{\"bomFormat\":\"CycloneDX\",\"token\":\"secret\"}")));
        Assert.Throws<ArgumentException>(() => InTotoEvidenceSerializer.SerializeCycloneDxStatement(
            [subject],
            Utf8("{\"bomFormat\":\"CycloneDX\",\"bomFormat\":\"CycloneDX\"}")));
    }

    [Fact]
    public void ResourceDescriptorsRequireExplicitValidImmutableDigestsAndPortableNames()
    {
        var git = InTotoResourceDescriptor.FromGitCommit("repository", GitSha.ToUpperInvariant());
        var gitSha256 = InTotoResourceDescriptor.FromGitCommit("repository-sha256", Sha256);
        var sha = InTotoResourceDescriptor.FromSha256("artifact.nupkg", Sha256.ToUpperInvariant());

        Assert.Equal("gitCommit", git.DigestAlgorithm);
        Assert.Equal(GitSha, git.Digest);
        Assert.Equal("gitCommit", gitSha256.DigestAlgorithm);
        Assert.Equal(Sha256, gitSha256.Digest);
        Assert.Equal("sha256", sha.DigestAlgorithm);
        Assert.Equal(Sha256, sha.Digest);
        Assert.Throws<ArgumentException>(() => InTotoResourceDescriptor.FromGitCommit("repository", "abc"));
        Assert.Throws<ArgumentException>(() => InTotoResourceDescriptor.FromSha256("repository", GitSha));
        Assert.Throws<ArgumentException>(() => InTotoResourceDescriptor.FromSha256("../artifact", Sha256));
        Assert.Throws<ArgumentException>(() => InTotoResourceDescriptor.FromSha256("C:\\artifact", Sha256));
        Assert.Throws<ArgumentException>(() => InTotoResourceDescriptor.FromSha256("https://example.test", Sha256));
        Assert.Throws<ArgumentException>(() => InTotoResourceDescriptor.FromSha256("token=secret", Sha256));
    }

    [Fact]
    public void TestResultStatementUsesTheVettedPredicateAndDeterministicStructuredEvidence()
    {
        var evidence = new InTotoTestResultEvidence(
            InTotoTestResult.Failed,
            [
                InTotoResourceDescriptor.FromSha256("windows-net8", Sha256),
                InTotoResourceDescriptor.FromGitCommit("ci-config", GitSha),
            ],
            ["ZetaTests.Passes", "AlphaTests.Passes"],
            ["WarningsTests.Obsolete"],
            ["ZetaTests.Fails", "BetaTests.Fails"]);

        var first = InTotoEvidenceSerializer.SerializeTestResultStatement(
            [InTotoResourceDescriptor.FromGitCommit("repository", GitSha)],
            evidence);
        var second = InTotoEvidenceSerializer.SerializeTestResultStatement(
            [InTotoResourceDescriptor.FromGitCommit("repository", GitSha)],
            evidence with
            {
                Configuration = evidence.Configuration.Reverse().ToArray(),
                PassedTests = evidence.PassedTests.Reverse().ToArray(),
                WarnedTests = evidence.WarnedTests.Reverse().ToArray(),
                FailedTests = evidence.FailedTests.Reverse().ToArray(),
            });

        Assert.Equal(first, second);
        using var document = JsonDocument.Parse(first);
        var root = document.RootElement;
        var predicate = root.GetProperty("predicate");
        Assert.Equal(
            InTotoEvidenceSerializer.TestResultPredicateType,
            root.GetProperty("predicateType").GetString());
        Assert.Equal("FAILED", predicate.GetProperty("result").GetString());
        Assert.Equal(
            ["AlphaTests.Passes", "ZetaTests.Passes"],
            predicate.GetProperty("passedTests").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            ["BetaTests.Fails", "ZetaTests.Fails"],
            predicate.GetProperty("failedTests").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            ["ci-config", "windows-net8"],
            predicate.GetProperty("configuration")
                .EnumerateArray()
                .Select(item => item.GetProperty("name").GetString()));
        Assert.False(predicate.TryGetProperty("url", out _));
    }

    [Fact]
    public void TestResultStatementRejectsContradictionsDuplicatesAndUnportableEvidence()
    {
        var subject = InTotoResourceDescriptor.FromGitCommit("repository", GitSha);
        var configuration = InTotoResourceDescriptor.FromSha256("linux-net8", Sha256);

        Assert.Throws<ArgumentException>(() => InTotoEvidenceSerializer.SerializeTestResultStatement(
            [subject],
            Evidence(InTotoTestResult.Passed, configuration, warned: ["Tests.Warning"])));
        Assert.Throws<ArgumentException>(() => InTotoEvidenceSerializer.SerializeTestResultStatement(
            [subject],
            Evidence(InTotoTestResult.Warned, configuration)));
        Assert.Throws<ArgumentException>(() => InTotoEvidenceSerializer.SerializeTestResultStatement(
            [subject],
            Evidence(InTotoTestResult.Failed, configuration)));
        Assert.Throws<ArgumentException>(() => InTotoEvidenceSerializer.SerializeTestResultStatement(
            [subject],
            Evidence(
                InTotoTestResult.Failed,
                configuration,
                passed: ["Tests.Same"],
                failed: ["Tests.Same"])));
        Assert.Throws<ArgumentException>(() => InTotoEvidenceSerializer.SerializeTestResultStatement(
            [subject],
            Evidence(
                InTotoTestResult.Passed,
                configuration,
                passed: ["Tests.Token token=secret"])));
        Assert.Throws<ArgumentException>(() => InTotoEvidenceSerializer.SerializeTestResultStatement(
            [subject],
            Evidence(
                InTotoTestResult.Passed,
                configuration,
                passed: ["C:\\private\\Tests.Pass"])));
    }

    [Fact]
    public void StatementsRejectEmptyDuplicateOrExcessiveSubjectsAndConfigurations()
    {
        var subject = InTotoResourceDescriptor.FromGitCommit("repository", GitSha);
        var duplicate = InTotoResourceDescriptor.FromSha256("repository", Sha256);
        var bom = Utf8("{\"bomFormat\":\"CycloneDX\"}");

        Assert.Throws<ArgumentException>(() =>
            InTotoEvidenceSerializer.SerializeCycloneDxStatement([], bom));
        Assert.Throws<ArgumentException>(() =>
            InTotoEvidenceSerializer.SerializeCycloneDxStatement([subject, duplicate], bom));
        Assert.Throws<ArgumentException>(() => InTotoEvidenceSerializer.SerializeCycloneDxStatement(
            Enumerable.Repeat(subject, InTotoEvidenceSerializer.MaximumSubjects + 1).ToArray(),
            bom));
        Assert.Throws<ArgumentException>(() => InTotoEvidenceSerializer.SerializeTestResultStatement(
            [subject],
            new InTotoTestResultEvidence(
                InTotoTestResult.Passed,
                [],
                [],
                [],
                [])));
        Assert.Throws<ArgumentException>(() => InTotoEvidenceSerializer.SerializeTestResultStatement(
            [subject],
            new InTotoTestResultEvidence(
                InTotoTestResult.Passed,
                Enumerable.Repeat(subject, InTotoEvidenceSerializer.MaximumConfigurations + 1).ToArray(),
                [],
                [],
                [])));
    }

    [Fact]
    public void TestResultStatementBoundsTestCountsAndNames()
    {
        var subject = InTotoResourceDescriptor.FromGitCommit("repository", GitSha);
        var configuration = InTotoResourceDescriptor.FromSha256("linux-net8", Sha256);
        var excessive = Enumerable.Repeat("Tests.Pass", InTotoEvidenceSerializer.MaximumTestNamesPerResult + 1)
            .ToArray();
        var longName = new string('a', InTotoEvidenceSerializer.MaximumTestNameLength + 1);

        Assert.Throws<ArgumentException>(() => InTotoEvidenceSerializer.SerializeTestResultStatement(
            [subject],
            Evidence(InTotoTestResult.Passed, configuration, passed: excessive)));
        Assert.Throws<ArgumentException>(() => InTotoEvidenceSerializer.SerializeTestResultStatement(
            [subject],
            Evidence(InTotoTestResult.Passed, configuration, passed: [longName])));
    }

    [Fact]
    public void EvidenceManifestHashesBytesAndIsDeterministicAcrossInputOrder()
    {
        var report = Utf8("{\"verdict\":\"pass\"}");
        var bom = Utf8("{\"bomFormat\":\"CycloneDX\"}");
        var first = InTotoEvidenceSerializer.SerializeEvidenceManifest(
        [
            EvidenceArtifact.FromBytes("report.json", report),
            EvidenceArtifact.FromBytes("bom.cdx.json", bom),
        ]);
        var second = InTotoEvidenceSerializer.SerializeEvidenceManifest(
        [
            EvidenceArtifact.FromBytes("bom.cdx.json", bom),
            EvidenceArtifact.FromBytes("report.json", report),
        ]);

        Assert.Equal(first, second);
        using var document = JsonDocument.Parse(first);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("sha256", root.GetProperty("algorithm").GetString());
        var artifacts = root.GetProperty("artifacts").EnumerateArray().ToArray();
        Assert.Equal(["bom.cdx.json", "report.json"], artifacts.Select(item => item.GetProperty("name").GetString()));
        Assert.Equal(bom.Length, artifacts[0].GetProperty("size").GetInt32());
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(bom.Span)).ToLowerInvariant(),
            artifacts[0].GetProperty("sha256").GetString());
        Assert.DoesNotContain("timestamp", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signature", first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvidenceManifestRejectsDuplicateUnsafeAndExcessiveEntries()
    {
        var bytes = Utf8("evidence");
        var item = EvidenceArtifact.FromBytes("report.json", bytes);

        Assert.Throws<ArgumentException>(() => EvidenceArtifact.FromBytes("../report.json", bytes));
        Assert.Throws<ArgumentException>(() => EvidenceArtifact.FromBytes("token=secret", bytes));
        Assert.Throws<ArgumentException>(() => InTotoEvidenceSerializer.SerializeEvidenceManifest([]));
        Assert.Throws<ArgumentException>(() =>
            InTotoEvidenceSerializer.SerializeEvidenceManifest([item, item]));
        Assert.Throws<ArgumentException>(() => InTotoEvidenceSerializer.SerializeEvidenceManifest(
            Enumerable.Repeat(item, InTotoEvidenceSerializer.MaximumManifestArtifacts + 1).ToArray()));
    }

    private static InTotoTestResultEvidence Evidence(
        InTotoTestResult result,
        InTotoResourceDescriptor configuration,
        IReadOnlyList<string>? passed = null,
        IReadOnlyList<string>? warned = null,
        IReadOnlyList<string>? failed = null) => new(
        result,
        [configuration],
        passed ?? [],
        warned ?? [],
        failed ?? []);

    private static ReadOnlyMemory<byte> Utf8(string value) => Encoding.UTF8.GetBytes(value);
}
