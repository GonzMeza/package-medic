using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

using PackageMedic.Core;

namespace PackageMedic.Core.Tests;

public sealed class PackageVersionEditorTests
{
    [Fact]
    public void EditsOneCentralDeclarationAndReturnsPortableEvidence()
    {
        using var snapshot = new TestSnapshot();
        var props = snapshot.Write(
            "Directory.Packages.props",
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Example.Package" Version="1.2.3" />
              </ItemGroup>
            </Project>
            """);
        var firstProject = snapshot.Write("src/One/One.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var secondProject = snapshot.Write("src/Two/Two.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var packages = new[]
        {
            Inventory(firstProject, props, 3, "central", "1.2.3", "net8.0"),
            Inventory(secondProject, props, 3, "central", "1.2.3", "net9.0"),
        };

        var result = PackageVersionEditor.Apply(new(
            snapshot.Root,
            "Example.Package",
            "2.0.0-rc.1+build-5",
            packages));

        Assert.Equal("Directory.Packages.props", result.File);
        Assert.False(Path.IsPathFullyQualified(result.File));
        Assert.Equal(3, result.Line);
        Assert.Equal(PackageVersionDeclarationKind.CentralPackageVersion, result.Kind);
        Assert.Equal("1.2.3", result.BeforeVersion);
        Assert.Equal("2.0.0-rc.1+build-5", result.CandidateVersion);
        Assert.Equal(["src/One/One.csproj", "src/Two/Two.csproj"], result.AffectedProjects);
        Assert.Equal(
            "2.0.0-rc.1+build-5",
            XDocument.Load(props).Descendants("PackageVersion").Single().Attribute("Version")!.Value);
        Assert.Empty(Directory.EnumerateFiles(snapshot.Root, ".packagemedic-*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void EditsPackageReferenceVersionElement()
    {
        using var snapshot = new TestSnapshot();
        var project = snapshot.Write(
            "App.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Example.Package">
                  <Version>1.0.0</Version>
                </PackageReference>
              </ItemGroup>
            </Project>
            """);

        var result = PackageVersionEditor.Apply(new(
            snapshot.Root,
            "Example.Package",
            "1.1.0",
            [Inventory(project, project, 3, "project", "1.0.0")]));

        Assert.Equal(PackageVersionDeclarationKind.PackageReferenceVersion, result.Kind);
        Assert.Equal(
            "1.1.0",
            XDocument.Load(project).Descendants("Version").Single().Value);
    }

    [Fact]
    public void EditsPackageReferenceVersionOverrideAttribute()
    {
        using var snapshot = new TestSnapshot();
        var project = snapshot.Write(
            "App.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Update="Example.Package" VersionOverride="3.0.0" />
              </ItemGroup>
            </Project>
            """);

        var result = PackageVersionEditor.Apply(new(
            snapshot.Root,
            "Example.Package",
            "3.1.0",
            [Inventory(project, project, 3, "override", "3.0.0")]));

        Assert.Equal(PackageVersionDeclarationKind.PackageReferenceOverride, result.Kind);
        Assert.Equal(
            "3.1.0",
            XDocument.Load(project).Descendants("PackageReference").Single().Attribute("VersionOverride")!.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1.*")]
    [InlineData("[1.0.0,2.0.0)")]
    [InlineData("$(PackageVersion)")]
    [InlineData("1.2.3 ")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3+bad..metadata")]
    public void RejectsNonExactCandidateVersions(string version)
    {
        using var snapshot = CreateDirectSnapshot();

        Assert.Throws<ArgumentException>(() => PackageVersionEditor.Apply(new(
            snapshot.Root,
            "Example.Package",
            version,
            [Inventory(snapshot.Project!, snapshot.Project!, 3, "project", "1.0.0")])));
    }

    [Fact]
    public void PublicValidatorsAcceptExactNuGetIdentity()
    {
        PackageVersionEditor.ValidatePackageId("Example.Package_2");
        PackageVersionEditor.ValidateExactVersion("2.0.0-rc.1+build-5");
    }

    [Theory]
    [InlineData("Example Package")]
    [InlineData("Example/Package")]
    [InlineData("Example:Package")]
    [InlineData(" Example.Package")]
    [InlineData(".Example.Package")]
    [InlineData("Example.Package-")]
    public void RejectsUnsafePackageIds(string packageId)
    {
        using var snapshot = CreateDirectSnapshot();

        Assert.Throws<ArgumentException>(() => PackageVersionEditor.Apply(new(
            snapshot.Root,
            packageId,
            "2.0.0",
            [Inventory(snapshot.Project!, snapshot.Project!, 3, "project", "1.0.0", id: packageId)])));
    }

    [Fact]
    public void RejectsTransitiveOnlyPackagesWithoutEditing()
    {
        using var snapshot = CreateDirectSnapshot();
        var before = File.ReadAllText(snapshot.Project!);

        var exception = Assert.Throws<InvalidOperationException>(() => PackageVersionEditor.Apply(new(
            snapshot.Root,
            "Example.Package",
            "2.0.0",
            [Inventory(
                snapshot.Project!,
                snapshot.Project!,
                3,
                "resolved",
                null,
                dependencyKind: PackageDependencyKind.Transitive)])));

        Assert.Contains("transitive-only", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(snapshot.Project!));
    }

    [Fact]
    public void RejectsMultipleEffectiveDeclarations()
    {
        using var snapshot = new TestSnapshot();
        var first = snapshot.Write(
            "One.csproj",
            ProjectWithPackage("Example.Package", "1.0.0"));
        var second = snapshot.Write(
            "Two.csproj",
            ProjectWithPackage("Example.Package", "1.0.0"));

        var exception = Assert.Throws<InvalidOperationException>(() => PackageVersionEditor.Apply(new(
            snapshot.Root,
            "Example.Package",
            "2.0.0",
            [
                Inventory(first, first, 3, "project", "1.0.0"),
                Inventory(second, second, 3, "project", "1.0.0"),
            ])));

        Assert.Contains("multiple effective declarations", exception.Message, StringComparison.Ordinal);
        Assert.Contains("1.0.0", File.ReadAllText(first), StringComparison.Ordinal);
        Assert.Contains("1.0.0", File.ReadAllText(second), StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsDuplicateXmlDeclarationsEvenWhenInventoryPointsToOne()
    {
        using var snapshot = new TestSnapshot();
        var project = snapshot.Write(
            "App.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Example.Package" Version="1.0.0" />
                <PackageReference Update="Example.Package" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => PackageVersionEditor.Apply(new(
            snapshot.Root,
            "Example.Package",
            "2.0.0",
            [Inventory(project, project, 3, "project", "1.0.0")])));

        Assert.Contains("multiple XML declarations", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<ItemGroup Condition=\"'$(TargetFramework)' == 'net8.0'\">")]
    [InlineData("<ItemGroup>")]
    public void RejectsConditionalDeclarations(string itemGroup)
    {
        using var snapshot = new TestSnapshot();
        var metadataCondition = itemGroup == "<ItemGroup>"
            ? "<Version Condition=\"'$(UseV1)' == 'true'\">1.0.0</Version>"
            : "<Version>1.0.0</Version>";
        var project = snapshot.Write(
            "App.csproj",
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              {itemGroup}
                <PackageReference Include="Example.Package">
                  {metadataCondition}
                </PackageReference>
              </ItemGroup>
            </Project>
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => PackageVersionEditor.Apply(new(
            snapshot.Root,
            "Example.Package",
            "2.0.0",
            [Inventory(project, project, 3, "project", "1.0.0")])));

        Assert.Contains("conditional", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsDynamicExistingVersion()
    {
        using var snapshot = new TestSnapshot();
        var project = snapshot.Write(
            "App.csproj",
            ProjectWithPackage("Example.Package", "$(ExampleVersion)"));

        Assert.Throws<ArgumentException>(() => PackageVersionEditor.Apply(new(
            snapshot.Root,
            "Example.Package",
            "2.0.0",
            [Inventory(project, project, 3, "project", "1.0.0")])));
        Assert.Contains("$(ExampleVersion)", File.ReadAllText(project), StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAttributeAndElementVersionWithoutChoosingOne()
    {
        using var snapshot = new TestSnapshot();
        var project = snapshot.Write(
            "App.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Example.Package" Version="1.0.0">
                  <Version>1.0.0</Version>
                </PackageReference>
              </ItemGroup>
            </Project>
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => PackageVersionEditor.Apply(new(
            snapshot.Root,
            "Example.Package",
            "2.0.0",
            [Inventory(project, project, 3, "project", "1.0.0")])));

        Assert.Contains("exactly one literal Version", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsExternalDeclarationAndLeavesItUnchanged()
    {
        using var snapshot = new TestSnapshot();
        using var outside = new TestSnapshot();
        var project = snapshot.Write("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var external = outside.Write("Directory.Packages.props", ProjectWithCentralPackage("Example.Package", "1.0.0"));
        var before = File.ReadAllText(external);

        Assert.Throws<InvalidOperationException>(() => PackageVersionEditor.Apply(new(
            snapshot.Root,
            "Example.Package",
            "2.0.0",
            [Inventory(project, external, 3, "central", "1.0.0")])));
        Assert.Equal(before, File.ReadAllText(external));
    }

    [Fact]
    public void RejectsSymbolicLinkDeclaration()
    {
        using var snapshot = new TestSnapshot();
        var project = snapshot.Write("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var target = snapshot.Write("Actual.props", ProjectWithCentralPackage("Example.Package", "1.0.0"));
        var link = Path.Combine(snapshot.Root, "Directory.Packages.props");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        Assert.Throws<InvalidOperationException>(() => PackageVersionEditor.Apply(new(
            snapshot.Root,
            "Example.Package",
            "2.0.0",
            [Inventory(project, link, 3, "central", "1.0.0")])));
        Assert.Contains("1.0.0", File.ReadAllText(target), StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsDtdEnabledXml()
    {
        using var snapshot = new TestSnapshot();
        var project = snapshot.Write(
            "App.csproj",
            """
            <!DOCTYPE Project [<!ENTITY version "1.0.0">]>
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Example.Package" Version="&version;" />
              </ItemGroup>
            </Project>
            """);

        Assert.Throws<InvalidDataException>(() => PackageVersionEditor.Apply(new(
            snapshot.Root,
            "Example.Package",
            "2.0.0",
            [Inventory(project, project, 4, "project", "1.0.0")])));
    }

    [Fact]
    public void RejectsOversizedDeclarationBeforeParsing()
    {
        using var snapshot = new TestSnapshot();
        var project = snapshot.Write("App.csproj", string.Empty);
        using (var stream = new FileStream(project, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(XmlItemLineLocator.MaximumSourceXmlBytes + 1);
        }

        Assert.Throws<InvalidDataException>(() => PackageVersionEditor.Apply(new(
            snapshot.Root,
            "Example.Package",
            "2.0.0",
            [Inventory(project, project, 1, "project", "1.0.0")])));
    }

    [Fact]
    public void RejectsStaleSourceLineInsteadOfGuessing()
    {
        using var snapshot = CreateDirectSnapshot();

        var exception = Assert.Throws<InvalidOperationException>(() => PackageVersionEditor.Apply(new(
            snapshot.Root,
            "Example.Package",
            "2.0.0",
            [Inventory(snapshot.Project!, snapshot.Project!, 30, "project", "1.0.0")])));

        Assert.Contains("no longer matches line", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReturnsNoChangeForNuGetEquivalentVersion()
    {
        using var snapshot = CreateDirectSnapshot();
        var before = File.ReadAllBytes(snapshot.Project!);
        var beforeHash = Sha256(before);

        var result = PackageVersionEditor.Apply(new(
            snapshot.Root,
            "Example.Package",
            "1.0.0+different-build-metadata",
            [Inventory(snapshot.Project!, snapshot.Project!, 3, "project", "1.0.0")])
        {
            ExpectedSourceSha256 = beforeHash.ToUpperInvariant(),
        });

        Assert.True(result.NoChange);
        Assert.Equal(beforeHash, result.SourceSha256Before);
        Assert.Equal(beforeHash, result.SourceSha256After);
        Assert.Equal(before, File.ReadAllBytes(snapshot.Project!));
    }

    [Fact]
    public void TreatsVersionCasingNumericPrereleaseAndMetadataAsNuGetEquivalent()
    {
        using var snapshot = new TestSnapshot();
        var project = snapshot.Write(
            "App.csproj",
            ProjectWithPackage("Example.Package", "1.0-RC.01+Build.One"));
        var before = File.ReadAllBytes(project);

        var result = PackageVersionEditor.Apply(new(
            snapshot.Root,
            "example.package",
            "1.0.0-rc.1+other",
            [Inventory(project, project, 3, "project", "1.0-RC.01+Build.One", id: "Example.Package")]));

        Assert.True(result.NoChange);
        Assert.Equal(before, File.ReadAllBytes(project));
    }

    [Fact]
    public void RejectsMismatchedObservedHashBeforeEditing()
    {
        using var snapshot = CreateDirectSnapshot();
        var before = File.ReadAllBytes(snapshot.Project!);

        var exception = Assert.Throws<InvalidOperationException>(() => PackageVersionEditor.Apply(new(
            snapshot.Root,
            "Example.Package",
            "2.0.0",
            [Inventory(snapshot.Project!, snapshot.Project!, 3, "project", "1.0.0")])
        {
            ExpectedSourceSha256 = new string('0', 64),
        }));

        Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(snapshot.Project!));
    }

    [Fact]
    public void ReplacesOnlyVersionBytesAndPreservesUtf8BomCrLfCommentsAndFormatting()
    {
        using var snapshot = new TestSnapshot();
        const string original =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<Project>\r\n" +
            "  <!-- keep this comment and every space -->\r\n" +
            "  <ItemGroup>\r\n" +
            "    <PackageVersion  Include='Example.Package'   Version = '1.2.3-rc.1+old'  PrivateAssets='all' />\r\n" +
            "  </ItemGroup>\r\n" +
            "</Project>\r\n";
        const string candidate = "2.0.0-preview.2+new";
        var bytes = WithPreamble(new UTF8Encoding(true, true), original);
        var props = snapshot.WriteBytes("Directory.Packages.props", bytes);
        var project = snapshot.Write("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var expected = WithPreamble(
            new UTF8Encoding(true, true),
            original.Replace("1.2.3-rc.1+old", candidate, StringComparison.Ordinal));

        var result = PackageVersionEditor.Apply(new(
            snapshot.Root,
            "Example.Package",
            candidate,
            [Inventory(project, props, 5, "central", "1.2.3-rc.1+old")])
        {
            ExpectedSourceSha256 = Sha256(bytes),
        });

        Assert.False(result.NoChange);
        Assert.Equal(Sha256(bytes), result.SourceSha256Before);
        Assert.Equal(Sha256(expected), result.SourceSha256After);
        Assert.Equal(expected, File.ReadAllBytes(props));
        Assert.True(File.ReadAllBytes(props).AsSpan().StartsWith(Encoding.UTF8.Preamble));
    }

    [Fact]
    public void PreservesUtf8AndUtf16EncodingByteForByteOutsideTheValue()
    {
        var formats = new[]
        {
            new EncodingFixture("utf8-no-bom", new UTF8Encoding(false, true), false, "utf-8", "\n"),
            new EncodingFixture("utf8-cr-only", new UTF8Encoding(false, true), false, "utf-8", "\r"),
            new EncodingFixture("utf8-bom", new UTF8Encoding(false, true), true, "utf-8", "\r\n"),
            new EncodingFixture("utf16-le-bom", new UnicodeEncoding(false, true, true), true, "utf-16", "\r\n"),
            new EncodingFixture("utf16-be-bom", new UnicodeEncoding(true, true, true), true, "utf-16", "\n"),
            new EncodingFixture("utf16-le-no-bom", new UnicodeEncoding(false, false, true), false, "utf-16", "\n"),
            new EncodingFixture("utf16-be-no-bom", new UnicodeEncoding(true, false, true), false, "utf-16", "\r\n"),
        };
        foreach (var format in formats)
        {
            using var snapshot = new TestSnapshot();
            var newline = format.NewLine;
            var bomlessUtf16 = format.Name.StartsWith("utf16", StringComparison.Ordinal) && !format.IncludePreamble;
            var declaration = bomlessUtf16
                ? " \t"
                : $"<?xml version='1.0' encoding='{format.Declaration}'?>{newline}";
            var original =
                declaration +
                $"<Project>{newline}" +
                $"  <ItemGroup>{newline}" +
                $"    <PackageReference Include = 'Example.Package'>{newline}" +
                $"      <Version>4.5.6</Version>{newline}" +
                $"    </PackageReference>{newline}" +
                $"  </ItemGroup>{newline}" +
                $"</Project>{newline}";
            var bytes = format.WithOptionalPreamble(original);
            var project = snapshot.WriteBytes("App.csproj", bytes);
            var expected = format.WithOptionalPreamble(
                original.Replace("4.5.6", "5.0.0", StringComparison.Ordinal));

            var result = PackageVersionEditor.Apply(new(
                snapshot.Root,
                "Example.Package",
                "5.0.0",
                [Inventory(project, project, bomlessUtf16 ? 3 : 4, "project", "4.5.6")])
            {
                ExpectedSourceSha256 = Sha256(bytes),
            });

            Assert.Equal(expected, File.ReadAllBytes(project));
            Assert.Equal(Sha256(expected), result.SourceSha256After);
        }
    }

    private static TestSnapshot CreateDirectSnapshot()
    {
        var snapshot = new TestSnapshot();
        snapshot.Project = snapshot.Write("App.csproj", ProjectWithPackage("Example.Package", "1.0.0"));
        return snapshot;
    }

    private static PackageInventoryItem Inventory(
        string project,
        string sourceFile,
        int sourceLine,
        string versionSource,
        string? requestedVersion,
        string framework = "net8.0",
        PackageDependencyKind dependencyKind = PackageDependencyKind.Direct,
        string id = "Example.Package") => new(
            project,
            framework,
            id,
            requestedVersion ?? "1.0.0",
            dependencyKind,
            requestedVersion,
            versionSource,
            SourceFile: sourceFile,
            SourceLine: sourceLine);

    private static string ProjectWithPackage(string id, string version) =>
        $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <ItemGroup>
            <PackageReference Include="{{id}}" Version="{{version}}" />
          </ItemGroup>
        </Project>
        """;

    private static string ProjectWithCentralPackage(string id, string version) =>
        $$"""
        <Project>
          <ItemGroup>
            <PackageVersion Include="{{id}}" Version="{{version}}" />
          </ItemGroup>
        </Project>
        """;

    private static string Sha256(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static byte[] WithPreamble(Encoding encoding, string value)
    {
        var payload = encoding.GetBytes(value);
        var preamble = encoding.GetPreamble();
        return [.. preamble, .. payload];
    }

    private sealed class TestSnapshot : IDisposable
    {
        public TestSnapshot()
        {
            Root = Directory.CreateTempSubdirectory("PackageMedic.VersionEdit.").FullName;
        }

        public string Root { get; }

        public string? Project { get; set; }

        public string Write(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public string WriteBytes(string relativePath, byte[] content)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            return path;
        }

        public void Dispose()
        {
            if (!Directory.Exists(Root))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed record EncodingFixture(
        string Name,
        Encoding Encoding,
        bool IncludePreamble,
        string Declaration,
        string NewLine)
    {
        public byte[] WithOptionalPreamble(string value)
        {
            var payload = Encoding.GetBytes(value);
            return IncludePreamble ? [.. Encoding.GetPreamble(), .. payload] : payload;
        }

        public override string ToString() => Name;
    }
}
