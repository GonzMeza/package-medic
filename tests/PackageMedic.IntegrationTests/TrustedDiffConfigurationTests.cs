using PackageMedic.Cli;

namespace PackageMedic.IntegrationTests;

public sealed class TrustedDiffConfigurationTests
{
    [Fact]
    public void MapsRepositoryOwnedConfigurationToTheBaseSnapshot()
    {
        using var roots = DiffRoots.Create();
        var currentConfiguration = roots.WriteCurrent("config/.packagemedic.json", "candidate");
        var baselineConfiguration = roots.WriteBaseline("config/.packagemedic.json", "baseline");
        var options = Options(roots.CurrentTarget, currentConfiguration);

        var resolved = Program.ResolveTrustedDiffConfiguration(
            options,
            roots.CurrentRoot,
            roots.CurrentTarget,
            roots.BaselineRoot,
            roots.BaselineTarget);

        Assert.Equal(Path.GetFullPath(baselineConfiguration), resolved.ConfigurationPath);
        Assert.False(resolved.NoConfiguration);
    }

    [Fact]
    public void RejectsRepositoryOwnedConfigurationMissingFromTheBaseRevision()
    {
        using var roots = DiffRoots.Create();
        var currentConfiguration = roots.WriteCurrent("config/.packagemedic.json", "candidate only");
        var options = Options(roots.CurrentTarget, currentConfiguration);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            Program.ResolveTrustedDiffConfiguration(
                options,
                roots.CurrentRoot,
                roots.CurrentTarget,
                roots.BaselineRoot,
                roots.BaselineTarget));

        Assert.Contains("tracked regular file in the base revision", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsExplicitConfigurationOutsideTheRepositoryAsInvocationPolicy()
    {
        using var roots = DiffRoots.Create();
        var external = Path.Combine(roots.OwnedRoot, "invocation-policy.json");
        File.WriteAllText(external, "trusted caller policy");
        var options = Options(roots.CurrentTarget, external);

        var resolved = Program.ResolveTrustedDiffConfiguration(
            options,
            roots.CurrentRoot,
            roots.CurrentTarget,
            roots.BaselineRoot,
            roots.BaselineTarget);

        Assert.Equal(Path.GetFullPath(external), resolved.ConfigurationPath);
        Assert.False(resolved.NoConfiguration);
    }

    [Fact]
    public void IgnoresCandidateOnlyAutomaticConfiguration()
    {
        using var roots = DiffRoots.Create();
        roots.WriteCurrent(".packagemedic.json", "candidate only");
        var options = Options(roots.CurrentTarget, configuration: null);

        var resolved = Program.ResolveTrustedDiffConfiguration(
            options,
            roots.CurrentRoot,
            roots.CurrentTarget,
            roots.BaselineRoot,
            roots.BaselineTarget);

        Assert.Null(resolved.ConfigurationPath);
        Assert.True(resolved.NoConfiguration);
    }

    [Fact]
    public void UsesAutomaticConfigurationFromTheBaseRevisionForBothSides()
    {
        using var roots = DiffRoots.Create();
        var baselineConfiguration = roots.WriteBaseline(".packagemedic.json", "baseline");
        var options = Options(roots.CurrentTarget, configuration: null);

        var resolved = Program.ResolveTrustedDiffConfiguration(
            options,
            roots.CurrentRoot,
            roots.CurrentTarget,
            roots.BaselineRoot,
            roots.BaselineTarget);

        Assert.Equal(Path.GetFullPath(baselineConfiguration), resolved.ConfigurationPath);
        Assert.False(resolved.NoConfiguration);
    }

    private static CliOptions Options(string target, string? configuration) => new(
        CliCommand.Diff,
        Path: target,
        ConfigurationPath: configuration,
        GitReference: "HEAD");

    private sealed class DiffRoots : IDisposable
    {
        private DiffRoots(string ownedRoot, string currentRoot, string baselineRoot)
        {
            OwnedRoot = ownedRoot;
            CurrentRoot = currentRoot;
            BaselineRoot = baselineRoot;
            CurrentTarget = Directory.CreateDirectory(Path.Combine(currentRoot, "src")).FullName;
            BaselineTarget = Directory.CreateDirectory(Path.Combine(baselineRoot, "src")).FullName;
        }

        public string OwnedRoot { get; }

        public string CurrentRoot { get; }

        public string BaselineRoot { get; }

        public string CurrentTarget { get; }

        public string BaselineTarget { get; }

        public static DiffRoots Create()
        {
            var owned = Path.Combine(Path.GetTempPath(), "PackageMedic.TrustedDiff." + Guid.NewGuid().ToString("N"));
            var current = Directory.CreateDirectory(Path.Combine(owned, "current")).FullName;
            var baseline = Directory.CreateDirectory(Path.Combine(owned, "baseline")).FullName;
            return new DiffRoots(owned, current, baseline);
        }

        public string WriteCurrent(string relativePath, string content) =>
            Write(CurrentRoot, relativePath, content);

        public string WriteBaseline(string relativePath, string content) =>
            Write(BaselineRoot, relativePath, content);

        public void Dispose()
        {
            if (Directory.Exists(OwnedRoot))
            {
                Directory.Delete(OwnedRoot, recursive: true);
            }
        }

        private static string Write(string root, string relativePath, string content)
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }
    }
}
