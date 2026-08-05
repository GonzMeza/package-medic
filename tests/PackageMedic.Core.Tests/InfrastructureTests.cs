using System.ComponentModel;
using System.Text;
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
    public void RejectsAssetsCollectionsThatExceedTheirSafetyBudget()
    {
        var root = Directory.CreateTempSubdirectory("PackageMedic.AssetsCollectionLimit.");
        try
        {
            var project = Path.Combine(root.FullName, "App.csproj");
            var assets = Path.Combine(root.FullName, "project.assets.json");
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            var sourceEntries = new StringBuilder();
            for (var index = 0; index <= AssetsFileReader.MaximumPackageSources; index++)
            {
                if (index > 0)
                {
                    sourceEntries.Append(',');
                }

                sourceEntries.Append('"')
                    .Append("https://packages.example.test/")
                    .Append(index)
                    .Append("\":{}");
            }

            File.WriteAllText(
                assets,
                $$"""
                {
                  "targets": {},
                  "libraries": {},
                  "project": {
                    "restore": { "sources": { {{sourceEntries}} } },
                    "frameworks": {}
                  }
                }
                """);

            var exception = Assert.Throws<InvalidDataException>(() =>
                new AssetsFileReader().Read(assets, project, root.FullName));

            Assert.Contains("package sources", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("safety limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            root.Delete(recursive: true);
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
    public void RejectsAssetsFilesOutsideTheExplicitTrustedRootBeforeReadingThem()
    {
        var root = Directory.CreateTempSubdirectory("PackageMedic.AssetsRoot.");
        var outside = Path.GetTempFileName();
        try
        {
            File.WriteAllText(outside, "{\"version\":3}");
            var project = Path.Combine(root.FullName, "App.csproj");

            var exception = Assert.Throws<InvalidDataException>(() =>
                new AssetsFileReader().Read(outside, project, root.FullName));

            Assert.Contains("outside the trusted analysis root", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(outside);
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void ReadsDependencyEdgesPackageProvenanceAndSourceMapping()
    {
        var root = Directory.CreateTempSubdirectory("PackageMedic.AssetsImpact.");
        try
        {
            var project = Path.Combine(root.FullName, "App.csproj");
            var assets = Path.Combine(root.FullName, "project.assets.json");
            var packageFolder = Path.Combine(root.FullName, "packages") + Path.DirectorySeparatorChar;
            var cachedPackage = Path.Combine(packageFolder, "transitive", "2.0.0");
            Directory.CreateDirectory(cachedPackage);
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(
                Path.Combine(root.FullName, "NuGet.Config"),
                "<configuration><packageSources><clear /><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" /><add key=\"other\" value=\"https://packages.example.test/v3/index.json\" /></packageSources><packageSourceMapping><clear /><packageSource key=\"nuget.org\"><package pattern=\"*\" /></packageSource></packageSourceMapping></configuration>");
            File.WriteAllText(
                Path.Combine(cachedPackage, ".nupkg.metadata"),
                "{\"source\":\"https://api.nuget.org/v3/index.json?sig=private-token#fragment\",\"contentHash\":\"sha512-from-cache\"}");
            var escapedFolder = JsonSerializer.Serialize(packageFolder);
            File.WriteAllText(
                assets,
                $$"""
                {
                  "targets": {
                    "net8.0": {
                      "Direct/1.0.0": { "dependencies": { "Transitive": "2.0.0" } },
                      "Transitive/2.0.0": {}
                    }
                  },
                  "libraries": {
                    "Direct/1.0.0": { "type": "package", "path": "direct/1.0.0" },
                    "Transitive/2.0.0": {
                      "type": "package",
                      "path": "transitive/2.0.0",
                      "sha512": "sha512-from-assets",
                      "files": [".nupkg.metadata", ".signature.p7s"]
                    }
                  },
                  "packageFolders": { {{escapedFolder}}: {} },
                  "project": {
                    "restore": {
                      "sources": {
                        "https://api.nuget.org/v3/index.json": {},
                        "https://packages.example.test/v3/index.json": {}
                      }
                    },
                    "frameworks": { "net8.0": { "dependencies": { "Direct": {} } } }
                  }
                }
                """);

            var result = new AssetsFileReader().Read(assets, project);

            var edge = Assert.Single(result.DependencyEdges);
            Assert.Equal("Direct", edge.ParentPackageId);
            Assert.Equal("Transitive", edge.ChildPackageId);
            Assert.Equal("2.0.0", edge.ChildResolvedVersion);
            Assert.Equal(2, result.PackageSourceCount);
            Assert.True(result.PackageSourceMappingEnabled);
            var transitive = Assert.Single(result.PackageInventory, item => item.Id == "Transitive");
            Assert.Null(transitive.PackageSource);
            Assert.Equal("sha512-from-assets", transitive.ContentHash);
            Assert.True(transitive.SignaturePresent);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void CountsCredentialBearingRestoreSourcesWithoutRetainingTheirValue()
    {
        var root = Directory.CreateTempSubdirectory("PackageMedic.UntrustedSource.");
        try
        {
            var project = Path.Combine(root.FullName, "App.csproj");
            var assets = Path.Combine(root.FullName, "project.assets.json");
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(
                assets,
                """
                {
                  "targets": { "net8.0": { "Example.Package/1.0.0": {} } },
                  "libraries": { "Example.Package/1.0.0": { "type": "package", "path": "example.package/1.0.0" } },
                  "project": {
                    "restore": {
                      "sources": {
                        "https://api.nuget.org/v3/index.json": {},
                        "https://packages.example.test/v3/index.json?token=private": {}
                      }
                    },
                    "frameworks": { "net8.0": { "dependencies": { "Example.Package": {} } } }
                  }
                }
                """);

            var result = new AssetsFileReader().Read(assets, project, root.FullName);

            Assert.Equal(2, result.PackageSourceCount);
            Assert.False(result.PackageSourceMappingEnabled);
            Assert.DoesNotContain("private", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void DoesNotTreatAnInvalidConfiguredHttpsSourceAsALocalFeed()
    {
        var root = Directory.CreateTempSubdirectory("PackageMedic.InvalidConfiguredSource.");
        try
        {
            var project = Path.Combine(root.FullName, "App.csproj");
            var assets = Path.Combine(root.FullName, "project.assets.json");
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(
                Path.Combine(root.FullName, "NuGet.Config"),
                "<configuration><packageSources><clear /><add key=\"invalid\" value=\"https://packages.example.test/v3/index.json?token=private\" /></packageSources><packageSourceMapping><clear /><packageSource key=\"invalid\"><package pattern=\"*\" /></packageSource></packageSourceMapping></configuration>");
            File.WriteAllText(
                assets,
                """
                {
                  "targets": { "net8.0": { "Example.Package/1.0.0": {} } },
                  "libraries": { "Example.Package/1.0.0": { "type": "package", "path": "example.package/1.0.0" } },
                  "project": {
                    "restore": { "sources": { "file:///trusted-feed": {} } },
                    "frameworks": { "net8.0": { "dependencies": { "Example.Package": {} } } }
                  }
                }
                """);

            var result = new AssetsFileReader().Read(assets, project, root.FullName);

            Assert.Equal(1, result.PackageSourceCount);
            Assert.False(result.PackageSourceMappingEnabled);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void DoesNotTrustPackageMetadataThroughAReparsePoint()
    {
        var root = Directory.CreateTempSubdirectory("PackageMedic.PackageCacheRoot.");
        var outside = Directory.CreateTempSubdirectory("PackageMedic.PackageCacheOutside.");
        try
        {
            var project = Path.Combine(root.FullName, "App.csproj");
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            var outsidePackage = Directory.CreateDirectory(Path.Combine(outside.FullName, "example.package", "1.0.0"));
            File.WriteAllText(
                Path.Combine(outsidePackage.FullName, ".nupkg.metadata"),
                "{\"source\":\"https://attacker.example/v3/index.json\",\"contentHash\":\"forged\"}");
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(root.FullName, "packages"), outside.FullName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var packageFolder = JsonSerializer.Serialize(Path.Combine(root.FullName, "packages") + Path.DirectorySeparatorChar);
            var assets = Path.Combine(root.FullName, "project.assets.json");
            File.WriteAllText(
                assets,
                $$"""
                {
                  "targets": { "net8.0": { "Example.Package/1.0.0": {} } },
                  "libraries": {
                    "Example.Package/1.0.0": { "type": "package", "path": "example.package/1.0.0" }
                  },
                  "packageFolders": { {{packageFolder}}: {} },
                  "project": {
                    "restore": { "sources": { "https://api.nuget.org/v3/index.json": {} } },
                    "frameworks": { "net8.0": { "dependencies": { "Example.Package": {} } } }
                  }
                }
                """);

            var result = new AssetsFileReader().Read(assets, project, root.FullName);

            var package = Assert.Single(result.PackageInventory);
            Assert.Null(package.PackageSource);
            Assert.Null(package.ContentHash);
            Assert.Null(package.SignaturePresent);
        }
        finally
        {
            root.Delete(recursive: true);
            outside.Delete(recursive: true);
        }
    }

    [Fact]
    public void RequiresEffectivePackageSourceMappingsWithUsablePatterns()
    {
        var root = Directory.CreateTempSubdirectory("PackageMedic.SourceMapping.");
        try
        {
            var projectDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "src"));
            var project = Path.Combine(projectDirectory.FullName, "App.csproj");
            var assets = Path.Combine(projectDirectory.FullName, "project.assets.json");
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(
                assets,
                """
                {
                  "targets": { "net8.0": { "Example.Package/1.0.0": {} } },
                  "libraries": { "Example.Package/1.0.0": { "type": "package", "path": "example.package/1.0.0" } },
                  "project": {
                    "restore": { "sources": { "https://api.nuget.org/v3/index.json": {}, "https://packages.example.test/v3/index.json": {} } },
                    "frameworks": { "net8.0": { "dependencies": { "Example.Package": {} } } }
                  }
                }
                """);
            File.WriteAllText(
                Path.Combine(root.FullName, "NuGet.Config"),
                "<configuration><packageSources><clear /><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" /><add key=\"other\" value=\"https://packages.example.test/v3/index.json\" /></packageSources><packageSourceMapping><packageSource key=\"nuget.org\"><package pattern=\"Example.*\" /></packageSource></packageSourceMapping></configuration>");

            Assert.True(new AssetsFileReader().Read(assets, project, root.FullName).PackageSourceMappingEnabled);

            File.WriteAllText(
                Path.Combine(projectDirectory.FullName, "NuGet.Config"),
                "<configuration><packageSourceMapping><clear /><packageSource key=\"nuget.org\" /></packageSourceMapping></configuration>");

            Assert.False(new AssetsFileReader().Read(assets, project, root.FullName).PackageSourceMappingEnabled);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void AcceptsOnlyValidLockFilesInsideTheTrustedAnalysisRoot()
    {
        var root = Directory.CreateTempSubdirectory("PackageMedic.LockRoot.");
        var outside = Directory.CreateTempSubdirectory("PackageMedic.LockOutside.");
        try
        {
            var valid = Path.Combine(root.FullName, "packages.lock.json");
            var invalid = Path.Combine(root.FullName, "invalid.lock.json");
            var external = Path.Combine(outside.FullName, "packages.lock.json");
            const string lockJson = "{\"version\":2,\"dependencies\":{\"net8.0\":{}}}";
            File.WriteAllText(valid, lockJson);
            File.WriteAllText(invalid, "{\"version\":2}");
            File.WriteAllText(external, lockJson);

            Assert.True(NuGetLockFileValidator.IsTrustedAndValid(valid, root.FullName));
            Assert.False(NuGetLockFileValidator.IsTrustedAndValid(invalid, root.FullName));
            Assert.False(NuGetLockFileValidator.IsTrustedAndValid(external, root.FullName));
        }
        finally
        {
            root.Delete(recursive: true);
            outside.Delete(recursive: true);
        }
    }

    [Fact]
    public void DoesNotReadPackageMetadataFromUntrustedAssetsFolders()
    {
        var root = Directory.CreateTempSubdirectory("PackageMedic.TrustedAssets.");
        var outside = Directory.CreateTempSubdirectory("PackageMedic.UntrustedCache.");
        try
        {
            var project = Path.Combine(root.FullName, "App.csproj");
            var assets = Path.Combine(root.FullName, "project.assets.json");
            var packageDirectory = Directory.CreateDirectory(Path.Combine(outside.FullName, "example", "1.0.0"));
            File.WriteAllText(
                Path.Combine(packageDirectory.FullName, ".nupkg.metadata"),
                "{\"source\":\"https://untrusted.example/v3/index.json\",\"contentHash\":\"external\"}");
            var escapedFolder = JsonSerializer.Serialize(outside.FullName + Path.DirectorySeparatorChar);
            File.WriteAllText(
                assets,
                $$"""
                {
                  "targets": { "net8.0": { "Example/1.0.0": {} } },
                  "libraries": { "Example/1.0.0": { "type": "package", "path": "example/1.0.0", "sha512": "assets-hash" } },
                  "packageFolders": { {{escapedFolder}}: {} },
                  "project": { "frameworks": { "net8.0": { "dependencies": { "Example": {} } } } }
                }
                """);

            var package = Assert.Single(new AssetsFileReader().Read(assets, project).PackageInventory);

            Assert.Null(package.PackageSource);
            Assert.Equal("assets-hash", package.ContentHash);
        }
        finally
        {
            root.Delete(recursive: true);
            outside.Delete(recursive: true);
        }
    }

    [Fact]
    public void RejectsMalformedAssetsDuringProgressiveParsing()
    {
        var temporaryFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(
                temporaryFile,
                "{\"targets\":{\"net8.0\":{\"Example/1.0.0\":{}}}");

            Assert.ThrowsAny<JsonException>(
                () => new AssetsFileReader().Read(temporaryFile, "App.csproj"));
        }
        finally
        {
            File.Delete(temporaryFile);
        }
    }

    [Fact]
    public void ReadsJsonTokensThatCrossTheInitialBufferBoundary()
    {
        var temporaryFile = Path.GetTempFileName();
        var message = new string('x', AssetsFileReader.InitialJsonBufferBytes + 257);
        try
        {
            File.WriteAllText(
                temporaryFile,
                JsonSerializer.Serialize(new
                {
                    targets = new { },
                    libraries = new { },
                    project = new { frameworks = new { } },
                    logs = new[]
                    {
                        new
                        {
                            code = "NU1605",
                            level = "warning",
                            message,
                        },
                    },
                }));

            var result = new AssetsFileReader().Read(temporaryFile, "App.csproj");

            Assert.Equal(message, Assert.Single(result.Diagnostics).Evidence);
        }
        finally
        {
            File.Delete(temporaryFile);
        }
    }

    [Fact]
    public void ReadsUtf8BomAssetsLikeJsonDocumentDid()
    {
        var temporaryFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(
                temporaryFile,
                "{\"targets\":{},\"libraries\":{},\"project\":{\"frameworks\":{}}}",
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var result = new AssetsFileReader().Read(temporaryFile, "App.csproj");

            Assert.Empty(result.PackageInventory);
            Assert.Empty(result.Diagnostics);
        }
        finally
        {
            File.Delete(temporaryFile);
        }
    }

    [Fact]
    public void StreamsLargeAssetsGraphsWithDeterministicInventory()
    {
        const int packageCount = 6_000;
        var temporaryFile = Path.GetTempFileName();
        try
        {
            using (var stream = new FileStream(temporaryFile, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("targets");
                writer.WriteStartObject();
                writer.WritePropertyName("net9.0/linux-x64");
                writer.WriteStartObject();
                for (var index = packageCount - 1; index >= 0; index--)
                {
                    writer.WritePropertyName($"Package{index:D5}/1.0.{index}");
                    writer.WriteStartObject();
                    writer.WritePropertyName("dependencies");
                    writer.WriteStartObject();
                    if (index + 1 < packageCount)
                    {
                        writer.WriteString($"Package{index + 1:D5}", "1.0.0");
                    }

                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
                writer.WriteEndObject();

                writer.WritePropertyName("ignored");
                writer.WriteStartArray();
                for (var index = 0; index < packageCount; index++)
                {
                    writer.WriteNumberValue(index);
                }

                writer.WriteEndArray();

                writer.WritePropertyName("libraries");
                writer.WriteStartObject();
                for (var index = 0; index < packageCount; index++)
                {
                    writer.WritePropertyName($"Package{index:D5}/1.0.{index}");
                    writer.WriteStartObject();
                    writer.WriteString("type", "package");
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();

                writer.WritePropertyName("project");
                writer.WriteStartObject();
                writer.WritePropertyName("frameworks");
                writer.WriteStartObject();
                writer.WritePropertyName("net9.0");
                writer.WriteStartObject();
                writer.WritePropertyName("dependencies");
                writer.WriteStartObject();
                for (var index = 0; index < packageCount; index += 1_000)
                {
                    writer.WritePropertyName($"Package{index:D5}");
                    writer.WriteStartObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            var reader = new AssetsFileReader();
            var first = reader.Read(temporaryFile, "Large.csproj");
            var second = reader.Read(temporaryFile, "Large.csproj");

            Assert.Equal(packageCount, first.PackageInventory.Count);
            Assert.Equal(packageCount, first.ResolvedPackages.Count);
            Assert.Equal(packageCount - 6, first.TransitivePackages.Count);
            Assert.Equal(
                first.PackageInventory.Select(PackageIdentity),
                second.PackageInventory.Select(PackageIdentity));
            Assert.Equal("Package00000", first.PackageInventory[0].Id);
            Assert.Equal(PackageDependencyKind.Direct, first.PackageInventory[0].DependencyKind);
            Assert.Equal("Package00001", first.PackageInventory[6].Id);
            Assert.Equal(PackageDependencyKind.Transitive, first.PackageInventory[6].DependencyKind);
        }
        finally
        {
            File.Delete(temporaryFile);
        }

        static string PackageIdentity(PackageInventoryItem item) =>
            $"{item.Framework}|{item.RuntimeIdentifier}|{item.DependencyKind}|{item.Id}|{item.ResolvedVersion}";
    }

    [Fact]
    public void DiscoverySkipsGeneratedDependencyAndReportDirectories()
    {
        var root = Directory.CreateTempSubdirectory("PackageMedic.Discovery.");
        try
        {
            var project = Path.Combine(root.FullName, "App.csproj");
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            foreach (var generated in new[]
                     {
                         "artifacts", ".next", ".wrangler", ".packagemedic-time-machine", "dist", "out",
                     })
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
    public void DirectoryDiscoveryRestoresProjectsOmittedFromItsSingleSolution()
    {
        var root = Directory.CreateTempSubdirectory("PackageMedic.MixedSolutionDiscovery.");
        try
        {
            var included = Path.Combine(root.FullName, "Included.csproj");
            var omitted = Path.Combine(root.FullName, "Omitted.csproj");
            var solution = Path.Combine(root.FullName, "Repository.slnx");
            File.WriteAllText(included, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(omitted, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(solution, "<Solution><Project Path=\"Included.csproj\" /></Solution>");

            var discovery = new ProjectDiscovery().Discover(root.FullName);

            Assert.Equal([included, omitted], discovery.Projects);
            Assert.Equal([solution, omitted], discovery.RestoreTargets);
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
    public async Task RestoreCompletesSolutionsBeforeRestoringProjectsOmittedFromThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "PackageMedic.RestorePhases");
        var solution = Path.Combine(root, "Repository.sln");
        var omitted = Path.Combine(root, "Omitted.csproj");
        var runner = new RestorePhaseRunner(solution);
        var restore = new RestoreRunner(runner, TimeSpan.FromSeconds(5), maxDegreeOfParallelism: 4);

        var result = await restore.RestoreAsync(
            new DiscoveryResult(root, [solution], [omitted], [solution, omitted]),
            null,
            TestContext.Current.CancellationToken);

        Assert.Empty(result.Errors);
        Assert.False(runner.ProjectStartedBeforeSolutionFinished);
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
    public async Task EvaluationRejectsAnUnboundedTargetFrameworkFanOut()
    {
        var frameworks = string.Join(';', Enumerable.Range(0, MsBuildProjectEvaluator.MaximumTargetFrameworksPerProject + 1)
            .Select(index => $"net8.0-f{index}"));
        var output =
            "{\"Properties\":{\"TargetFrameworks\":\"" + frameworks +
            "\",\"ProjectAssetsFile\":\"obj/project.assets.json\"}," +
            "\"Items\":{\"PackageReference\":[],\"PackageVersion\":[]}}";
        var evaluator = new MsBuildProjectEvaluator(
            new DelayedProcessRunner(_ => new ProcessResult(0, output, string.Empty)),
            TimeSpan.FromSeconds(5),
            maxDegreeOfParallelism: 2);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => evaluator.EvaluateAsync(
            Path.Combine(Path.GetTempPath(), "PackageMedic.FrameworkLimit", "App.csproj"),
            TestContext.Current.CancellationToken));

        Assert.Contains("target-framework safety limit", exception.Message, StringComparison.OrdinalIgnoreCase);
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

    private sealed class RestorePhaseRunner(string solution) : IProcessRunner
    {
        private int solutionFinished;

        public bool ProjectStartedBeforeSolutionFinished { get; private set; }

        public async Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            var target = arguments[1];
            if (target.Equals(solution, StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(30, cancellationToken);
                Volatile.Write(ref solutionFinished, 1);
            }
            else if (Volatile.Read(ref solutionFinished) == 0)
            {
                ProjectStartedBeforeSolutionFinished = true;
            }

            return new ProcessResult(0, string.Empty, string.Empty);
        }
    }
}
