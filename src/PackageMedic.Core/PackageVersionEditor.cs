using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PackageMedic.Core;

public enum PackageVersionDeclarationKind
{
    CentralPackageVersion,
    PackageReferenceVersion,
    PackageReferenceOverride,
}

public sealed record PackageVersionEditRequest(
    string SnapshotRoot,
    string PackageId,
    string CandidateVersion,
    IReadOnlyList<PackageInventoryItem> Packages)
{
    public string? ExpectedSourceSha256 { get; init; }
}

public sealed record PackageVersionEditResult(
    string PackageId,
    string File,
    int Line,
    PackageVersionDeclarationKind Kind,
    string BeforeVersion,
    string CandidateVersion,
    IReadOnlyList<string> AffectedProjects)
{
    public bool NoChange { get; init; }

    public string SourceSha256Before { get; init; } = string.Empty;

    public string SourceSha256After { get; init; } = string.Empty;
}

/// <summary>
/// Applies one exact package-version change to an isolated analysis snapshot. The editor
/// deliberately rejects declarations that require interpreting MSBuild conditions or
/// expressions; a simulation must fail closed rather than edit a declaration by inference.
/// </summary>
public static class PackageVersionEditor
{
    public const long MaximumMutationXmlBytes = 16L * 1024 * 1024;
    private const int MaximumPackageIdCharacters = 100;
    private const int MaximumVersionCharacters = 256;

    public static PackageVersionEditResult Apply(PackageVersionEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SnapshotRoot);
        ArgumentNullException.ThrowIfNull(request.Packages);
        ValidatePackageId(request.PackageId);
        ValidateExactVersion(request.CandidateVersion, nameof(request.CandidateVersion));

        var root = Path.GetFullPath(request.SnapshotRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"The simulation snapshot '{root}' does not exist.");
        }

        if (IsReparsePoint(root))
        {
            throw new InvalidOperationException("The simulation snapshot root cannot be a symbolic link or reparse point.");
        }

        var matching = request.Packages
            .Where(item => item.Id.Equals(request.PackageId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var direct = matching
            .Where(item => item.DependencyKind == PackageDependencyKind.Direct)
            .ToArray();
        if (direct.Length == 0)
        {
            throw new InvalidOperationException(
                matching.Length == 0
                    ? $"Package '{request.PackageId}' was not found in the selected dependency graph."
                    : $"Package '{request.PackageId}' is transitive-only; Dependency Time Machine requires one direct declaration.");
        }

        var declarations = direct.Select(item => ResolveInventoryDeclaration(root, item)).ToArray();
        var selected = declarations[0];
        if (declarations.Skip(1).Any(item => !DeclarationIdentityEquals(selected, item)))
        {
            throw new InvalidOperationException(
                $"Package '{request.PackageId}' has multiple effective declarations in the selected scope; select a narrower project or solution.");
        }

        var affectedProjects = direct
            .Select(item => ToContainedPortablePath(root, item.Project, "An affected project"))
            .Distinct(PortablePathComparer())
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (affectedProjects.Length == 0)
        {
            throw new InvalidOperationException($"Package '{request.PackageId}' has no affected projects in the simulation snapshot.");
        }

        var source = ReadSourceDocument(selected.SourceFile);
        ValidateExpectedHash(request.ExpectedSourceSha256, source.Sha256);
        var document = LoadSafeXml(source.Text, source.Encoding);
        var candidates = FindPackageItems(document, request.PackageId).ToArray();
        if (candidates.Length != 1)
        {
            throw new InvalidOperationException(
                candidates.Length == 0
                    ? $"Could not find one literal XML declaration for package '{request.PackageId}'."
                    : $"Package '{request.PackageId}' has multiple XML declarations in '{selected.PortableSourceFile}'.");
        }

        var item = candidates[0];
        var actualLine = (item as IXmlLineInfo)?.LineNumber ?? 0;
        if (actualLine <= 0 || actualLine != selected.SourceLine)
        {
            throw new InvalidOperationException(
                $"The evaluated declaration for package '{request.PackageId}' no longer matches line {selected.SourceLine} in '{selected.PortableSourceFile}'.");
        }

        if (HasCondition(item))
        {
            throw new InvalidOperationException(
                $"Package '{request.PackageId}' uses a conditional declaration; select an explicit unconditional declaration before simulating it.");
        }

        var expectedElement = selected.Kind == PackageVersionDeclarationKind.CentralPackageVersion
            ? "PackageVersion"
            : "PackageReference";
        if (!item.Name.LocalName.Equals(expectedElement, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The evaluated source for package '{request.PackageId}' does not match its '{selected.VersionSource}' version source.");
        }

        var metadataName = selected.Kind == PackageVersionDeclarationKind.PackageReferenceOverride
            ? "VersionOverride"
            : "Version";
        var lexicalItems = XmlLexicalScanner.Scan(source.Text)
            .Where(element =>
                element.StartLine == selected.SourceLine &&
                element.LocalName.Equals(expectedElement, StringComparison.Ordinal) &&
                HasLiteralPackageId(element, request.PackageId))
            .ToArray();
        if (lexicalItems.Length != 1)
        {
            throw new InvalidOperationException(
                $"Could not locate the exact byte range of package '{request.PackageId}' without rewriting its XML file.");
        }

        var versionLocation = FindSingleVersionLocation(
            lexicalItems[0],
            metadataName,
            request.PackageId,
            source.Text);
        var beforeVersion = source.Text[versionLocation.Start..versionLocation.End];
        ValidateExactVersion(beforeVersion, "the existing package version");
        if (!beforeVersion.Equals(selected.RequestedVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The literal version for package '{request.PackageId}' does not match the evaluated version '{selected.RequestedVersion}'.");
        }

        if (AreNuGetEquivalent(beforeVersion, request.CandidateVersion))
        {
            EnsureSourceHashUnchanged(selected.SourceFile, source.Sha256);
            return new PackageVersionEditResult(
                request.PackageId,
                selected.PortableSourceFile,
                selected.SourceLine,
                selected.Kind,
                beforeVersion,
                request.CandidateVersion,
                affectedProjects)
            {
                NoChange = true,
                SourceSha256Before = source.Sha256,
                SourceSha256After = source.Sha256,
            };
        }

        var editedText = string.Concat(
            source.Text.AsSpan(0, versionLocation.Start),
            request.CandidateVersion,
            source.Text.AsSpan(versionLocation.End));
        var editedBytes = source.Encode(editedText);
        var editedHash = ComputeSha256(editedBytes);
        WriteAtomically(root, selected.SourceFile, source.Sha256, editedBytes);
        return new PackageVersionEditResult(
            request.PackageId,
            selected.PortableSourceFile,
            selected.SourceLine,
            selected.Kind,
            beforeVersion,
            request.CandidateVersion,
            affectedProjects)
        {
            SourceSha256Before = source.Sha256,
            SourceSha256After = editedHash,
        };
    }

    internal static bool IsExactVersion(string value) => TryParseExactVersion(value, out _);

    internal static bool AreNuGetEquivalent(string left, string right)
    {
        if (!TryParseExactVersion(left, out var leftVersion) ||
            !TryParseExactVersion(right, out var rightVersion))
        {
            return false;
        }

        return leftVersion.Core.SequenceEqual(rightVersion.Core) &&
               leftVersion.Prerelease.SequenceEqual(rightVersion.Prerelease, StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryParseExactVersion(string value, out ComparablePackageVersion version)
    {
        version = default!;
        if (string.IsNullOrWhiteSpace(value) ||
            !value.Equals(value.Trim(), StringComparison.Ordinal) ||
            value.Length > MaximumVersionCharacters ||
            value.Any(char.IsControl))
        {
            return false;
        }

        var metadataSplit = value.Split('+', 2);
        if (metadataSplit.Length == 2 && !ValidIdentifiers(metadataSplit[1]))
        {
            return false;
        }

        var prereleaseSplit = metadataSplit[0].Split('-', 2);
        if (prereleaseSplit.Length == 2 && !ValidIdentifiers(prereleaseSplit[1]))
        {
            return false;
        }

        var coreParts = prereleaseSplit[0].Split('.');
        if (coreParts.Length is < 1 or > 4)
        {
            return false;
        }

        var core = new long[4];
        for (var index = 0; index < coreParts.Length; index++)
        {
            var part = coreParts[index];
            if (part.Length == 0 ||
                !part.All(char.IsAsciiDigit) ||
                !long.TryParse(part, out core[index]))
            {
                return false;
            }
        }

        var prerelease = prereleaseSplit.Length == 1
            ? []
            : prereleaseSplit[1]
                .Split('.')
                .Select(NormalizePrereleaseIdentifier)
                .ToArray();
        version = new ComparablePackageVersion(core, prerelease);
        return true;
    }

    public static void ValidatePackageId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !value.Equals(value.Trim(), StringComparison.Ordinal) ||
            value.Length > MaximumPackageIdCharacters ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            !char.IsAsciiLetterOrDigit(value[^1]) ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_')))
        {
            throw new ArgumentException(
                "The package ID must be a non-empty NuGet ID containing only letters, digits, '.', '-', or '_'.",
                nameof(value));
        }
    }

    public static void ValidateExactVersion(string value) =>
        ValidateExactVersion(value, nameof(value));

    private static void ValidateExactVersion(string value, string parameterName)
    {
        if (!IsExactVersion(value))
        {
            throw new ArgumentException(
                "The package version must be one exact NuGet version; ranges, floating versions, and MSBuild expressions are not supported.",
                parameterName);
        }
    }

    private static bool ValidIdentifiers(string value) =>
        value.Length > 0 &&
        value.Split('.').All(identifier =>
            identifier.Length > 0 &&
            identifier.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));

    private static string NormalizePrereleaseIdentifier(string value)
    {
        if (!value.All(char.IsAsciiDigit))
        {
            return value.ToLowerInvariant();
        }

        var normalized = value.TrimStart('0');
        return normalized.Length == 0 ? "0" : normalized;
    }

    private static InventoryDeclaration ResolveInventoryDeclaration(string root, PackageInventoryItem item)
    {
        if (string.IsNullOrWhiteSpace(item.SourceFile) ||
            item.SourceLine is not > 0 ||
            string.IsNullOrWhiteSpace(item.RequestedVersion))
        {
            throw new InvalidOperationException(
                $"Package '{item.Id}' does not expose one explicit version declaration that can be simulated safely.");
        }

        var kind = item.VersionSource.ToLowerInvariant() switch
        {
            "central" => PackageVersionDeclarationKind.CentralPackageVersion,
            "project" => PackageVersionDeclarationKind.PackageReferenceVersion,
            "override" => PackageVersionDeclarationKind.PackageReferenceOverride,
            _ => throw new InvalidOperationException(
                $"Package '{item.Id}' uses unsupported or dynamic version source '{item.VersionSource}'."),
        };
        var sourceFile = ResolveContainedFile(root, item.SourceFile);
        var portableSourceFile = ToPortablePath(root, sourceFile);
        return new InventoryDeclaration(
            sourceFile,
            portableSourceFile,
            item.SourceLine.Value,
            kind,
            item.VersionSource,
            item.RequestedVersion);
    }

    private static bool DeclarationIdentityEquals(InventoryDeclaration left, InventoryDeclaration right) =>
        PathComparer().Equals(left.SourceFile, right.SourceFile) &&
        left.SourceLine == right.SourceLine &&
        left.Kind == right.Kind &&
        left.RequestedVersion.Equals(right.RequestedVersion, StringComparison.OrdinalIgnoreCase);

    private static string ResolveContainedFile(string root, string value)
    {
        var candidate = Path.GetFullPath(value, root);
        if (!File.Exists(candidate) ||
            !ProjectDiscovery.IsSafelyContained(root, candidate) ||
            IsReparsePoint(candidate))
        {
            throw new InvalidOperationException("The package declaration must be a regular file inside the simulation snapshot.");
        }

        return candidate;
    }

    private static string ToContainedPortablePath(string root, string value, string description)
    {
        var candidate = Path.GetFullPath(value, root);
        if (!File.Exists(candidate) || !ProjectDiscovery.IsSafelyContained(root, candidate))
        {
            throw new InvalidOperationException($"{description} must be a regular file inside the simulation snapshot.");
        }

        return ToPortablePath(root, candidate);
    }

    private static string ToPortablePath(string root, string value) =>
        Path.GetRelativePath(root, value).Replace(Path.DirectorySeparatorChar, '/');

    private static SourceDocument ReadSourceDocument(string sourceFile)
    {
        byte[] bytes;
        using (var stream = new FileStream(
                   sourceFile,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   bufferSize: 64 * 1024,
                   FileOptions.SequentialScan))
        {
            if (stream.Length == 0 || stream.Length > MaximumMutationXmlBytes)
            {
                throw new InvalidDataException(
                    $"The package declaration XML must be between 1 and {MaximumMutationXmlBytes} bytes.");
            }

            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
        }

        var format = DetectTextFormat(bytes);
        string text;
        try
        {
            text = format.Encoding.GetString(bytes, format.PreambleLength, bytes.Length - format.PreambleLength);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The package declaration is not valid UTF-8 or UTF-16 XML.", exception);
        }

        return new SourceDocument(
            text,
            format.Encoding,
            bytes.AsSpan(0, format.PreambleLength).ToArray(),
            ComputeSha256(bytes));
    }

    private static TextFormat DetectTextFormat(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return new TextFormat(new UTF8Encoding(false, true), 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return new TextFormat(new UnicodeEncoding(false, false, true), 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return new TextFormat(new UnicodeEncoding(true, false, true), 2);
        }

        if (LooksLikeBomlessUtf16(bytes, littleEndian: true))
        {
            return new TextFormat(new UnicodeEncoding(false, false, true), 0);
        }

        if (LooksLikeBomlessUtf16(bytes, littleEndian: false))
        {
            return new TextFormat(new UnicodeEncoding(true, false, true), 0);
        }

        return new TextFormat(new UTF8Encoding(false, true), 0);
    }

    private static bool LooksLikeBomlessUtf16(ReadOnlySpan<byte> bytes, bool littleEndian)
    {
        var limit = Math.Min(bytes.Length - bytes.Length % 2, 256);
        for (var index = 0; index < limit; index += 2)
        {
            var character = littleEndian ? bytes[index] : bytes[index + 1];
            var zero = littleEndian ? bytes[index + 1] : bytes[index];
            if (zero != 0)
            {
                return false;
            }

            if (character is 0x20 or 0x09 or 0x0A or 0x0D)
            {
                continue;
            }

            return character == 0x3C;
        }

        return false;
    }

    private static XDocument LoadSafeXml(string text, Encoding actualEncoding)
    {
        try
        {
            using var textReader = new StringReader(text);
            using var reader = XmlReader.Create(
                textReader,
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaximumMutationXmlBytes,
                });
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            if (document.Root is null || !document.Root.Name.LocalName.Equals("Project", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The package declaration file is not an MSBuild Project XML document.");
            }

            if (document.Declaration?.Encoding is { } declaredEncoding &&
                !IsSupportedXmlEncoding(declaredEncoding))
            {
                throw new InvalidDataException(
                    $"The package declaration uses unsupported XML encoding '{declaredEncoding}'.");
            }

            if (document.Declaration?.Encoding is { } encoding &&
                !IsCompatibleXmlEncoding(encoding, actualEncoding.WebName))
            {
                throw new InvalidDataException(
                    $"The XML declaration encoding '{encoding}' does not match the package declaration bytes.");
            }

            return document;
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("The package declaration file is not valid safe XML.", exception);
        }
    }

    private static bool IsSupportedXmlEncoding(string value) =>
        value.Equals("utf-8", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("utf8", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("utf-16", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("utf-16le", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("utf-16be", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("unicode", StringComparison.OrdinalIgnoreCase);

    private static bool IsCompatibleXmlEncoding(string declared, string actual) =>
        declared.ToLowerInvariant() switch
        {
            "utf-8" or "utf8" => actual.Equals("utf-8", StringComparison.OrdinalIgnoreCase),
            "utf-16" => actual.StartsWith("utf-16", StringComparison.OrdinalIgnoreCase),
            "utf-16le" or "unicode" => actual.Equals("utf-16", StringComparison.OrdinalIgnoreCase),
            "utf-16be" => actual.Equals("utf-16BE", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    private static IEnumerable<XElement> FindPackageItems(XDocument document, string packageId) =>
        document.Descendants()
            .Where(element => element.Name.LocalName is "PackageVersion" or "PackageReference")
            .Where(element =>
            {
                var include = element.Attribute("Include")?.Value;
                var update = element.Attribute("Update")?.Value;
                return include is not null && update is null && include.Equals(packageId, StringComparison.OrdinalIgnoreCase) ||
                       update is not null && include is null && update.Equals(packageId, StringComparison.OrdinalIgnoreCase);
            });

    private static bool HasCondition(XElement item)
    {
        for (XElement? current = item; current is not null; current = current.Parent)
        {
            if (current.Attributes().Any(attribute =>
                    attribute.Name.LocalName.Equals("Condition", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(attribute.Value)))
            {
                return true;
            }
        }

        return item.Elements().Any(child =>
            child.Attributes().Any(attribute =>
                attribute.Name.LocalName.Equals("Condition", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(attribute.Value)));
    }

    private static bool HasLiteralPackageId(LexicalElement item, string packageId)
    {
        var include = item.Attributes.SingleOrDefault(attribute => attribute.LocalName == "Include");
        var update = item.Attributes.SingleOrDefault(attribute => attribute.LocalName == "Update");
        return include is not null && update is null && include.Value.Equals(packageId, StringComparison.OrdinalIgnoreCase) ||
               update is not null && include is null && update.Value.Equals(packageId, StringComparison.OrdinalIgnoreCase);
    }

    private static TextRange FindSingleVersionLocation(
        LexicalElement item,
        string metadataName,
        string packageId,
        string text)
    {
        var attributes = item.Attributes
            .Where(candidate => candidate.LocalName.Equals(metadataName, StringComparison.Ordinal))
            .ToArray();
        var children = item.Children
            .Where(candidate => candidate.LocalName.Equals(metadataName, StringComparison.Ordinal))
            .ToArray();
        if (attributes.Length + children.Length != 1)
        {
            throw new InvalidOperationException(
                $"Package '{packageId}' must declare exactly one literal {metadataName} value.");
        }

        if (attributes.Length == 1)
        {
            return new TextRange(attributes[0].ValueStart, attributes[0].ValueEnd);
        }

        var child = children[0];
        if (child.SelfClosing || child.ContentEnd < child.ContentStart)
        {
            throw new InvalidOperationException(
                $"Package '{packageId}' must declare one non-empty literal {metadataName} value.");
        }

        var rawValue = text[child.ContentStart..child.ContentEnd];
        if (rawValue.Contains('<', StringComparison.Ordinal) || rawValue.Contains('&', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Package '{packageId}' uses non-literal {metadataName} XML content, which cannot be edited byte-locally.");
        }

        return new TextRange(child.ContentStart, child.ContentEnd);
    }

    private static void ValidateExpectedHash(string? expected, string observed)
    {
        if (expected is null)
        {
            return;
        }

        if (expected.Length != 64 || expected.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException("ExpectedSourceSha256 must contain exactly 64 hexadecimal characters.", nameof(expected));
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expected),
                Convert.FromHexString(observed)))
        {
            throw new InvalidOperationException(
                "The package declaration no longer matches the SHA-256 observed by the baseline analysis.");
        }
    }

    private static void EnsureSourceHashUnchanged(string sourceFile, string expectedHash)
    {
        var current = ReadSourceDocument(sourceFile).Sha256;
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(current),
                Convert.FromHexString(expectedHash)))
        {
            throw new InvalidOperationException(
                "The package declaration changed while the simulation was preparing its isolated edit.");
        }
    }

    private static void WriteAtomically(
        string snapshotRoot,
        string sourceFile,
        string expectedHash,
        ReadOnlySpan<byte> content)
    {
        if (!ProjectDiscovery.IsSafelyContained(snapshotRoot, sourceFile) ||
            IsReparsePoint(sourceFile))
        {
            throw new InvalidOperationException("The package declaration changed while the simulation was preparing its isolated edit.");
        }

        var directory = Path.GetDirectoryName(sourceFile)!;
        var temporaryFile = Path.Combine(directory, $".packagemedic-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryFile,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            EnsureSourceHashUnchanged(sourceFile, expectedHash);
            if (!ProjectDiscovery.IsSafelyContained(snapshotRoot, sourceFile) || IsReparsePoint(sourceFile))
            {
                throw new InvalidOperationException(
                    "The package declaration filesystem boundary changed before the isolated edit could be committed.");
            }

            File.Move(temporaryFile, sourceFile, overwrite: true);
            EnsureSourceHashUnchanged(sourceFile, ComputeSha256(content));
        }
        finally
        {
            if (File.Exists(temporaryFile))
            {
                File.Delete(temporaryFile);
            }
        }
    }

    private static string ComputeSha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Could not verify the simulation snapshot filesystem boundary.", exception);
        }
    }

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparer PortablePathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record InventoryDeclaration(
        string SourceFile,
        string PortableSourceFile,
        int SourceLine,
        PackageVersionDeclarationKind Kind,
        string VersionSource,
        string RequestedVersion);

    private sealed record ComparablePackageVersion(long[] Core, IReadOnlyList<string> Prerelease);

    private sealed record TextFormat(Encoding Encoding, int PreambleLength);

    private sealed record TextRange(int Start, int End);

    private sealed record SourceDocument(
        string Text,
        Encoding Encoding,
        byte[] Preamble,
        string Sha256)
    {
        public byte[] Encode(string value)
        {
            var payload = Encoding.GetBytes(value);
            if (Preamble.Length == 0)
            {
                return payload;
            }

            var result = new byte[checked(Preamble.Length + payload.Length)];
            Preamble.CopyTo(result, 0);
            payload.CopyTo(result, Preamble.Length);
            return result;
        }
    }

    private sealed record LexicalAttribute(
        string LocalName,
        string Value,
        int ValueStart,
        int ValueEnd);

    private sealed class LexicalElement(
        string localName,
        int startLine,
        IReadOnlyList<LexicalAttribute> attributes,
        int contentStart,
        bool selfClosing)
    {
        public string LocalName { get; } = localName;

        public int StartLine { get; } = startLine;

        public IReadOnlyList<LexicalAttribute> Attributes { get; } = attributes;

        public int ContentStart { get; } = contentStart;

        public int ContentEnd { get; set; } = selfClosing ? contentStart : -1;

        public bool SelfClosing { get; } = selfClosing;

        public List<LexicalElement> Children { get; } = [];
    }

    private static class XmlLexicalScanner
    {
        public static IReadOnlyList<LexicalElement> Scan(string text)
        {
            var result = new List<LexicalElement>();
            var openElements = new Stack<int>();
            var index = 0;
            var line = 1;
            while (index < text.Length)
            {
                if (text[index] != '<')
                {
                    Advance(text, ref index, index + 1, ref line);
                    continue;
                }

                var elementLine = line;
                if (StartsWith(text, index, "<!--"))
                {
                    SkipDelimited(text, ref index, "-->", ref line);
                    continue;
                }

                if (StartsWith(text, index, "<![CDATA["))
                {
                    SkipDelimited(text, ref index, "]]>", ref line);
                    continue;
                }

                if (StartsWith(text, index, "<?"))
                {
                    SkipDelimited(text, ref index, "?>", ref line);
                    continue;
                }

                if (StartsWith(text, index, "<!DOCTYPE", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("DTD declarations are prohibited in package declaration XML.");
                }

                if (StartsWith(text, index, "</"))
                {
                    var endStart = index;
                    Advance(text, ref index, index + 2, ref line);
                    SkipWhitespace(text, ref index, ref line);
                    var name = ReadName(text, ref index);
                    SkipWhitespace(text, ref index, ref line);
                    if (index >= text.Length || text[index] != '>')
                    {
                        throw new InvalidDataException("The package declaration contains a malformed XML end tag.");
                    }

                    Advance(text, ref index, index + 1, ref line);
                    if (openElements.Count == 0)
                    {
                        throw new InvalidDataException("The package declaration contains an unmatched XML end tag.");
                    }

                    var opened = result[openElements.Pop()];
                    if (!opened.LocalName.Equals(LocalName(name), StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("The package declaration contains mismatched XML tags.");
                    }

                    opened.ContentEnd = endStart;
                    continue;
                }

                if (StartsWith(text, index, "<!"))
                {
                    throw new InvalidDataException("Unsupported XML markup was found in the package declaration.");
                }

                Advance(text, ref index, index + 1, ref line);
                var elementName = ReadName(text, ref index);
                var attributes = new List<LexicalAttribute>();
                var selfClosing = false;
                while (true)
                {
                    SkipWhitespace(text, ref index, ref line);
                    if (index >= text.Length)
                    {
                        throw new InvalidDataException("The package declaration contains an unterminated XML start tag.");
                    }

                    if (text[index] == '>')
                    {
                        Advance(text, ref index, index + 1, ref line);
                        break;
                    }

                    if (text[index] == '/')
                    {
                        Advance(text, ref index, index + 1, ref line);
                        if (index >= text.Length || text[index] != '>')
                        {
                            throw new InvalidDataException("The package declaration contains malformed self-closing XML.");
                        }

                        Advance(text, ref index, index + 1, ref line);
                        selfClosing = true;
                        break;
                    }

                    var attributeName = ReadName(text, ref index);
                    SkipWhitespace(text, ref index, ref line);
                    if (index >= text.Length || text[index] != '=')
                    {
                        throw new InvalidDataException("The package declaration contains malformed XML metadata.");
                    }

                    Advance(text, ref index, index + 1, ref line);
                    SkipWhitespace(text, ref index, ref line);
                    if (index >= text.Length || text[index] is not ('\'' or '"'))
                    {
                        throw new InvalidDataException("The package declaration contains an unquoted XML attribute.");
                    }

                    var quote = text[index];
                    Advance(text, ref index, index + 1, ref line);
                    var valueStart = index;
                    while (index < text.Length && text[index] != quote)
                    {
                        Advance(text, ref index, index + 1, ref line);
                    }

                    if (index >= text.Length)
                    {
                        throw new InvalidDataException("The package declaration contains an unterminated XML attribute.");
                    }

                    var valueEnd = index;
                    attributes.Add(new LexicalAttribute(
                        LocalName(attributeName),
                        text[valueStart..valueEnd],
                        valueStart,
                        valueEnd));
                    Advance(text, ref index, index + 1, ref line);
                }

                var element = new LexicalElement(
                    LocalName(elementName),
                    elementLine,
                    attributes,
                    index,
                    selfClosing);
                var elementIndex = result.Count;
                result.Add(element);
                if (openElements.TryPeek(out var parentIndex))
                {
                    result[parentIndex].Children.Add(element);
                }

                if (!selfClosing)
                {
                    openElements.Push(elementIndex);
                }
            }

            if (openElements.Count != 0)
            {
                throw new InvalidDataException("The package declaration contains an unterminated XML element.");
            }

            return result;
        }

        private static string ReadName(string text, ref int index)
        {
            var start = index;
            while (index < text.Length &&
                   !char.IsWhiteSpace(text[index]) &&
                   text[index] is not ('/' or '>' or '=' or '\'' or '"'))
            {
                index++;
            }

            if (index == start)
            {
                throw new InvalidDataException("The package declaration contains an invalid XML name.");
            }

            return text[start..index];
        }

        private static string LocalName(string name)
        {
            var separator = name.LastIndexOf(':');
            return separator < 0 ? name : name[(separator + 1)..];
        }

        private static void SkipWhitespace(string text, ref int index, ref int line)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                Advance(text, ref index, index + 1, ref line);
            }
        }

        private static void SkipDelimited(
            string text,
            ref int index,
            string terminator,
            ref int line)
        {
            var end = text.IndexOf(terminator, index, StringComparison.Ordinal);
            if (end < 0)
            {
                throw new InvalidDataException("The package declaration contains unterminated XML markup.");
            }

            Advance(text, ref index, checked(end + terminator.Length), ref line);
        }

        private static void Advance(string text, ref int index, int end, ref int line)
        {
            while (index < end)
            {
                var character = text[index++];
                if (character == '\n' ||
                    character == '\r' && (index >= text.Length || text[index] != '\n'))
                {
                    line++;
                }
            }
        }

        private static bool StartsWith(
            string text,
            int index,
            string value,
            StringComparison comparison = StringComparison.Ordinal) =>
            index <= text.Length - value.Length &&
            text.AsSpan(index, value.Length).Equals(value.AsSpan(), comparison);
    }
}
