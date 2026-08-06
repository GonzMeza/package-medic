using System.Security;
using System.Text;
using PackageMedic.Core;

namespace PackageMedic.Core.Tests;

public sealed class TrxTestResultParserTests
{
    private static readonly Guid PassedTestId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FailedTestId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SkippedTestId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task AggregatesFilesAndRetainsOnlyStableFailureIdentities()
    {
        using var root = OwnedTemporaryDirectory.Create(Directory.GetCurrentDirectory());
        var nested = Path.Combine(root.DirectoryPath, "nested");
        Directory.CreateDirectory(nested);
        await WriteAsync(
            Path.Combine(root.DirectoryPath, "first.trx"),
            CreateTrx(
                new TestResult(PassedTestId, "Passed test", "Passed", "Tests.Passing", "Runs"),
                new TestResult(SkippedTestId, "Skipped test", "NotExecuted", "Tests.Skipped", "Waits")));
        await WriteAsync(
            Path.Combine(nested, "second.TRX"),
            CreateTrx(
                new TestResult(
                    FailedTestId,
                    "Failed test C:\\agent\\secret",
                    "Failed",
                    "Tests.DependencyTests",
                    "RejectsDowngrade",
                    IncludeUntrustedOutput: true)));

        var evidence = await new TrxTestResultParser().ParseAsync(
            root,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TrxTestEvidenceStatus.Failed, evidence.Status);
        Assert.Equal(3, evidence.Total);
        Assert.Equal(1, evidence.Passed);
        Assert.Equal(1, evidence.Failed);
        Assert.Equal(1, evidence.Skipped);
        Assert.Equal(["Tests.DependencyTests.RejectsDowngrade"], evidence.FailedTestIdentities);
        Assert.DoesNotContain(evidence.FailedTestIdentities, identity => identity.Contains("agent", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(evidence.FailedTestIdentities, identity => identity.Contains("2026", StringComparison.Ordinal));
        Assert.False(evidence.HasAdditionalFailedTests);
        Assert.Equal(
            VerificationStageEvidence.Failed(VerificationFailureKind.TestsFailed),
            evidence.ToVerificationStageEvidence());
    }

    [Fact]
    public async Task ZeroTestsIsIncompleteInsteadOfPassing()
    {
        using var root = OwnedTemporaryDirectory.Create(Directory.GetCurrentDirectory());
        await WriteAsync(Path.Combine(root.DirectoryPath, "empty.trx"), CreateTrx());

        var evidence = await new TrxTestResultParser().ParseAsync(
            root,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TrxTestEvidenceStatus.Incomplete, evidence.Status);
        Assert.Equal(TrxTestEvidenceErrorKind.NoTestsDiscovered, evidence.Error?.Kind);
        Assert.Equal(
            VerificationStageEvidence.Incomplete(VerificationFailureKind.NoTestsDiscovered),
            evidence.ToVerificationStageEvidence());
    }

    [Fact]
    public async Task MissingTrxFilesIsTypedIncompleteEvidence()
    {
        using var root = OwnedTemporaryDirectory.Create(Directory.GetCurrentDirectory());
        await WriteAsync(Path.Combine(root.DirectoryPath, "test.log"), "not a result file");

        var evidence = await new TrxTestResultParser().ParseAsync(
            root,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TrxTestEvidenceStatus.Incomplete, evidence.Status);
        Assert.Equal(TrxTestEvidenceErrorKind.NoResultFiles, evidence.Error?.Kind);
        Assert.Equal(VerificationFailureKind.TestResultsUnavailable, evidence.ToVerificationStageEvidence().FailureKind);
    }

    [Theory]
    [InlineData("<TestRun><Results></TestRun>", TrxTestEvidenceErrorKind.MalformedXml)]
    [InlineData("<NotATestRun />", TrxTestEvidenceErrorKind.InvalidDocument)]
    public async Task MalformedOrInvalidXmlIsTypedIncompleteEvidence(
        string xml,
        TrxTestEvidenceErrorKind expectedError)
    {
        using var root = OwnedTemporaryDirectory.Create(Directory.GetCurrentDirectory());
        await WriteAsync(Path.Combine(root.DirectoryPath, "unsafe.trx"), xml);

        var evidence = await new TrxTestResultParser().ParseAsync(
            root,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TrxTestEvidenceStatus.Incomplete, evidence.Status);
        Assert.Equal(expectedError, evidence.Error?.Kind);
    }

    [Fact]
    public async Task ProhibitsDtdsAndNeverResolvesExternalEntities()
    {
        using var root = OwnedTemporaryDirectory.Create(Directory.GetCurrentDirectory());
        var xml = "<!DOCTYPE TestRun [<!ENTITY external SYSTEM 'file:///does-not-exist'>]>" +
                  "<TestRun><Results/><ResultSummary><Counters total='0' executed='0' passed='0' failed='0'/></ResultSummary></TestRun>";
        await WriteAsync(Path.Combine(root.DirectoryPath, "external-entity.trx"), xml);

        var evidence = await new TrxTestResultParser().ParseAsync(
            root,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TrxTestEvidenceStatus.Incomplete, evidence.Status);
        Assert.Equal(TrxTestEvidenceErrorKind.MalformedXml, evidence.Error?.Kind);
    }

    [Theory]
    [InlineData("passed=\"1\"", "passed=\"0\"")]
    [InlineData("executed=\"1\"", "executed=\"0\"")]
    [InlineData("ResultSummary outcome=\"Completed\"", "ResultSummary outcome=\"Failed\"")]
    public async Task ContradictorySummaryOrCountersCanNeverProducePass(string original, string replacement)
    {
        using var root = OwnedTemporaryDirectory.Create(Directory.GetCurrentDirectory());
        var xml = CreateTrx(new TestResult(PassedTestId, "Pass", "Passed", "Tests.Sample", "Passes"))
            .Replace(original, replacement, StringComparison.Ordinal);
        await WriteAsync(Path.Combine(root.DirectoryPath, "contradictory.trx"), xml);

        var evidence = await new TrxTestResultParser().ParseAsync(
            root,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TrxTestEvidenceStatus.Incomplete, evidence.Status);
        Assert.Equal(TrxTestEvidenceErrorKind.ContradictoryCounts, evidence.Error?.Kind);
    }

    [Fact]
    public async Task RetainedFailureLimitBoundsEvidenceWithoutHidingTheFailureVerdict()
    {
        using var root = OwnedTemporaryDirectory.Create(Directory.GetCurrentDirectory());
        await WriteAsync(
            Path.Combine(root.DirectoryPath, "failed.trx"),
            CreateTrx(
                new TestResult(FailedTestId, "First", "Failed", "Tests.Sample", "First"),
                new TestResult(Guid.Parse("44444444-4444-4444-4444-444444444444"), "Second", "Failed", "Tests.Sample", "Second")));
        var limits = TrxTestResultLimits.Default with { MaximumRetainedFailures = 1 };

        var evidence = await new TrxTestResultParser().ParseAsync(
            root,
            limits,
            TestContext.Current.CancellationToken);

        Assert.Equal(TrxTestEvidenceStatus.Failed, evidence.Status);
        Assert.Equal(2, evidence.Failed);
        Assert.Single(evidence.FailedTestIdentities);
        Assert.True(evidence.HasAdditionalFailedTests);
    }

    [Fact]
    public async Task EnforcesResultFileCountLimitBeforeParsing()
    {
        using var root = OwnedTemporaryDirectory.Create(Directory.GetCurrentDirectory());
        var xml = CreateTrx(new TestResult(PassedTestId, "Pass", "Passed", "Tests.Sample", "Passes"));
        await WriteAsync(Path.Combine(root.DirectoryPath, "one.trx"), xml);
        await WriteAsync(Path.Combine(root.DirectoryPath, "two.trx"), xml);
        var limits = TrxTestResultLimits.Default with { MaximumFiles = 1 };

        var evidence = await new TrxTestResultParser().ParseAsync(
            root,
            limits,
            TestContext.Current.CancellationToken);

        Assert.Equal(TrxTestEvidenceStatus.Incomplete, evidence.Status);
        Assert.Equal(TrxTestEvidenceErrorKind.ResultFileLimitExceeded, evidence.Error?.Kind);
    }

    [Fact]
    public async Task EnforcesPerFileAndAggregateByteLimits()
    {
        var xml = CreateTrx(new TestResult(PassedTestId, "Pass", "Passed", "Tests.Sample", "Passes"));
        var byteCount = Encoding.UTF8.GetByteCount(xml) + Encoding.UTF8.GetPreamble().Length;

        using (var root = OwnedTemporaryDirectory.Create(Directory.GetCurrentDirectory()))
        {
            await WriteAsync(Path.Combine(root.DirectoryPath, "large.trx"), xml);
            var limits = TrxTestResultLimits.Default with
            {
                MaximumFileBytes = byteCount - 1,
                MaximumTotalBytes = byteCount * 2L,
            };

            var evidence = await new TrxTestResultParser().ParseAsync(
                root,
                limits,
                TestContext.Current.CancellationToken);
            Assert.Equal(TrxTestEvidenceErrorKind.ResultFileByteLimitExceeded, evidence.Error?.Kind);
        }

        using (var root = OwnedTemporaryDirectory.Create(Directory.GetCurrentDirectory()))
        {
            await WriteAsync(Path.Combine(root.DirectoryPath, "one.trx"), xml);
            await WriteAsync(Path.Combine(root.DirectoryPath, "two.trx"), xml);
            var limits = TrxTestResultLimits.Default with
            {
                MaximumFileBytes = byteCount,
                MaximumTotalBytes = (byteCount * 2L) - 1,
            };

            var evidence = await new TrxTestResultParser().ParseAsync(
                root,
                limits,
                TestContext.Current.CancellationToken);
            Assert.Equal(TrxTestEvidenceErrorKind.TotalByteLimitExceeded, evidence.Error?.Kind);
        }
    }

    [Fact]
    public async Task EnforcesXmlDepthTestCountAndIdentityLimits()
    {
        using (var root = OwnedTemporaryDirectory.Create(Directory.GetCurrentDirectory()))
        {
            var xml = "<TestRun><Results/><Nested><One><Two/></One></Nested>" +
                      "<ResultSummary><Counters total='0' executed='0' passed='0' failed='0'/></ResultSummary></TestRun>";
            await WriteAsync(Path.Combine(root.DirectoryPath, "deep.trx"), xml);
            var evidence = await new TrxTestResultParser().ParseAsync(
                root,
                TrxTestResultLimits.Default with { MaximumXmlDepth = 2 },
                TestContext.Current.CancellationToken);
            Assert.Equal(TrxTestEvidenceErrorKind.XmlDepthLimitExceeded, evidence.Error?.Kind);
        }

        using (var root = OwnedTemporaryDirectory.Create(Directory.GetCurrentDirectory()))
        {
            await WriteAsync(
                Path.Combine(root.DirectoryPath, "many.trx"),
                CreateTrx(
                    new TestResult(PassedTestId, "First", "Passed", "Tests.Sample", "First"),
                    new TestResult(SkippedTestId, "Second", "Passed", "Tests.Sample", "Second")));
            var evidence = await new TrxTestResultParser().ParseAsync(
                root,
                TrxTestResultLimits.Default with { MaximumTestCount = 1 },
                TestContext.Current.CancellationToken);
            Assert.Equal(TrxTestEvidenceErrorKind.TestCountLimitExceeded, evidence.Error?.Kind);
        }

        using (var root = OwnedTemporaryDirectory.Create(Directory.GetCurrentDirectory()))
        {
            await WriteAsync(
                Path.Combine(root.DirectoryPath, "identity.trx"),
                CreateTrx(new TestResult(FailedTestId, "Failure", "Failed", "Tests.Sample", "Fails")));
            var evidence = await new TrxTestResultParser().ParseAsync(
                root,
                TrxTestResultLimits.Default with { MaximumIdentityLength = 16 },
                TestContext.Current.CancellationToken);
            Assert.Equal(TrxTestEvidenceErrorKind.IdentityLengthLimitExceeded, evidence.Error?.Kind);
        }
    }

    [Fact]
    public async Task RejectsReparsePointsAnywhereBelowTheOwnedRoot()
    {
        using var root = OwnedTemporaryDirectory.Create(Directory.GetCurrentDirectory());
        var external = Path.Combine(Path.GetTempPath(), $"packagemedic-external-{Guid.NewGuid():N}.trx");
        var link = Path.Combine(root.DirectoryPath, "linked.trx");
        await WriteAsync(external, CreateTrx());
        try
        {
            try
            {
                File.CreateSymbolicLink(link, external);
            }
            catch (Exception exception) when (exception is
                IOException or
                UnauthorizedAccessException or
                PlatformNotSupportedException)
            {
                return;
            }

            var evidence = await new TrxTestResultParser().ParseAsync(
                root,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(TrxTestEvidenceStatus.Incomplete, evidence.Status);
            Assert.Equal(TrxTestEvidenceErrorKind.UnsafeResultsEntry, evidence.Error?.Kind);
        }
        finally
        {
            if (File.Exists(link))
            {
                File.Delete(link);
            }

            File.Delete(external);
        }
    }

    private static async Task WriteAsync(string path, string content) =>
        await File.WriteAllTextAsync(path, content, Encoding.UTF8, TestContext.Current.CancellationToken);

    private static string CreateTrx(params TestResult[] results)
    {
        var passed = results.Count(result => result.Outcome == "Passed");
        var failed = results.Count(result => result.Outcome == "Failed");
        var skipped = results.Count(result => result.Outcome == "NotExecuted");
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        builder.Append("<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\">");
        builder.Append("<Results>");
        foreach (var result in results)
        {
            builder.Append("<UnitTestResult testId=\"")
                .Append(result.Id.ToString("D"))
                .Append("\" testName=\"")
                .Append(SecurityElement.Escape(result.Name))
                .Append("\" outcome=\"")
                .Append(result.Outcome)
                .Append("\" duration=\"00:00:00.1234567\" startTime=\"2026-08-04T12:00:00Z\" endTime=\"2026-08-04T12:00:01Z\"");
            if (result.IncludeUntrustedOutput)
            {
                builder.Append("><Output><StdOut>C:\\agent\\secret token=do-not-retain</StdOut></Output></UnitTestResult>");
            }
            else
            {
                builder.Append(" />");
            }
        }

        builder.Append("</Results><TestDefinitions>");
        foreach (var result in results)
        {
            builder.Append("<UnitTest id=\"")
                .Append(result.Id.ToString("D"))
                .Append("\" name=\"")
                .Append(SecurityElement.Escape(result.Name))
                .Append("\"><TestMethod className=\"")
                .Append(SecurityElement.Escape(result.ClassName))
                .Append("\" name=\"")
                .Append(SecurityElement.Escape(result.MethodName))
                .Append("\" /></UnitTest>");
        }

        builder.Append("</TestDefinitions><ResultSummary outcome=\"")
            .Append(failed == 0 ? "Completed" : "Failed")
            .Append("\"><Counters total=\"")
            .Append(results.Length)
            .Append("\" executed=\"")
            .Append(results.Length - skipped)
            .Append("\" passed=\"")
            .Append(passed)
            .Append("\" failed=\"")
            .Append(failed)
            .Append("\" error=\"0\" timeout=\"0\" aborted=\"0\" inconclusive=\"0\" passedButRunAborted=\"0\" notRunnable=\"0\" notExecuted=\"")
            .Append(skipped)
            .Append("\" disconnected=\"0\" warning=\"0\" completed=\"0\" inProgress=\"0\" pending=\"0\" /></ResultSummary></TestRun>");
        return builder.ToString();
    }

    private sealed record TestResult(
        Guid Id,
        string Name,
        string Outcome,
        string ClassName,
        string MethodName,
        bool IncludeUntrustedOutput = false);
}
