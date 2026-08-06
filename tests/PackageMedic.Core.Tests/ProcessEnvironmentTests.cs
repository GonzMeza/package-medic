using System.Diagnostics;
using PackageMedic.Core;

namespace PackageMedic.Core.Tests;

public sealed class ProcessEnvironmentTests
{
    private static readonly string[] IsolatedDirectoryVariables =
    [
        "NUGET_PACKAGES",
        "NUGET_HTTP_CACHE_PATH",
        "NUGET_PLUGINS_CACHE_PATH",
        "DOTNET_CLI_HOME",
        "HOME",
        "USERPROFILE",
        "APPDATA",
        "LOCALAPPDATA",
        "TEMP",
        "TMP",
        "TMPDIR",
    ];

    [Fact]
    public void IsolatedDotNetEnvironmentClearsInheritedValuesAndContainsEveryWritablePath()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var packages = Path.Combine(root, "shared-packages");
            var environment = ProcessEnvironment.CreateIsolatedDotNet(root, packages);
            var startInfo = new ProcessStartInfo();
            startInfo.Environment["PACKAGEMEDIC_HOST_SECRET"] = "must-not-survive";

            environment.ApplyTo(startInfo);

            Assert.True(environment.ClearInheritedEnvironment);
            Assert.False(startInfo.Environment.ContainsKey("PACKAGEMEDIC_HOST_SECRET"));
            Assert.Equal("1", startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"]);
            Assert.Equal("1", startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"]);
            Assert.Equal("1", startInfo.Environment["DOTNET_NOLOGO"]);
            Assert.Equal("1", startInfo.Environment["MSBUILDDISABLENODEREUSE"]);
            Assert.Equal("0", startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"]);
            Assert.Equal("skip", startInfo.Environment["NUGET_XMLDOC_MODE"]);
            Assert.Equal(Path.GetFullPath(packages), startInfo.Environment["NUGET_PACKAGES"]);
            foreach (var name in IsolatedDirectoryVariables)
            {
                var path = Assert.IsType<string>(startInfo.Environment[name]);
                Assert.True(ProjectDiscovery.IsSafelyContained(root, path), $"{name} escaped the owned root.");
                Assert.True(Directory.Exists(path), $"{name} directory was not created.");
                Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void IsolatedDotNetEnvironmentRejectsPackageCacheOutsideTheOwnedRoot()
    {
        var root = CreateTemporaryDirectory();
        var outside = CreateTemporaryDirectory();
        try
        {
            Assert.Throws<ArgumentException>(() =>
                ProcessEnvironment.CreateIsolatedDotNet(root, Path.Combine(outside, "packages")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void IsolatedDotNetEnvironmentDoesNotAllowOverridingControlledCachePaths()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var exception = Assert.Throws<ArgumentException>(() =>
                ProcessEnvironment.CreateIsolatedDotNet(
                    root,
                    additionalVariables: new Dictionary<string, string>
                    {
                        ["NUGET_PACKAGES"] = Path.Combine(root, "replacement"),
                    }));

            Assert.Contains("controlled", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScopedRunnerForwardsIsolationAndRedactsExplicitPrivateFeedSecret()
    {
        const string credentialVariable = "VSS_NUGET_EXTERNAL_FEED_ENDPOINTS";
        const string secret = "private-feed-secret-9af4f3";
        var root = CreateTemporaryDirectory();
        try
        {
            var environment = ProcessEnvironment.CreateIsolatedDotNet(
                root,
                additionalVariables: new Dictionary<string, string>
                {
                    [credentialVariable] = secret,
                },
                sensitiveVariableNames: [credentialVariable]);
            var inner = new CapturingEnvironmentRunner(
                new ProcessResult(0, $"credential:{secret}", $"stderr:{secret}"));
            IProcessRunner runner = new EnvironmentScopedProcessRunner(inner, environment);

            var result = await runner.RunAsync(
                "dotnet",
                ["--info"],
                root,
                TestContext.Current.CancellationToken);

            Assert.Same(environment, inner.Environment);
            Assert.Equal(secret, inner.Environment!.Variables[credentialVariable]);
            Assert.DoesNotContain(secret, result.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, result.StandardError, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScopedRunnerDoesNotSilentlyUseALegacyRunnerThatCannotApplyIsolation()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            IProcessRunner runner = new EnvironmentScopedProcessRunner(
                new LegacyRunner(),
                ProcessEnvironment.CreateIsolatedDotNet(root));

            var exception = await Assert.ThrowsAsync<NotSupportedException>(() => runner.RunAsync(
                "dotnet",
                ["--info"],
                root,
                TestContext.Current.CancellationToken));

            Assert.Contains("environment isolation", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScopedRunnerDoesNotSilentlyIgnoreExecutableTrustRoots()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            IProcessRunner runner = new EnvironmentScopedProcessRunner(
                new LegacyRunner(),
                ProcessEnvironment.CreateOverrides(
                    new Dictionary<string, string?>(),
                    untrustedExecutableRoots: [root]));

            var exception = await Assert.ThrowsAsync<NotSupportedException>(() => runner.RunAsync(
                "dotnet",
                ["--info"],
                root,
                TestContext.Current.CancellationToken));

            Assert.Contains("environment isolation", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OwnedTemporaryDirectoryRemovesOnlyItsMarkedPrivateRoot()
    {
        var repository = CreateTemporaryDirectory();
        string ownedPath;
        try
        {
            using (var owned = OwnedTemporaryDirectory.Create(repository))
            {
                ownedPath = owned.DirectoryPath;
                Assert.True(Directory.Exists(ownedPath));
                Assert.False(ProjectDiscovery.IsSafelyContained(repository, ownedPath));
                Assert.False(File.GetAttributes(ownedPath).HasFlag(FileAttributes.ReparsePoint));
                File.WriteAllText(Path.Combine(ownedPath, "runtime.txt"), "owned");
            }

            Assert.False(Directory.Exists(ownedPath));
            Assert.True(Directory.Exists(repository));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    [Fact]
    public void OwnedTemporaryDirectoryRejectsARootThatPhysicallyResolvesInsideTheRepository()
    {
        var outer = CreateTemporaryDirectory();
        var repository = Directory.CreateDirectory(Path.Combine(outer, "repository")).FullName;
        var physicalTemporaryRoot = Directory.CreateDirectory(Path.Combine(repository, "runtime-root")).FullName;
        var link = Path.Combine(outer, "runtime-link");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, physicalTemporaryRoot);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var error = Assert.Throws<InvalidOperationException>(() =>
                OwnedTemporaryDirectory.Create(repository, link));
            Assert.Contains("outside", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link, recursive: false);
            }

            Directory.Delete(outer, recursive: true);
        }
    }

    [Fact]
    public void NonIsolatedOverridesPreserveUnrelatedInheritedVariables()
    {
        var environment = ProcessEnvironment.CreateOverrides(new Dictionary<string, string?>
        {
            ["PACKAGEMEDIC_OVERRIDE"] = "new",
            ["PACKAGEMEDIC_REMOVE"] = null,
        });
        var startInfo = new ProcessStartInfo();
        startInfo.Environment["PACKAGEMEDIC_PRESERVE"] = "preserved";
        startInfo.Environment["PACKAGEMEDIC_OVERRIDE"] = "old";
        startInfo.Environment["PACKAGEMEDIC_REMOVE"] = "remove-me";

        environment.ApplyTo(startInfo);

        Assert.False(environment.ClearInheritedEnvironment);
        Assert.Equal("preserved", startInfo.Environment["PACKAGEMEDIC_PRESERVE"]);
        Assert.Equal("new", startInfo.Environment["PACKAGEMEDIC_OVERRIDE"]);
        Assert.False(startInfo.Environment.ContainsKey("PACKAGEMEDIC_REMOVE"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" PRIVATE_FEED_TOKEN")]
    [InlineData("PRIVATE_FEED_TOKEN ")]
    [InlineData("PRIVATE_FEED_TOKEN=value")]
    [InlineData("PRIVATE_FEED\nTOKEN")]
    public void PublicCredentialVariableNameValidatorRejectsUnsafeNames(string name)
    {
        Assert.Throws<ArgumentException>(() => ProcessEnvironment.ValidateVariableName(name));
    }

    [Fact]
    public void PublicCredentialVariableNameValidatorAcceptsNuGetCredentialNames()
    {
        ProcessEnvironment.ValidateVariableName("NuGetPackageSourceCredentials_PrivateFeed");
        ProcessEnvironment.ValidateVariableName("VSS_NUGET_EXTERNAL_FEED_ENDPOINTS");
    }

    [Fact]
    public void PublicCredentialVariableNameValidatorBoundsNameLength()
    {
        var name = new string('A', ProcessEnvironment.MaximumVariableNameLength + 1);

        Assert.Throws<ArgumentException>(() => ProcessEnvironment.ValidateVariableName(name));
    }

    [Fact]
    public void ProcessRunnerNeverResolvesARepositoryLocalExecutable()
    {
        var root = Directory.CreateTempSubdirectory("PackageMedic.ExecutableResolution.");
        try
        {
            var repository = Directory.CreateDirectory(Path.Combine(root.FullName, "repository"));
            var hostBin = Directory.CreateDirectory(Path.Combine(root.FullName, "host-bin"));
            const string executableName = "packagemedic-shadow-test";
            var suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
            var repositoryExecutable = Path.Combine(repository.FullName, executableName + suffix);
            var hostExecutable = Path.Combine(hostBin.FullName, executableName + suffix);
            File.WriteAllText(repositoryExecutable, "hostile");
            File.WriteAllText(hostExecutable, "trusted host placeholder");
            var path = string.Join(Path.PathSeparator, repository.FullName, hostBin.FullName);

            var resolved = ProcessRunner.ResolveExecutable(
                executableName,
                repository.FullName,
                path,
                OperatingSystem.IsWindows() ? ".EXE" : null);

            Assert.True(File.Exists(resolved));
            Assert.Equal("trusted host placeholder", File.ReadAllText(resolved));
            Assert.Throws<InvalidOperationException>(() => ProcessRunner.ResolveExecutable(
                repositoryExecutable,
                repository.FullName,
                path,
                OperatingSystem.IsWindows() ? ".EXE" : null));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void ProcessRunnerRejectsExecutablesAnywhereInTheAnalysisRoot()
    {
        var repository = Directory.CreateTempSubdirectory("PackageMedic.ExecutableRoot.");
        var project = Directory.CreateDirectory(Path.Combine(repository.FullName, "src", "App"));
        var tools = Directory.CreateDirectory(Path.Combine(repository.FullName, "tools"));
        var host = Directory.CreateTempSubdirectory("PackageMedic.HostExecutable.");
        try
        {
            var executableName = "packagemedic-root-test";
            var suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
            var repositoryExecutable = Path.Combine(tools.FullName, executableName + suffix);
            var hostExecutable = Path.Combine(host.FullName, executableName + suffix);
            File.WriteAllText(repositoryExecutable, "hostile");
            File.WriteAllText(hostExecutable, "trusted host placeholder");
            var path = string.Join(Path.PathSeparator, tools.FullName, host.FullName);

            var resolved = ProcessRunner.ResolveExecutable(
                executableName,
                project.FullName,
                path,
                OperatingSystem.IsWindows() ? ".EXE" : null,
                [repository.FullName]);

            Assert.True(File.Exists(resolved));
            Assert.Equal("trusted host placeholder", File.ReadAllText(resolved));
        }
        finally
        {
            repository.Delete(recursive: true);
            host.Delete(recursive: true);
        }
    }

    [Fact]
    public void ProcessRunnerRejectsAPathDirectoryLinkedIntoTheAnalysisRoot()
    {
        var repository = Directory.CreateTempSubdirectory("PackageMedic.LinkedExecutableRoot.");
        var tools = Directory.CreateDirectory(Path.Combine(repository.FullName, "tools"));
        var pathRoot = Directory.CreateTempSubdirectory("PackageMedic.LinkedPath.");
        var host = Directory.CreateTempSubdirectory("PackageMedic.LinkedHost.");
        try
        {
            var executableName = "packagemedic-linked-root-test";
            var suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
            File.WriteAllText(Path.Combine(tools.FullName, executableName + suffix), "hostile");
            var hostExecutable = Path.Combine(host.FullName, executableName + suffix);
            File.WriteAllText(hostExecutable, "trusted host placeholder");
            var linkedPath = Path.Combine(pathRoot.FullName, "linked-bin");
            try
            {
                Directory.CreateSymbolicLink(linkedPath, tools.FullName);
            }
            catch (Exception exception) when (exception is
                IOException or
                PlatformNotSupportedException or
                UnauthorizedAccessException)
            {
                return;
            }

            var resolved = ProcessRunner.ResolveExecutable(
                executableName,
                repository.FullName,
                string.Join(Path.PathSeparator, linkedPath, host.FullName),
                OperatingSystem.IsWindows() ? ".EXE" : null,
                [repository.FullName]);

            Assert.True(File.Exists(resolved));
            Assert.Equal("trusted host placeholder", File.ReadAllText(resolved));

            var linkedRepository = Path.Combine(pathRoot.FullName, "linked-repository");
            Directory.CreateSymbolicLink(linkedRepository, repository.FullName);
            var resolvedThroughLinkedRoot = ProcessRunner.ResolveExecutable(
                executableName,
                linkedRepository,
                string.Join(Path.PathSeparator, tools.FullName, host.FullName),
                OperatingSystem.IsWindows() ? ".EXE" : null,
                [linkedRepository]);

            Assert.True(File.Exists(resolvedThroughLinkedRoot));
            Assert.Equal("trusted host placeholder", File.ReadAllText(resolvedThroughLinkedRoot));
        }
        finally
        {
            repository.Delete(recursive: true);
            pathRoot.Delete(recursive: true);
            host.Delete(recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"PackageMedic.ProcessEnvironment.{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class CapturingEnvironmentRunner(ProcessResult result) : IProcessRunner
    {
        public ProcessEnvironment? Environment { get; private set; }

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The scoped runner must use the explicit environment overload.");

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            ProcessEnvironment environment,
            CancellationToken cancellationToken)
        {
            Environment = environment;
            return Task.FromResult(result);
        }
    }

    private sealed class LegacyRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
    }
}
