using System.Text.Json;
using PackageMedic.Core;

namespace PackageMedic.Core.Tests;

public sealed class DeprecationAuditTests
{
    private const string Report =
        """
        informational prefix
        {
          "version": 1,
          "projects": [{
            "path": "/repo/src/App/App.csproj",
            "frameworks": [{
              "framework": "net8.0",
              "topLevelPackages": [{
                "id": "Legacy.Direct",
                "requestedVersion": "1.0.0",
                "resolvedVersion": "1.2.0",
                "deprecationReasons": ["Legacy", "Other"],
                "alternativePackage": { "id": "Modern.Direct", "versionRange": "[2.0.0,)" }
              }],
              "transitivePackages": [{
                "id": "Broken.Transitive",
                "resolvedVersion": "3.0.0",
                "deprecationReasons": ["Critical Bugs"]
              }]
            }]
          }]
        }
        trailing text
        """;

    [Fact]
    public void ParsesOfficialVersionedJsonAndReplacementMetadataDeterministically()
    {
        var first = DeprecationAuditParser.Parse(Report);
        var second = DeprecationAuditParser.Parse(Report);

        Assert.Equal(
            first.Select(item => $"{item.PackageId}|{item.ResolvedVersion}|{string.Join(',', item.Reasons)}|{item.AlternativePackageId}|{item.AlternativeVersionRange}"),
            second.Select(item => $"{item.PackageId}|{item.ResolvedVersion}|{string.Join(',', item.Reasons)}|{item.AlternativePackageId}|{item.AlternativeVersionRange}"));
        Assert.Equal(2, first.Count);
        var broken = first[0];
        Assert.Equal("Broken.Transitive", broken.PackageId);
        Assert.Contains(PackageDeprecationReason.CriticalBugs, broken.Reasons);
        Assert.True(broken.IsTransitive);
        var legacy = Assert.Single(first, item => item.PackageId == "Legacy.Direct");
        Assert.Equal("Modern.Direct", legacy.AlternativePackageId);
        Assert.Equal("[2.0.0,)", legacy.AlternativeVersionRange);
        Assert.True(legacy.IsDirect);
    }

    [Fact]
    public void CreatesPm008WithCriticalBugSeverityAndDirectSourceLocation()
    {
        var packages = DeprecationAuditParser.Parse(Report);
        var inventory = new PackageInventoryItem(
            "/repo/src/App/App.csproj",
            "net8.0",
            "Legacy.Direct",
            "1.2.0",
            PackageDependencyKind.Direct,
            "1.0.0",
            "central",
            SourceFile: "/repo/Directory.Packages.props",
            SourceLine: 14);

        var diagnostics = DeprecationAuditParser.ToDiagnostics(packages, [inventory]);

        Assert.Equal(2, diagnostics.Count);
        var critical = Assert.Single(diagnostics, item => item.Evidence.Contains("Broken.Transitive", StringComparison.Ordinal));
        Assert.Equal(DiagnosticSeverity.Error, critical.Severity);
        var legacy = Assert.Single(diagnostics, item => item.Evidence.Contains("Legacy.Direct", StringComparison.Ordinal));
        Assert.Equal("PM008", legacy.Code);
        Assert.Equal(DiagnosticSeverity.Warning, legacy.Severity);
        Assert.Equal("/repo/Directory.Packages.props", legacy.File);
        Assert.Equal(14, legacy.Line);
        Assert.Contains("Modern.Direct", legacy.SuggestedAction, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsUnsupportedVersionsAndMalformedStructures()
    {
        Assert.Throws<InvalidDataException>(() => DeprecationAuditParser.Parse("{\"version\":2,\"projects\":[]}"));
        Assert.Throws<InvalidDataException>(() => DeprecationAuditParser.Parse("{\"version\":1,\"projects\":{}}"));
        Assert.ThrowsAny<JsonException>(() => DeprecationAuditParser.Parse("prefix { bad-json } suffix"));
    }

    [Fact]
    public async Task RunnerUsesSeparateOfficialDeprecatedQuery()
    {
        var target = Path.Combine(Path.GetTempPath(), "PackageMedic.Deprecation", "App.csproj");
        var processRunner = new RecordingProcessRunner(
            (_, _, _, _) => Task.FromResult(new ProcessResult(0, Report, string.Empty)));
        var result = await new DeprecationAuditRunner(processRunner).AuditAsync(
            new DiscoveryResult(target, [], [target], [target]),
            includeTransitive: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.HasOperationalError);
        Assert.Equal(2, result.DeprecatedPackages.Count);
        Assert.Equal(
            ["list", target, "package", "--deprecated", "--format", "json", "--output-version", "1", "--include-transitive"],
            Assert.Single(processRunner.Calls).Arguments);
    }

    [Fact]
    public async Task RunnerRedactsErrorsAndTurnsTimeoutIntoOperationalError()
    {
        var target = Path.Combine(Path.GetTempPath(), "PackageMedic.Deprecation", "App.csproj");
        var failedRunner = new RecordingProcessRunner((_, _, _, _) => Task.FromResult(
            new ProcessResult(1, string.Empty, "https://user:password@feed.test token=secret")));
        var failed = await new DeprecationAuditRunner(failedRunner).AuditAsync(
            new DiscoveryResult(target, [], [target], [target]), false,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(failed.HasOperationalError);
        Assert.DoesNotContain("password", Assert.Single(failed.Errors), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", failed.Errors[0], StringComparison.Ordinal);

        var timeout = await new DeprecationAuditRunner(
            new NeverCompletesProcessRunner(), TimeSpan.FromMilliseconds(25), 1).AuditAsync(
                new DiscoveryResult(target, [], [target], [target]), false,
                cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(timeout.HasOperationalError);
        Assert.Contains("timed out", Assert.Single(timeout.Errors), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunnerPreservesCallerCancellationAndBoundsParallelTargets()
    {
        var target = Path.Combine(Path.GetTempPath(), "PackageMedic.Deprecation", "Cancel.csproj");
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new DeprecationAuditRunner(new NeverCompletesProcessRunner()).AuditAsync(
                    new DiscoveryResult(target, [], [target], [target]), true,
                    cancellationToken: cancellation.Token));
        }

        var targets = Enumerable.Range(0, 20)
            .Select(index => Path.Combine(Path.GetTempPath(), "PackageMedic.Deprecation", $"Project{index:D2}.csproj"))
            .ToArray();
        var processRunner = new ConcurrentProcessRunner();
        var result = await new DeprecationAuditRunner(
            processRunner, TimeSpan.FromSeconds(5), maxDegreeOfParallelism: 3).AuditAsync(
                new DiscoveryResult(Path.GetTempPath(), [], targets, targets), true,
                cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.HasOperationalError);
        Assert.InRange(processRunner.MaximumConcurrency, 2, 3);
        Assert.Equal(targets.Length, processRunner.CallCount);
    }

    [Fact]
    public void Pm008IsRegisteredInTheRuleCatalog()
    {
        var rule = DiagnosticRuleCatalog.GetRequired("PM008");

        Assert.Equal("DeprecatedPackage", rule.Name);
        Assert.Equal(DiagnosticSeverity.Warning, rule.DefaultSeverity);
    }

    private sealed class RecordingProcessRunner(
        Func<string, IReadOnlyList<string>, string, CancellationToken, Task<ProcessResult>> handler) : IProcessRunner
    {
        public List<ProcessCall> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            Calls.Add(new ProcessCall(fileName, arguments.ToArray()));
            return handler(fileName, arguments, workingDirectory, cancellationToken);
        }
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

    private sealed class ConcurrentProcessRunner : IProcessRunner
    {
        private int active;
        private int callCount;
        private int maximumConcurrency;

        public int CallCount => Volatile.Read(ref callCount);

        public int MaximumConcurrency => Volatile.Read(ref maximumConcurrency);

        public async Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref callCount);
            var current = Interlocked.Increment(ref active);
            while (true)
            {
                var observed = Volatile.Read(ref maximumConcurrency);
                if (current <= observed ||
                    Interlocked.CompareExchange(ref maximumConcurrency, current, observed) == observed)
                {
                    break;
                }
            }

            try
            {
                await Task.Delay(30, cancellationToken);
                return new ProcessResult(0, "{\"version\":1,\"projects\":[]}", string.Empty);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }
    }

    private sealed record ProcessCall(string FileName, IReadOnlyList<string> Arguments);
}
