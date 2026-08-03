using System.ComponentModel;
using System.Text.Json;
using PackageMedic.Core;

namespace PackageMedic.Core.Tests;

public sealed class InfrastructureTests
{
    [Fact]
    public void RejectsOversizedAssetsBeforeParsingThemIntoMemory()
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(AssetsFileReader.MaximumAssetsFileBytes + 1);
            }

            var exception = Assert.Throws<InvalidDataException>(
                () => new AssetsFileReader().Read(path, "App.csproj"));

            Assert.Contains("safety limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsOversizedSolutionsBeforeParsingThemIntoMemory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"packagemedic-solution-{Guid.NewGuid():N}.slnx");
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(ProjectDiscovery.MaximumSolutionFileBytes + 1);
            }

            var exception = Assert.Throws<InvalidDataException>(
                () => new ProjectDiscovery().Discover(path));

            Assert.Contains("safety limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsSolutionXmlDocumentTypesAsHandledInvalidData()
    {
        var path = Path.Combine(Path.GetTempPath(), $"packagemedic-solution-{Guid.NewGuid():N}.slnx");
        try
        {
            File.WriteAllText(
                path,
                "<!DOCTYPE Solution [<!ENTITY example SYSTEM 'file:///outside'>]><Solution>&example;</Solution>");

            var exception = Assert.Throws<InvalidDataException>(
                () => new ProjectDiscovery().Discover(path));

            Assert.Contains("valid safe XML", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

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
                    "net9.0": { "Direct/1.0.0": {} },
                    "net8.0/win-x64": { "RidOnly/3.0.0": {} }
                  },
                  "libraries": {
                    "Direct/1.0.0": { "type": "package" },
                    "Transitive/2.0.0": { "type": "package" },
                    "RidOnly/3.0.0": { "type": "package" }
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
            Assert.Equal(4, result.PackageInventory.Count);
            Assert.Contains(
                result.PackageInventory,
                item => item.Id == "Direct" && item.Framework == "net8.0" &&
                        item.ResolvedVersion == "1.0.0" && item.DependencyKind == PackageDependencyKind.Direct);
            Assert.Contains(
                result.PackageInventory,
                item => item.Id == "Transitive" && item.Framework == "net8.0" &&
                        item.ResolvedVersion == "2.0.0" && item.DependencyKind == PackageDependencyKind.Transitive);
            Assert.Contains(
                result.PackageInventory,
                item => item.Id == "RidOnly" && item.Framework == "net8.0" &&
                        item.RuntimeIdentifier == "win-x64" && item.ResolvedVersion == "3.0.0");
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal("NU1605", diagnostic.OriginalCode);
        }
        finally
        {
            File.Delete(temporaryFile);
        }
    }

    [Fact]
    public void DiscoverySkipsGeneratedDependencyAndReportDirectories()
    {
        var root = Directory.CreateTempSubdirectory("PackageMedic.Discovery.");
        try
        {
            var project = Path.Combine(root.FullName, "App.csproj");
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            foreach (var generated in new[] { "artifacts", ".next", ".wrangler", "dist", "out" })
            {
                var directory = Directory.CreateDirectory(Path.Combine(root.FullName, generated));
                File.WriteAllText(Path.Combine(directory.FullName, "Generated.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            }

            var discovery = new ProjectDiscovery().Discover(root.FullName);

            Assert.Equal([project], discovery.Projects);
            Assert.Equal([project], discovery.RestoreTargets);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void DiscoveryDoesNotFollowDirectorySymbolicLinksOutsideTheScanRoot()
    {
        var root = Directory.CreateTempSubdirectory("PackageMedic.DiscoveryRoot.");
        var outside = Directory.CreateTempSubdirectory("PackageMedic.DiscoveryOutside.");
        try
        {
            var project = Path.Combine(root.FullName, "App.csproj");
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(
                Path.Combine(outside.FullName, "Outside.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(root.FullName, "linked"), outside.FullName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var discovery = new ProjectDiscovery().Discover(root.FullName);

            Assert.Equal([project], discovery.Projects);
        }
        finally
        {
            root.Delete(recursive: true);
            outside.Delete(recursive: true);
        }
    }

    [Fact]
    public void SolutionCannotReachAProjectThroughAnEscapingSymbolicLink()
    {
        var root = Directory.CreateTempSubdirectory("PackageMedic.SolutionRoot.");
        var outside = Directory.CreateTempSubdirectory("PackageMedic.SolutionOutside.");
        try
        {
            var solution = Path.Combine(root.FullName, "Unsafe.slnx");
            File.WriteAllText(solution, "<Solution><Project Path=\"linked/Outside.csproj\" /></Solution>");
            File.WriteAllText(
                Path.Combine(outside.FullName, "Outside.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(root.FullName, "linked"), outside.FullName);
            }
            catch (Exception symlinkException) when (symlinkException is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var discoveryException = Assert.Throws<InvalidOperationException>(
                () => new ProjectDiscovery().Discover(solution, root.FullName));

            Assert.Contains("safe analysis root", discoveryException.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            root.Delete(recursive: true);
            outside.Delete(recursive: true);
        }
    }

    [Fact]
    public void SolutionCannotReferenceAProjectOutsideTheAnalysisRootEvenWhenMissing()
    {
        var root = Directory.CreateTempSubdirectory("PackageMedic.SolutionBoundary.");
        try
        {
            var solution = Path.Combine(root.FullName, "Unsafe.slnx");
            File.WriteAllText(solution, "<Solution><Project Path=\"../Missing.csproj\" /></Solution>");

            var exception = Assert.Throws<InvalidOperationException>(
                () => new ProjectDiscovery().Discover(solution, root.FullName));

            Assert.Contains("safe analysis root", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void SolutionCannotSilentlyOmitAMissingProjectInsideTheAnalysisRoot()
    {
        var root = Directory.CreateTempSubdirectory("PackageMedic.MissingSolutionProject.");
        try
        {
            var existing = Path.Combine(root.FullName, "Existing.csproj");
            var solution = Path.Combine(root.FullName, "Incomplete.slnx");
            File.WriteAllText(existing, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(
                solution,
                "<Solution><Project Path=\"Existing.csproj\" /><Project Path=\"Missing.csproj\" /></Solution>");

            var exception = Assert.Throws<InvalidOperationException>(
                () => new ProjectDiscovery().Discover(solution, root.FullName));

            Assert.Contains("missing", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Missing.csproj", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void DiscoversOneThousandProjectsDeterministicallyInOneTreeWalk()
    {
        var root = Directory.CreateTempSubdirectory("PackageMedic.LargeDiscovery.");
        try
        {
            for (var directoryIndex = 0; directoryIndex < 100; directoryIndex++)
            {
                var directory = Directory.CreateDirectory(Path.Combine(root.FullName, $"group-{directoryIndex:D3}"));
                for (var projectIndex = 0; projectIndex < 10; projectIndex++)
                {
                    File.WriteAllText(
                        Path.Combine(directory.FullName, $"Project-{projectIndex:D2}.csproj"),
                        "<Project Sdk=\"Microsoft.NET.Sdk\" />");
                }
            }

            var discovery = new ProjectDiscovery().Discover(root.FullName);

            Assert.Equal(1_000, discovery.Projects.Count);
            Assert.Equal(1_000, discovery.Projects.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Empty(discovery.Errors);
            Assert.Equal(
                discovery.Projects.Order(StringComparer.OrdinalIgnoreCase),
                discovery.Projects);
        }
        finally
        {
            root.Delete(recursive: true);
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
    public void SecretRedactionPreservesStructuredJsonAndRemovesTerminalControls()
    {
        const string raw = "{\"message\":\"token=secret\",\"next\":1,\"text\":\"unsafe\u001b[31m\"}";

        var redacted = ProcessRunner.RedactSecrets(raw);

        using var document = JsonDocument.Parse(redacted);
        Assert.Equal("token=[REDACTED]", document.RootElement.GetProperty("message").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("next").GetInt32());
        Assert.DoesNotContain('\u001b', redacted);
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
    public async Task TruncatedRestoreOutputBecomesAnOperationalError()
    {
        using var reader = new StringReader(new string('x', 64));
        var truncated = await ProcessRunner.ReadBoundedAsync(reader, 16, CancellationToken.None);
        var target = Path.Combine(Path.GetTempPath(), "PackageMedic.Truncated", "App.csproj");
        var runner = new DelayedProcessRunner(_ => new ProcessResult(0, truncated, string.Empty));
        var restore = new RestoreRunner(runner, TimeSpan.FromSeconds(5), 1);

        var result = await restore.RestoreAsync(
            new DiscoveryResult(target, [], [target], [target]),
            null,
            TestContext.Current.CancellationToken);

        Assert.Contains(result.Errors, item => item.Contains("safety limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RestoreFailureIncludesUsefulRedactedContext()
    {
        var target = Path.Combine(Path.GetTempPath(), "PackageMedic.Restore", "App.csproj");
        var runner = new DelayedProcessRunner(_ => new ProcessResult(
            1,
            string.Empty,
            "Unable to access https://feed-user:feed-password@packages.example.test/v3/index.json token=secret"));

        var result = await new RestoreRunner(runner).RestoreAsync(
            new DiscoveryResult(target, [], [target], [target]),
            null,
            TestContext.Current.CancellationToken);

        var error = Assert.Single(result.Errors);
        Assert.Contains("Unable to access", error, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", error, StringComparison.Ordinal);
        Assert.DoesNotContain("feed-user", error, StringComparison.Ordinal);
        Assert.DoesNotContain("feed-password", error, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalysisExecutionTimeoutsRejectUnsafeValues()
    {
        var options = new AnalysisExecutionOptions(TimeSpan.Zero, TimeSpan.FromMinutes(1));

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AnalysisExecutionOptions(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), 33).Validate());
    }

    [Fact]
    public void ProcessSpecificTimeoutsRejectUnsafeValues()
    {
        var processRunner = new NeverCompletesProcessRunner();

        Assert.Throws<ArgumentOutOfRangeException>(() => new RestoreRunner(processRunner, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MsBuildProjectEvaluator(processRunner, Timeout.InfiniteTimeSpan));
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

    [Fact]
    public async Task RestoreParallelismIsBoundedAndActuallyConcurrent()
    {
        var runner = new DelayedProcessRunner(_ => new ProcessResult(0, string.Empty, string.Empty));
        var targets = Enumerable.Range(0, 12)
            .Select(index => Path.Combine(Path.GetTempPath(), "PackageMedic.Parallel", $"Project{index:D2}.csproj"))
            .ToArray();
        var restore = new RestoreRunner(runner, TimeSpan.FromSeconds(5), maxDegreeOfParallelism: 3);

        var result = await restore.RestoreAsync(
            new DiscoveryResult(Path.GetTempPath(), [], targets, targets),
            null,
            TestContext.Current.CancellationToken);

        Assert.Empty(result.Errors);
        Assert.InRange(runner.MaximumConcurrency, 2, 3);
    }

    [Fact]
    public async Task MultiTargetEvaluationSharesTheConfiguredProcessLimit()
    {
        const string output =
            "{\"Properties\":{\"TargetFrameworks\":\"net8.0;net9.0;net10.0\",\"ProjectAssetsFile\":\"obj/project.assets.json\"}," +
            "\"Items\":{\"PackageReference\":[],\"PackageVersion\":[]}}";
        var runner = new DelayedProcessRunner(_ => new ProcessResult(0, output, string.Empty));
        var evaluator = new MsBuildProjectEvaluator(
            runner,
            TimeSpan.FromSeconds(5),
            maxDegreeOfParallelism: 2);

        var evaluated = await evaluator.EvaluateAsync(
            Path.Combine(Path.GetTempPath(), "PackageMedic.Parallel", "App.csproj"),
            TestContext.Current.CancellationToken);

        Assert.Equal(["net8.0", "net9.0", "net10.0"], evaluated.TargetFrameworks);
        Assert.InRange(runner.MaximumConcurrency, 2, 2);
    }

    [Fact]
    public async Task StreamingJsonMatchesInMemoryJson()
    {
        var result = new AnalysisResult(
            PackageMedicAnalyzer.Version,
            "/repo",
            new ScanSummary(0, 0, 0, 0, 0, 0, 0),
            [],
            []);
        await using var stream = new MemoryStream();

        await ResultJsonSerializer.SerializeAsync(
            stream,
            result,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ResultJsonSerializer.Serialize(result), System.Text.Encoding.UTF8.GetString(stream.ToArray()));
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

    private sealed class DelayedProcessRunner(Func<IReadOnlyList<string>, ProcessResult> resultFactory) : IProcessRunner
    {
        private int active;
        private int maximumConcurrency;

        public int MaximumConcurrency => Volatile.Read(ref maximumConcurrency);

        public async Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref active);
            while (true)
            {
                var observed = Volatile.Read(ref maximumConcurrency);
                if (current <= observed || Interlocked.CompareExchange(ref maximumConcurrency, current, observed) == observed)
                {
                    break;
                }
            }

            try
            {
                await Task.Delay(30, cancellationToken);
                return resultFactory(arguments);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }
    }
}
