using System.ComponentModel;
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

    [Fact]
    public async Task ProcessOutputIsBoundedWhileTheStreamIsFullyConsumed()
    {
        using var reader = new StringReader(new string('x', 64));

        var output = await ProcessRunner.ReadBoundedAsync(reader, 16, CancellationToken.None);

        Assert.StartsWith(new string('x', 16), output, StringComparison.Ordinal);
        Assert.Contains("subprocess output truncated", output, StringComparison.Ordinal);
        Assert.True(output.Length < 128);
    }

    [Fact]
    public void AnalysisExecutionTimeoutsRejectUnsafeValues()
    {
        var options = new AnalysisExecutionOptions(TimeSpan.Zero, TimeSpan.FromMinutes(1));

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Fact]
    public void ProcessTerminationRecognizesDocumentedPlatformFailures()
    {
        Assert.True(ProcessRunner.IsExpectedTerminationException(new InvalidOperationException()));
        Assert.True(ProcessRunner.IsExpectedTerminationException(new Win32Exception()));
        Assert.True(ProcessRunner.IsExpectedTerminationException(new NotSupportedException()));
        Assert.True(ProcessRunner.IsExpectedTerminationException(new AggregateException()));
        Assert.False(ProcessRunner.IsExpectedTerminationException(new IOException()));
    }

    [Fact]
    public async Task RestoreTimeoutBecomesAnOperationalErrorInsteadOfHanging()
    {
        var target = Path.Combine(Path.GetTempPath(), "PackageMedic.Timeout", "App.csproj");
        var discovery = new DiscoveryResult(target, [], [target], [target]);
        var runner = new RestoreRunner(new NeverCompletesProcessRunner(), TimeSpan.FromMilliseconds(25));

        var result = await runner.RestoreAsync(discovery, null, CancellationToken.None);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Errors, item => item.Contains("timed out", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EvaluationTimeoutIsReportedWithProjectContext()
    {
        var target = Path.Combine(Path.GetTempPath(), "PackageMedic.Timeout", "App.csproj");
        var evaluator = new MsBuildProjectEvaluator(new NeverCompletesProcessRunner(), TimeSpan.FromMilliseconds(25));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => evaluator.EvaluateAsync(target, CancellationToken.None));

        Assert.Contains("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(target, exception.Message, StringComparison.Ordinal);
    }

    private sealed class NeverCompletesProcessRunner : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }
}
