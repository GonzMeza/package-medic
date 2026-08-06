using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PackageMedic.Core;

/// <summary>
/// Serializes the resolved NuGet dependency information retained by an
/// <see cref="AnalysisResult"/> as a deterministic CycloneDX 1.7 JSON BOM.
/// </summary>
/// <remarks>
/// <para>
/// PackageMedic currently retains canonical dependency paths rather than every
/// edge from project.assets.json. The generated composition is therefore
/// deliberately marked incomplete and must not be represented as a complete
/// application, operating-system, native, or runtime SBOM.
/// Projects without package inventory also cannot be given a framework/RID
/// context by the current <see cref="AnalysisResult"/> contract and are omitted.
/// </para>
/// <para>
/// The repository root is used only to produce portable project identities. It
/// is never written to the BOM. Only credential-free HTTPS/local sources,
/// portable declaration files, valid SHA-512 package hashes, and unambiguous
/// signature evidence are retained; unsafe values and restore errors are excluded.
/// </para>
/// </remarks>
public static class CycloneDxSbomSerializer
{
    public const string BomFormat = "CycloneDX";
    public const string SpecVersion = "1.7";
    public const string SchemaUri = "http://cyclonedx.org/schema/bom-1.7.schema.json";
    public const int MaximumOutputBytes = 16 * 1024 * 1024;

    private const string AnalysisScope = "resolved-nuget-dependencies";
    private const string CompletenessReason =
        "Canonical resolved NuGet dependency paths only; alternate dependency edges, non-NuGet, native, build-time, operating-system, and runtime components are outside this BOM.";
    private const int MaximumIdentityLength = 4096;

    public static string Serialize(AnalysisResult result, string repositoryRoot)
        => Serialize(result, repositoryRoot, MaximumOutputBytes);

    internal static string Serialize(
        AnalysisResult result,
        string repositoryRoot,
        long maximumOutputBytes)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateMaximumOutputBytes(maximumOutputBytes);
        var model = CreateModel(result, repositoryRoot);
        using var buffer = new MemoryStream();
        using (var bounded = new SizeLimitedWriteStream(buffer, maximumOutputBytes))
        using (var writer = new Utf8JsonWriter(bounded, new JsonWriterOptions { Indented = true }))
        {
            Write(writer, result, model);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static async Task SerializeAsync(
        Stream destination,
        AnalysisResult result,
        string repositoryRoot,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(
            destination,
            result,
            repositoryRoot,
            MaximumOutputBytes,
            cancellationToken).ConfigureAwait(false);

    internal static async Task SerializeAsync(
        Stream destination,
        AnalysisResult result,
        string repositoryRoot,
        long maximumOutputBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(result);
        ValidateMaximumOutputBytes(maximumOutputBytes);
        var model = CreateModel(result, repositoryRoot);
        using var bounded = new SizeLimitedWriteStream(destination, maximumOutputBytes, leaveOpen: true);
        using var writer = new Utf8JsonWriter(bounded, new JsonWriterOptions { Indented = true });
        Write(writer, result, model);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateMaximumOutputBytes(long maximumOutputBytes)
    {
        if (maximumOutputBytes < 1 || maximumOutputBytes > MaximumOutputBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOutputBytes));
        }
    }

    private static SbomModel CreateModel(AnalysisResult result, string repositoryRoot)
    {
        var root = RepositoryRoot.Parse(repositoryRoot);
        var toolVersion = NormalizeRequired(result.Version, "PackageMedic version");
        var rows = result.Packages
            .Select(item => CreatePackageRow(item, root))
            .OrderBy(item => item.Context.Project, StringComparer.Ordinal)
            .ThenBy(item => item.Context.Framework, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Context.RuntimeIdentifier, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Package.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Package.Id, StringComparer.Ordinal)
            .ThenBy(item => item.Package.ResolvedVersion, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Package.ResolvedVersion, StringComparer.Ordinal)
            .ThenBy(item => item.Package.DependencyKind)
            .ToArray();

        var contexts = rows
            .GroupBy(item => ContextLookupKey(item.Context), StringComparer.Ordinal)
            .Select(group => CreateContext(group.Select(item => item).ToArray()))
            .OrderBy(item => item.Project, StringComparer.Ordinal)
            .ThenBy(item => item.Framework, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Framework, StringComparer.Ordinal)
            .ThenBy(item => item.RuntimeIdentifier, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.RuntimeIdentifier, StringComparer.Ordinal)
            .ToArray();

        var contextsByKey = contexts.ToDictionary(ContextLookupKey, StringComparer.Ordinal);
        AddCanonicalPathEdges(result.DependencyPaths, root, contextsByKey);

        var rootIdentity = string.Join(
            '\n',
            contexts.Select(item => ContextIdentity(item.Project, item.Framework, item.RuntimeIdentifier)));
        var rootReference = CreateReference("root", rootIdentity.Length == 0 ? "empty" : rootIdentity);
        return new SbomModel(rootReference, toolVersion, contexts);
    }

    private static PackageRow CreatePackageRow(PackageInventoryItem package, RepositoryRoot root)
    {
        var project = RepositoryRoot.TryGetRelativeUri(package.Project, root) ??
            throw new InvalidDataException(
                "A package project is outside the repository root; CycloneDX export refused to emit a machine-specific path.");
        var framework = NormalizeRequired(package.Framework, "target framework");
        var runtimeIdentifier = NormalizeOptional(package.RuntimeIdentifier);
        var id = NormalizeRequired(package.Id, "package identifier");
        var version = NormalizeRequired(package.ResolvedVersion, "resolved package version");
        return new PackageRow(
            new ContextIdentityModel(project, framework, runtimeIdentifier),
            package with
            {
                Project = project,
                Framework = framework,
                RuntimeIdentifier = runtimeIdentifier,
                Id = id,
                ResolvedVersion = version,
                SourceFile = package.SourceFile is null
                    ? null
                    : RepositoryRoot.TryGetRelativeUri(package.SourceFile, root),
                PackageSource = NormalizeCredentialFreeSource(package.PackageSource),
                ContentHash = NormalizeSha512(package.ContentHash),
            });
    }

    private static ContextModel CreateContext(IReadOnlyList<PackageRow> rows)
    {
        var representative = rows[0].Context;
        var contextIdentity = ContextIdentity(
            representative.Project,
            representative.Framework,
            representative.RuntimeIdentifier);
        var context = new ContextModel(
            representative.Project,
            representative.Framework,
            representative.RuntimeIdentifier,
            CreateReference("project", contextIdentity));

        foreach (var group in rows.GroupBy(
                     item => PackageLookupKey(item.Package.Id, item.Package.ResolvedVersion),
                     StringComparer.Ordinal))
        {
            var ordered = group
                .OrderBy(item => item.Package.Id, StringComparer.Ordinal)
                .ThenBy(item => item.Package.ResolvedVersion, StringComparer.Ordinal)
                .ToArray();
            var first = ordered[0].Package;
            var kind = ordered.Any(item => item.Package.DependencyKind == PackageDependencyKind.Direct)
                ? PackageDependencyKind.Direct
                : PackageDependencyKind.Transitive;
            var packages = ordered.Select(item => item.Package).ToArray();
            var package = new PackageModel(
                first.Id,
                first.ResolvedVersion,
                kind,
                CreateReference(
                    "package",
                    $"{contextIdentity}\n{PackageIdentity(first.Id, first.ResolvedVersion)}"),
                CreatePackageUrl(first.Id, first.ResolvedVersion),
                Consensus(packages.Select(item => item.VersionSource)),
                Consensus(packages.Select(item => item.SourceFile)),
                Consensus(packages.Select(item => item.PackageSource)),
                Consensus(packages.Select(item => item.ContentHash)),
                Consensus(packages.Select(item => item.SignaturePresent)));
            context.Packages.Add(PackageLookupKey(first.Id, first.ResolvedVersion), package);
            if (kind == PackageDependencyKind.Direct)
            {
                context.DependsOn.Add(package.Reference);
            }
        }

        return context;
    }

    private static void AddCanonicalPathEdges(
        IReadOnlyList<PackageDependencyPath> paths,
        RepositoryRoot root,
        IReadOnlyDictionary<string, ContextModel> contexts)
    {
        foreach (var path in paths
                     .OrderBy(item => item.Project, StringComparer.Ordinal)
                     .ThenBy(item => item.Framework, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.RuntimeIdentifier, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.PackageId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.ResolvedVersion, StringComparer.OrdinalIgnoreCase))
        {
            var project = RepositoryRoot.TryGetRelativeUri(path.Project, root);
            if (project is null)
            {
                continue;
            }

            var framework = NormalizeRequired(path.Framework, "dependency-path target framework");
            var runtimeIdentifier = NormalizeOptional(path.RuntimeIdentifier);
            var lookup = ContextLookupKey(new ContextIdentityModel(project, framework, runtimeIdentifier));
            if (!contexts.TryGetValue(lookup, out var context))
            {
                continue;
            }

            for (var index = 1; index < path.Path.Count; index++)
            {
                var parentSegment = path.Path[index - 1];
                var childSegment = path.Path[index];
                if (!context.Packages.TryGetValue(
                        PackageLookupKey(parentSegment.PackageId, parentSegment.ResolvedVersion),
                        out var parent) ||
                    !context.Packages.TryGetValue(
                        PackageLookupKey(childSegment.PackageId, childSegment.ResolvedVersion),
                        out var child))
                {
                    continue;
                }

                parent.DependsOn.Add(child.Reference);
            }
        }
    }

    private static void Write(Utf8JsonWriter writer, AnalysisResult result, SbomModel model)
    {
        writer.WriteStartObject();
        writer.WriteString("$schema", SchemaUri);
        writer.WriteString("bomFormat", BomFormat);
        writer.WriteString("specVersion", SpecVersion);
        writer.WriteNumber("version", 1);

        writer.WriteStartObject("metadata");
        writer.WriteStartObject("tools");
        writer.WriteStartArray("components");
        writer.WriteStartObject();
        writer.WriteString("type", "application");
        writer.WriteString("name", "PackageMedic");
        writer.WriteString("version", model.ToolVersion);
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteStartObject("component");
        writer.WriteString("type", "application");
        writer.WriteString("bom-ref", model.RootReference);
        writer.WriteString("name", "PackageMedic analysis target");
        writer.WriteEndObject();
        writer.WriteStartArray("properties");
        WriteProperty(writer, "packagemedic:analysis-error-count", result.AnalysisErrors.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        WriteProperty(writer, "packagemedic:completeness", "incomplete");
        WriteProperty(writer, "packagemedic:completeness-reason", CompletenessReason);
        WriteProperty(writer, "packagemedic:scope", AnalysisScope);
        writer.WriteEndArray();
        writer.WriteEndObject();

        writer.WriteStartArray("components");
        foreach (var context in model.Contexts)
        {
            WriteProjectComponent(writer, context);
        }

        foreach (var context in model.Contexts)
        {
            foreach (var package in OrderedPackages(context))
            {
                WritePackageComponent(writer, context, package);
            }
        }

        writer.WriteEndArray();

        writer.WriteStartArray("dependencies");
        WriteDependency(writer, model.RootReference, model.Contexts.Select(item => item.Reference));
        foreach (var context in model.Contexts)
        {
            WriteDependency(writer, context.Reference, context.DependsOn);
        }

        foreach (var context in model.Contexts)
        {
            foreach (var package in OrderedPackages(context))
            {
                WriteDependency(writer, package.Reference, package.DependsOn);
            }
        }

        writer.WriteEndArray();

        writer.WriteStartArray("compositions");
        writer.WriteStartObject();
        writer.WriteString("aggregate", "incomplete");
        writer.WriteStartArray("dependencies");
        var compositionReferences = new[] { model.RootReference }
            .Concat(model.Contexts.Select(item => item.Reference))
            .Concat(model.Contexts.SelectMany(item => item.Packages.Values.Select(package => package.Reference)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        foreach (var reference in compositionReferences)
        {
            writer.WriteStringValue(reference);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteProjectComponent(Utf8JsonWriter writer, ContextModel context)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "application");
        writer.WriteString("bom-ref", context.Reference);
        writer.WriteString("name", context.Project);
        writer.WriteStartArray("properties");
        WriteProperty(writer, "packagemedic:framework", context.Framework);
        WriteProperty(writer, "packagemedic:project", context.Project);
        if (context.RuntimeIdentifier is not null)
        {
            WriteProperty(writer, "packagemedic:runtime-identifier", context.RuntimeIdentifier);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WritePackageComponent(
        Utf8JsonWriter writer,
        ContextModel context,
        PackageModel package)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "library");
        writer.WriteString("bom-ref", package.Reference);
        writer.WriteString("name", package.Id);
        writer.WriteString("version", package.Version);
        writer.WriteString("purl", package.PackageUrl);
        if (package.Sha512 is not null)
        {
            writer.WriteStartArray("hashes");
            writer.WriteStartObject();
            writer.WriteString("alg", "SHA-512");
            writer.WriteString("content", package.Sha512);
            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        writer.WriteStartArray("properties");
        WriteProperty(
            writer,
            "packagemedic:dependency-kind",
            package.DependencyKind == PackageDependencyKind.Direct ? "direct" : "transitive");
        WriteProperty(writer, "packagemedic:framework", context.Framework);
        WriteProperty(writer, "packagemedic:project", context.Project);
        if (package.VersionSource is not null)
        {
            WriteProperty(writer, "packagemedic:version-source", package.VersionSource);
        }

        if (package.DeclarationFile is not null)
        {
            WriteProperty(writer, "packagemedic:declaration-file", package.DeclarationFile);
        }

        if (package.PackageSource is not null)
        {
            WriteProperty(writer, "packagemedic:package-source", package.PackageSource);
        }

        if (package.SignaturePresent is { } signaturePresent)
        {
            WriteProperty(
                writer,
                "packagemedic:signature-present",
                signaturePresent ? "true" : "false");
        }

        if (context.RuntimeIdentifier is not null)
        {
            WriteProperty(writer, "packagemedic:runtime-identifier", context.RuntimeIdentifier);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteDependency(Utf8JsonWriter writer, string reference, IEnumerable<string> dependsOn)
    {
        writer.WriteStartObject();
        writer.WriteString("ref", reference);
        writer.WriteStartArray("dependsOn");
        foreach (var dependency in dependsOn.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            writer.WriteStringValue(dependency);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteProperty(Utf8JsonWriter writer, string name, string value)
    {
        writer.WriteStartObject();
        writer.WriteString("name", name);
        writer.WriteString("value", value);
        writer.WriteEndObject();
    }

    private static IEnumerable<PackageModel> OrderedPackages(ContextModel context) => context.Packages.Values
        .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.Id, StringComparer.Ordinal)
        .ThenBy(item => item.Version, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.Version, StringComparer.Ordinal);

    private static string CreatePackageUrl(string packageId, string version) =>
        $"pkg:nuget/{EscapePackageUrlSegment(packageId)}@{EscapePackageUrlSegment(version)}";

    private static string EscapePackageUrlSegment(string value)
    {
        try
        {
            return Uri.EscapeDataString(value);
        }
        catch (UriFormatException exception)
        {
            throw new InvalidDataException("A NuGet package identity cannot be encoded as a Package URL.", exception);
        }
    }

    private static string CreateReference(string kind, string identity)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}\n{identity}")))
            .ToLowerInvariant();
        return $"urn:packagemedic:{kind}:{digest}";
    }

    private static string ContextLookupKey(ContextIdentityModel context) =>
        ContextLookupKey(context.Project, context.Framework, context.RuntimeIdentifier);

    private static string ContextLookupKey(ContextModel context) =>
        ContextLookupKey(context.Project, context.Framework, context.RuntimeIdentifier);

    private static string ContextLookupKey(string project, string framework, string? runtimeIdentifier) =>
        $"{project}\n{framework.ToUpperInvariant()}\n{runtimeIdentifier?.ToUpperInvariant()}";

    private static string ContextIdentity(string project, string framework, string? runtimeIdentifier) =>
        $"{project}\n{framework}\n{runtimeIdentifier}";

    private static string PackageLookupKey(string packageId, string version) =>
        $"{packageId.ToUpperInvariant()}\n{version.ToUpperInvariant()}";

    private static string PackageIdentity(string packageId, string version) =>
        $"{packageId}\n{version}";

    private static string? Consensus(IEnumerable<string?> values)
    {
        var all = values.Select(value => string.IsNullOrWhiteSpace(value) ? null : value.Trim()).ToArray();
        if (all.Length == 0 || all.Any(value => value is null))
        {
            return null;
        }

        var observed = all
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        return observed.Length == 1 ? observed[0] : null;
    }

    private static bool? Consensus(IEnumerable<bool?> values)
    {
        var all = values.ToArray();
        if (all.Length == 0 || all.Any(value => value is null))
        {
            return null;
        }

        var observed = all.Select(value => value!.Value).Distinct().Take(2).ToArray();
        return observed.Length == 1 ? observed[0] : null;
    }

    private static string? NormalizeCredentialFreeSource(string? value)
    {
        var source = value?.Trim();
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        if (source.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            return "local";
        }

        return Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
               uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
               string.IsNullOrEmpty(uri.UserInfo) &&
               string.IsNullOrEmpty(uri.Query) &&
               string.IsNullOrEmpty(uri.Fragment)
            ? uri.AbsoluteUri.TrimEnd('/')
            : null;
    }

    private static string? NormalizeSha512(string? value)
    {
        var hash = value?.Trim();
        if (string.IsNullOrWhiteSpace(hash))
        {
            return null;
        }

        if (hash.StartsWith("sha512-", StringComparison.OrdinalIgnoreCase))
        {
            hash = hash[7..];
        }

        if (hash.Length == 128 && hash.All(Uri.IsHexDigit))
        {
            return hash.ToLowerInvariant();
        }

        try
        {
            var bytes = Convert.FromBase64String(hash);
            return bytes.Length == 64 ? Convert.ToHexString(bytes).ToLowerInvariant() : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string NormalizeRequired(string value, string description)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidDataException($"CycloneDX export requires a non-empty {description}.");
        }

        ValidateIdentityLength(normalized, description);
        return normalized;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        ValidateIdentityLength(normalized, "runtime identifier");
        return normalized;
    }

    private static void ValidateIdentityLength(string value, string description)
    {
        if (value.Length > MaximumIdentityLength)
        {
            throw new InvalidDataException(
                $"CycloneDX export cannot include a {description} longer than {MaximumIdentityLength} characters.");
        }
    }

    private sealed record PackageRow(ContextIdentityModel Context, PackageInventoryItem Package);

    private sealed record ContextIdentityModel(
        string Project,
        string Framework,
        string? RuntimeIdentifier);

    private sealed class ContextModel(
        string project,
        string framework,
        string? runtimeIdentifier,
        string reference)
    {
        public string Project { get; } = project;

        public string Framework { get; } = framework;

        public string? RuntimeIdentifier { get; } = runtimeIdentifier;

        public string Reference { get; } = reference;

        public Dictionary<string, PackageModel> Packages { get; } = new(StringComparer.Ordinal);

        public SortedSet<string> DependsOn { get; } = new(StringComparer.Ordinal);
    }

    private sealed class PackageModel(
        string id,
        string version,
        PackageDependencyKind dependencyKind,
        string reference,
        string packageUrl,
        string? versionSource,
        string? declarationFile,
        string? packageSource,
        string? sha512,
        bool? signaturePresent)
    {
        public string Id { get; } = id;

        public string Version { get; } = version;

        public PackageDependencyKind DependencyKind { get; } = dependencyKind;

        public string Reference { get; } = reference;

        public string PackageUrl { get; } = packageUrl;

        public string? VersionSource { get; } = versionSource;

        public string? DeclarationFile { get; } = declarationFile;

        public string? PackageSource { get; } = packageSource;

        public string? Sha512 { get; } = sha512;

        public bool? SignaturePresent { get; } = signaturePresent;

        public SortedSet<string> DependsOn { get; } = new(StringComparer.Ordinal);
    }

    private sealed record SbomModel(
        string RootReference,
        string ToolVersion,
        IReadOnlyList<ContextModel> Contexts);

    private sealed class SizeLimitedWriteStream(
        Stream destination,
        long maximumBytes,
        bool leaveOpen = false) : Stream
    {
        private long written;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => destination.CanWrite;

        public override long Length => written;

        public override long Position
        {
            get => written;
            set => throw new NotSupportedException();
        }

        public override void Flush() => destination.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            destination.FlushAsync(cancellationToken);

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacity(count);
            destination.Write(buffer, offset, count);
            written += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            destination.Write(buffer);
            written += buffer.Length;
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            EnsureCapacity(buffer.Length);
            await destination.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            written += buffer.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen)
            {
                destination.Dispose();
            }

            base.Dispose(disposing);
        }

        private void EnsureCapacity(int count)
        {
            if (count < 0 || written > maximumBytes - count)
            {
                throw new InvalidDataException(
                    $"CycloneDX output exceeds the {maximumBytes}-byte safety limit.");
            }
        }
    }
}
