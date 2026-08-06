using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PackageMedic.Core;

/// <summary>
/// Represents an immutable resource referenced by an in-toto Statement.
/// </summary>
/// <remarks>
/// Only Git commit identifiers and SHA-256 digests are supported deliberately.
/// PackageMedic emits unsigned Statements; signing and trust establishment belong
/// to the caller.
/// </remarks>
public sealed class InTotoResourceDescriptor
{
    internal const int MaximumNameLength = 256;

    private InTotoResourceDescriptor(string name, string digestAlgorithm, string digest)
    {
        Name = EvidenceValueValidator.ValidatePortableName(name, nameof(name));
        DigestAlgorithm = digestAlgorithm;
        Digest = digest;
    }

    public string Name { get; }

    public string DigestAlgorithm { get; }

    public string Digest { get; }

    public static InTotoResourceDescriptor FromGitCommit(string name, string gitCommit)
    {
        var digest = EvidenceValueValidator.ValidateHexDigest(
            gitCommit,
            nameof(gitCommit),
            40,
            64);
        return new InTotoResourceDescriptor(name, "gitCommit", digest);
    }

    public static InTotoResourceDescriptor FromSha256(string name, string sha256)
    {
        var digest = EvidenceValueValidator.ValidateHexDigest(sha256, nameof(sha256), 64);
        return new InTotoResourceDescriptor(name, "sha256", digest);
    }
}

public enum InTotoTestResult
{
    Passed,
    Warned,
    Failed,
}

public enum PackageMedicAnalysisCompleteness
{
    Complete,
    Incomplete,
}

public enum PackageMedicConfigurationFingerprintState
{
    None,
    Sha256,
}

/// <summary>
/// Identifies the trusted PackageMedic configuration used for an analysis,
/// without embedding configuration contents or a machine path.
/// </summary>
public sealed class PackageMedicConfigurationFingerprint
{
    private PackageMedicConfigurationFingerprint(
        PackageMedicConfigurationFingerprintState state,
        string? sha256)
    {
        State = state;
        Sha256 = sha256;
    }

    public PackageMedicConfigurationFingerprintState State { get; }

    public string? Sha256 { get; }

    public static PackageMedicConfigurationFingerprint None { get; } = new(
        PackageMedicConfigurationFingerprintState.None,
        null);

    public static PackageMedicConfigurationFingerprint FromSha256(string sha256) => new(
        PackageMedicConfigurationFingerprintState.Sha256,
        EvidenceValueValidator.ValidateHexDigest(sha256, nameof(sha256), 64));
}

/// <summary>
/// Portable facts emitted by one PackageMedic analysis. This is a
/// PackageMedic-specific evidence predicate, not SLSA provenance.
/// </summary>
public sealed record PackageMedicAnalysisEvidence(
    string Target,
    string BaselineGitCommit,
    string ComparisonSha256,
    string ToolVersion,
    VerificationLevel VerificationLevel,
    VerificationVerdict VerificationStatus,
    PackageMedicAnalysisCompleteness Completeness,
    PackageMedicConfigurationFingerprint ConfigurationFingerprint)
{
    public string? SbomSha256 { get; init; }
}

/// <summary>
/// Structured, portable evidence for the in-toto Test Result v0.1 predicate.
/// </summary>
public sealed record InTotoTestResultEvidence(
    InTotoTestResult Result,
    IReadOnlyList<InTotoResourceDescriptor> Configuration,
    IReadOnlyList<string> PassedTests,
    IReadOnlyList<string> WarnedTests,
    IReadOnlyList<string> FailedTests);

/// <summary>
/// An evidence payload whose bytes will be represented by a SHA-256 digest in
/// an evidence manifest. Content is never embedded in the manifest.
/// </summary>
public sealed class EvidenceArtifact
{
    private EvidenceArtifact(string name, ReadOnlyMemory<byte> content)
    {
        Name = EvidenceValueValidator.ValidatePortableName(name, nameof(name));
        Content = content;
    }

    public string Name { get; }

    public ReadOnlyMemory<byte> Content { get; }

    public static EvidenceArtifact FromBytes(string name, ReadOnlyMemory<byte> content) => new(name, content);
}

/// <summary>
/// Writes deterministic, unsigned in-toto Statement v1 documents and a
/// deterministic SHA-256 evidence manifest.
/// </summary>
/// <remarks>
/// This serializer does not produce a DSSE envelope, a signature, or SLSA
/// provenance. Its output only becomes an attestation after a separate trusted
/// system authenticates it.
/// </remarks>
public static class InTotoEvidenceSerializer
{
    public const string StatementType = "https://in-toto.io/Statement/v1";
    public const string CycloneDxPredicateType = "https://cyclonedx.org/bom";
    public const string TestResultPredicateType = "https://in-toto.io/attestation/test-result/v0.1";
    public const string PackageMedicAnalysisPredicateType =
        "https://gonzmeza.github.io/package-medic/attestation/analysis/v1";

    public const int EvidenceManifestSchemaVersion = 1;
    public const int PackageMedicAnalysisSchemaVersion = 1;
    public const int MaximumSubjects = 64;
    public const int MaximumConfigurations = 64;
    public const int MaximumTestNamesPerResult = 100_000;
    public const int MaximumTestNameLength = 512;
    public const int MaximumTestResultTextBytes = 16 * 1024 * 1024;
    public const int MaximumCycloneDxBytes = 16 * 1024 * 1024;
    public const int MaximumManifestArtifacts = 4_096;
    public const int MaximumArtifactBytes = 256 * 1024 * 1024;
    public const long MaximumManifestContentBytes = 1024L * 1024L * 1024L;

    private const int MaximumJsonDepth = 64;
    private const int MaximumAnalysisTargetLength = 512;
    private const int MaximumToolVersionLength = 128;

    public static string SerializeCycloneDxStatement(
        IReadOnlyCollection<InTotoResourceDescriptor> subjects,
        ReadOnlyMemory<byte> cycloneDxBom)
    {
        if (cycloneDxBom.IsEmpty)
        {
            throw new ArgumentException("A CycloneDX predicate cannot be empty.", nameof(cycloneDxBom));
        }

        if (cycloneDxBom.Length > MaximumCycloneDxBytes)
        {
            throw new ArgumentException(
                $"A CycloneDX predicate cannot exceed {MaximumCycloneDxBytes} bytes.",
                nameof(cycloneDxBom));
        }

        using var document = ParseCycloneDx(cycloneDxBom);
        return SerializeStatement(subjects, CycloneDxPredicateType, writer =>
            WriteCanonicalJson(writer, document.RootElement));
    }

    public static string SerializeTestResultStatement(
        IReadOnlyCollection<InTotoResourceDescriptor> subjects,
        InTotoTestResultEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!Enum.IsDefined(evidence.Result))
        {
            throw new ArgumentOutOfRangeException(nameof(evidence), evidence.Result, "Unknown test result.");
        }

        var configurations = ValidateAndOrderDescriptors(
            evidence.Configuration,
            nameof(evidence.Configuration),
            MaximumConfigurations);
        if (configurations.Count == 0)
        {
            throw new ArgumentException(
                "Test Result evidence requires at least one immutable configuration descriptor.",
                nameof(evidence));
        }

        var passed = ValidateAndOrderTestNames(evidence.PassedTests, nameof(evidence.PassedTests));
        var warned = ValidateAndOrderTestNames(evidence.WarnedTests, nameof(evidence.WarnedTests));
        var failed = ValidateAndOrderTestNames(evidence.FailedTests, nameof(evidence.FailedTests));
        ValidateTotalTestEvidenceSize(passed, warned, failed);
        EnsureTestSetsDoNotOverlap(passed, warned, failed);
        ValidateTestResultConsistency(evidence.Result, passed, warned, failed);

        return SerializeStatement(subjects, TestResultPredicateType, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("result", evidence.Result switch
            {
                InTotoTestResult.Passed => "PASSED",
                InTotoTestResult.Warned => "WARNED",
                InTotoTestResult.Failed => "FAILED",
                _ => throw new UnreachableException(),
            });
            writer.WriteStartArray("configuration");
            foreach (var configuration in configurations)
            {
                WriteResourceDescriptor(writer, configuration);
            }

            writer.WriteEndArray();
            WriteStrings(writer, "passedTests", passed);
            WriteStrings(writer, "warnedTests", warned);
            WriteStrings(writer, "failedTests", failed);
            writer.WriteEndObject();
        });
    }

    /// <summary>
    /// Serializes portable PackageMedic analysis evidence as an unsigned
    /// in-toto Statement v1 bound to an immutable Git commit.
    /// </summary>
    public static string SerializePackageMedicAnalysisStatement(
        string gitCommit,
        PackageMedicAnalysisEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var subject = InTotoResourceDescriptor.FromGitCommit("repository", gitCommit);
        var target = EvidenceValueValidator.ValidatePortableTarget(
            evidence.Target,
            nameof(evidence.Target),
            MaximumAnalysisTargetLength);
        var toolVersion = EvidenceValueValidator.ValidateSemanticVersion(
            evidence.ToolVersion,
            nameof(evidence.ToolVersion),
            MaximumToolVersionLength);
        var baselineGitCommit = EvidenceValueValidator.ValidateHexDigest(
            evidence.BaselineGitCommit,
            nameof(evidence.BaselineGitCommit),
            40,
            64);
        var comparisonSha256 = EvidenceValueValidator.ValidateHexDigest(
            evidence.ComparisonSha256,
            nameof(evidence.ComparisonSha256),
            64);
        ArgumentNullException.ThrowIfNull(
            evidence.ConfigurationFingerprint,
            nameof(evidence.ConfigurationFingerprint));
        if (!Enum.IsDefined(evidence.VerificationLevel))
        {
            throw new ArgumentOutOfRangeException(
                nameof(evidence),
                evidence.VerificationLevel,
                "Unknown verification level.");
        }

        if (!Enum.IsDefined(evidence.VerificationStatus))
        {
            throw new ArgumentOutOfRangeException(
                nameof(evidence),
                evidence.VerificationStatus,
                "Unknown verification status.");
        }

        if (!Enum.IsDefined(evidence.Completeness))
        {
            throw new ArgumentOutOfRangeException(
                nameof(evidence),
                evidence.Completeness,
                "Unknown analysis completeness.");
        }

        ValidateCompleteness(evidence.VerificationStatus, evidence.Completeness);
        var sbomSha256 = evidence.SbomSha256 is null
            ? null
            : EvidenceValueValidator.ValidateHexDigest(
                evidence.SbomSha256,
                nameof(evidence.SbomSha256),
                64);

        return SerializeStatement([subject], PackageMedicAnalysisPredicateType, writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", PackageMedicAnalysisSchemaVersion);
            writer.WriteString("target", target);
            writer.WriteStartObject("tool");
            writer.WriteString("name", "PackageMedic");
            writer.WriteString("version", toolVersion);
            writer.WriteEndObject();
            writer.WriteStartObject("configuration");
            writer.WriteString(
                "state",
                evidence.ConfigurationFingerprint.State switch
                {
                    PackageMedicConfigurationFingerprintState.None => "none",
                    PackageMedicConfigurationFingerprintState.Sha256 => "sha256",
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(evidence),
                        evidence.ConfigurationFingerprint.State,
                        "Unknown configuration fingerprint state."),
                });
            if (evidence.ConfigurationFingerprint.State == PackageMedicConfigurationFingerprintState.Sha256)
            {
                writer.WriteString("sha256", evidence.ConfigurationFingerprint.Sha256);
            }

            writer.WriteEndObject();
            writer.WriteStartObject("comparison");
            writer.WriteString("baselineGitCommit", baselineGitCommit);
            writer.WriteString("sha256", comparisonSha256);
            writer.WriteEndObject();
            writer.WriteStartObject("verification");
            writer.WriteString("level", VerificationLevelValue(evidence.VerificationLevel));
            writer.WriteString("status", VerificationStatusValue(evidence.VerificationStatus));
            writer.WriteString(
                "completeness",
                evidence.Completeness == PackageMedicAnalysisCompleteness.Complete
                    ? "complete"
                    : "incomplete");
            writer.WriteEndObject();
            if (sbomSha256 is not null)
            {
                writer.WriteStartObject("sbom");
                writer.WriteString("algorithm", "sha256");
                writer.WriteString("digest", sbomSha256);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        });
    }

    public static byte[] SerializePackageMedicAnalysisStatementUtf8(
        string gitCommit,
        PackageMedicAnalysisEvidence evidence) => Encoding.UTF8.GetBytes(
        SerializePackageMedicAnalysisStatement(gitCommit, evidence));

    public static string SerializeEvidenceManifest(IReadOnlyCollection<EvidenceArtifact> artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        if (artifacts.Count == 0)
        {
            throw new ArgumentException("An evidence manifest requires at least one artifact.", nameof(artifacts));
        }

        if (artifacts.Count > MaximumManifestArtifacts)
        {
            throw new ArgumentException(
                $"An evidence manifest cannot contain more than {MaximumManifestArtifacts} artifacts.",
                nameof(artifacts));
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var totalBytes = 0L;
        var entries = new List<ManifestEntry>(artifacts.Count);
        foreach (var artifact in artifacts)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            if (!names.Add(artifact.Name))
            {
                throw new ArgumentException(
                    $"Evidence artifact name '{artifact.Name}' is duplicated.",
                    nameof(artifacts));
            }

            if (artifact.Content.Length > MaximumArtifactBytes)
            {
                throw new ArgumentException(
                    $"Evidence artifact '{artifact.Name}' exceeds {MaximumArtifactBytes} bytes.",
                    nameof(artifacts));
            }

            totalBytes = checked(totalBytes + artifact.Content.Length);
            if (totalBytes > MaximumManifestContentBytes)
            {
                throw new ArgumentException(
                    $"Evidence manifest content cannot exceed {MaximumManifestContentBytes} bytes.",
                    nameof(artifacts));
            }

            var digest = Convert.ToHexString(SHA256.HashData(artifact.Content.Span)).ToLowerInvariant();
            entries.Add(new ManifestEntry(artifact.Name, artifact.Content.Length, digest));
        }

        entries.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        return WriteDocument(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", EvidenceManifestSchemaVersion);
            writer.WriteString("algorithm", "sha256");
            writer.WriteStartArray("artifacts");
            foreach (var entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("name", entry.Name);
                writer.WriteNumber("size", entry.Size);
                writer.WriteString("sha256", entry.Sha256);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private static JsonDocument ParseCycloneDx(ReadOnlyMemory<byte> cycloneDxBom)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(cycloneDxBom, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumJsonDepth,
            });
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The CycloneDX predicate must be valid JSON.", nameof(cycloneDxBom), exception);
        }

        try
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("The CycloneDX predicate must be a JSON object.", nameof(cycloneDxBom));
            }

            if (!root.TryGetProperty("bomFormat", out var format) ||
                format.ValueKind != JsonValueKind.String ||
                !string.Equals(format.GetString(), "CycloneDX", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The CycloneDX predicate must declare bomFormat 'CycloneDX'.",
                    nameof(cycloneDxBom));
            }

            ValidateJsonEvidence(root, "$", 0);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static string SerializeStatement(
        IReadOnlyCollection<InTotoResourceDescriptor> subjects,
        string predicateType,
        Action<Utf8JsonWriter> writePredicate)
    {
        var orderedSubjects = ValidateAndOrderDescriptors(subjects, nameof(subjects), MaximumSubjects);
        if (orderedSubjects.Count == 0)
        {
            throw new ArgumentException("An in-toto Statement requires at least one immutable subject.", nameof(subjects));
        }

        return WriteDocument(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("_type", StatementType);
            writer.WriteStartArray("subject");
            foreach (var subject in orderedSubjects)
            {
                WriteResourceDescriptor(writer, subject);
            }

            writer.WriteEndArray();
            writer.WriteString("predicateType", predicateType);
            writer.WritePropertyName("predicate");
            writePredicate(writer);
            writer.WriteEndObject();
        });
    }

    private static List<InTotoResourceDescriptor> ValidateAndOrderDescriptors(
        IReadOnlyCollection<InTotoResourceDescriptor> descriptors,
        string parameterName,
        int maximumCount)
    {
        ArgumentNullException.ThrowIfNull(descriptors, parameterName);
        if (descriptors.Count > maximumCount)
        {
            throw new ArgumentException(
                $"'{parameterName}' cannot contain more than {maximumCount} descriptors.",
                parameterName);
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<InTotoResourceDescriptor>(descriptors.Count);
        foreach (var descriptor in descriptors)
        {
            ArgumentNullException.ThrowIfNull(descriptor, parameterName);
            if (!names.Add(descriptor.Name))
            {
                throw new ArgumentException(
                    $"Resource descriptor name '{descriptor.Name}' is duplicated.",
                    parameterName);
            }

            ordered.Add(descriptor);
        }

        ordered.Sort(static (left, right) =>
        {
            var comparison = StringComparer.Ordinal.Compare(left.Name, right.Name);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(left.DigestAlgorithm, right.DigestAlgorithm);
            return comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(left.Digest, right.Digest);
        });
        return ordered;
    }

    private static List<string> ValidateAndOrderTestNames(
        IReadOnlyList<string> testNames,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(testNames, parameterName);
        if (testNames.Count > MaximumTestNamesPerResult)
        {
            throw new ArgumentException(
                $"'{parameterName}' cannot contain more than {MaximumTestNamesPerResult} test names.",
                parameterName);
        }

        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var testName in testNames)
        {
            var validated = EvidenceValueValidator.ValidateTestName(testName, parameterName);
            if (!unique.Add(validated))
            {
                throw new ArgumentException($"Test name '{validated}' is duplicated.", parameterName);
            }
        }

        return unique.Order(StringComparer.Ordinal).ToList();
    }

    private static void EnsureTestSetsDoNotOverlap(
        IReadOnlyCollection<string> passed,
        IReadOnlyCollection<string> warned,
        IReadOnlyCollection<string> failed)
    {
        var classifications = new HashSet<string>(passed, StringComparer.Ordinal);
        foreach (var item in warned.Concat(failed))
        {
            if (!classifications.Add(item))
            {
                throw new ArgumentException(
                    $"Test name '{item}' appears in more than one result classification.",
                    nameof(InTotoTestResultEvidence));
            }
        }
    }

    private static void ValidateTotalTestEvidenceSize(params IReadOnlyCollection<string>[] classifications)
    {
        var totalCount = 0L;
        var totalTextBytes = 0L;
        foreach (var classification in classifications)
        {
            totalCount = checked(totalCount + classification.Count);
            foreach (var testName in classification)
            {
                totalTextBytes = checked(totalTextBytes + Encoding.UTF8.GetByteCount(testName));
            }
        }

        if (totalCount > MaximumTestNamesPerResult)
        {
            throw new ArgumentException(
                $"Test Result evidence cannot contain more than {MaximumTestNamesPerResult} classified tests.",
                nameof(InTotoTestResultEvidence));
        }

        if (totalTextBytes > MaximumTestResultTextBytes)
        {
            throw new ArgumentException(
                $"Test Result names cannot exceed {MaximumTestResultTextBytes} UTF-8 bytes in total.",
                nameof(InTotoTestResultEvidence));
        }
    }

    private static void ValidateTestResultConsistency(
        InTotoTestResult result,
        IReadOnlyCollection<string> passed,
        IReadOnlyCollection<string> warned,
        IReadOnlyCollection<string> failed)
    {
        if (result == InTotoTestResult.Passed &&
            (passed.Count == 0 || warned.Count != 0 || failed.Count != 0))
        {
            throw new ArgumentException(
                "A PASSED Test Result requires a passed test and cannot contain warned or failed tests.",
                nameof(InTotoTestResultEvidence));
        }

        if (result == InTotoTestResult.Warned && (warned.Count == 0 || failed.Count != 0))
        {
            throw new ArgumentException(
                "A WARNED Test Result requires a warned test and cannot contain failed tests.",
                nameof(InTotoTestResultEvidence));
        }

        if (result == InTotoTestResult.Failed && failed.Count == 0)
        {
            throw new ArgumentException(
                "A FAILED Test Result requires at least one failed test.",
                nameof(InTotoTestResultEvidence));
        }
    }

    private static void ValidateCompleteness(
        VerificationVerdict verificationStatus,
        PackageMedicAnalysisCompleteness completeness)
    {
        var expected = verificationStatus == VerificationVerdict.Incomplete
            ? PackageMedicAnalysisCompleteness.Incomplete
            : PackageMedicAnalysisCompleteness.Complete;
        if (completeness != expected)
        {
            throw new ArgumentException(
                $"Verification status '{VerificationStatusValue(verificationStatus)}' requires " +
                $"'{expected.ToString().ToLowerInvariant()}' analysis evidence.",
                nameof(PackageMedicAnalysisEvidence));
        }
    }

    private static string VerificationLevelValue(VerificationLevel level) => level switch
    {
        VerificationLevel.Restore => "restore",
        VerificationLevel.Build => "build",
        VerificationLevel.Test => "test",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown verification level."),
    };

    private static string VerificationStatusValue(VerificationVerdict status) => status switch
    {
        VerificationVerdict.Pass => "pass",
        VerificationVerdict.Reject => "reject",
        VerificationVerdict.NoChange => "noChange",
        VerificationVerdict.Incomplete => "incomplete",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown verification status."),
    };

    private static void WriteResourceDescriptor(Utf8JsonWriter writer, InTotoResourceDescriptor descriptor)
    {
        writer.WriteStartObject();
        writer.WriteString("name", descriptor.Name);
        writer.WriteStartObject("digest");
        writer.WriteString(descriptor.DigestAlgorithm, descriptor.Digest);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteStrings(Utf8JsonWriter writer, string propertyName, IEnumerable<string> values)
    {
        writer.WriteStartArray(propertyName);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static string WriteDocument(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            SkipValidation = false,
        }))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;

            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: false);
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;

            default:
                throw new ArgumentException("Unsupported JSON value in CycloneDX predicate.");
        }
    }

    private static void ValidateJsonEvidence(JsonElement value, string location, int depth)
    {
        if (depth > MaximumJsonDepth)
        {
            throw new ArgumentException("The CycloneDX predicate exceeds the supported JSON depth.");
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var properties = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in value.EnumerateObject())
                    {
                        if (!properties.Add(property.Name))
                        {
                            throw new ArgumentException(
                                $"The CycloneDX predicate contains duplicate property '{property.Name}' at {location}.");
                        }

                        if (EvidenceValueValidator.IsSensitivePropertyName(property.Name) ||
                            string.Equals(property.Name, "timestamp", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new ArgumentException(
                                $"The CycloneDX predicate contains disallowed metadata property '{property.Name}'.");
                        }

                        ValidateJsonEvidence(property.Value, $"{location}.{property.Name}", depth + 1);
                    }

                    break;
                }

            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    ValidateJsonEvidence(item, location, depth + 1);
                }

                break;

            case JsonValueKind.String:
                EvidenceValueValidator.ValidateEvidenceString(value.GetString()!, location);
                break;
        }
    }

    private sealed record ManifestEntry(string Name, int Size, string Sha256);
}

internal static partial class EvidenceValueValidator
{
    private static readonly HashSet<string> SensitivePropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "accessToken",
        "apiKey",
        "authorization",
        "credential",
        "credentials",
        "password",
        "secret",
        "token",
    };

    internal static string ValidatePortableName(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > InTotoResourceDescriptor.MaximumNameLength)
        {
            throw new ArgumentException(
                $"A portable evidence name cannot exceed {InTotoResourceDescriptor.MaximumNameLength} characters.",
                parameterName);
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.IndexOfAny(['/', '\\']) >= 0 ||
            value.Contains("://", StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Evidence names must be portable identifiers, not paths, URLs, or control-character data.",
                parameterName);
        }

        ValidateCredentialText(value, parameterName);
        return value;
    }

    internal static string ValidateHexDigest(string value, string parameterName, params int[] permittedLengths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!permittedLengths.Contains(value.Length) || !HexRegex().IsMatch(value))
        {
            throw new ArgumentException(
                $"'{parameterName}' must be a hexadecimal digest of length {string.Join(" or ", permittedLengths)}.",
                parameterName);
        }

        return value.ToLowerInvariant();
    }

    internal static string ValidateTestName(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > InTotoEvidenceSerializer.MaximumTestNameLength)
        {
            throw new ArgumentException(
                $"A test name cannot exceed {InTotoEvidenceSerializer.MaximumTestNameLength} characters.",
                parameterName);
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) || value.Any(char.IsControl))
        {
            throw new ArgumentException("Test names cannot contain surrounding whitespace or control characters.", parameterName);
        }

        ValidateEvidenceString(value, parameterName);
        return value;
    }

    internal static string ValidatePortableTarget(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength)
        {
            throw new ArgumentException(
                $"A portable analysis target cannot exceed {maximumLength} characters.",
                parameterName);
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl) ||
            value.Contains('\\', StringComparison.Ordinal) ||
            value.Contains(':', StringComparison.Ordinal) ||
            value.Contains('?', StringComparison.Ordinal) ||
            value.Contains('#', StringComparison.Ordinal) ||
            value.Contains('%', StringComparison.Ordinal) ||
            value.StartsWith('/') ||
            value.StartsWith('~'))
        {
            throw new ArgumentException(
                "Analysis targets must be normalized portable relative paths without URLs, queries, or control characters.",
                parameterName);
        }

        if (!string.Equals(value, ".", StringComparison.Ordinal))
        {
            var segments = value.Split('/');
            if (segments.Any(segment =>
                    string.IsNullOrWhiteSpace(segment) ||
                    string.Equals(segment, ".", StringComparison.Ordinal) ||
                    string.Equals(segment, "..", StringComparison.Ordinal)))
            {
                throw new ArgumentException(
                    "Analysis targets cannot contain empty, current-directory, or parent-directory segments.",
                    parameterName);
            }

            foreach (var segment in segments)
            {
                ValidateCredentialText(segment, parameterName);
            }
        }

        ValidateCredentialText(value, parameterName);
        return value;
    }

    internal static string ValidateSemanticVersion(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength || !SemanticVersionRegex().IsMatch(value))
        {
            throw new ArgumentException(
                $"PackageMedic tool version must be a SemVer 2 value of at most {maximumLength} characters.",
                parameterName);
        }

        var buildMetadataStart = value.IndexOf('+');
        var prereleaseStart = value.IndexOf('-');
        if (prereleaseStart >= 0 &&
            (buildMetadataStart < 0 || prereleaseStart < buildMetadataStart))
        {
            var prereleaseLength = (buildMetadataStart < 0 ? value.Length : buildMetadataStart) -
                prereleaseStart - 1;
            var prerelease = value.Substring(prereleaseStart + 1, prereleaseLength);
            if (prerelease.Split('.').Any(identifier =>
                    identifier.Length > 1 &&
                    identifier[0] == '0' &&
                    identifier.All(char.IsAsciiDigit)))
            {
                throw new ArgumentException(
                    "PackageMedic tool version cannot contain a numeric prerelease identifier with leading zeroes.",
                    parameterName);
            }
        }

        ValidateCredentialText(value, parameterName);
        return value;
    }

    internal static void ValidateEvidenceString(string value, string location)
    {
        if (LooksLikeAbsolutePath(value))
        {
            throw new ArgumentException($"Evidence at '{location}' contains an absolute machine path.");
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException($"Evidence at '{location}' contains URI credentials.");
        }

        ValidateCredentialText(value, location);
    }

    internal static bool IsSensitivePropertyName(string name) => SensitivePropertyNames.Contains(name);

    private static bool LooksLikeAbsolutePath(string value) =>
        value.StartsWith("/", StringComparison.Ordinal) ||
        value.StartsWith("\\\\", StringComparison.Ordinal) ||
        (value.Length >= 3 &&
         char.IsAsciiLetter(value[0]) &&
         value[1] == ':' &&
         (value[2] == '\\' || value[2] == '/'));

    private static void ValidateCredentialText(string value, string location)
    {
        if (CredentialRegex().IsMatch(value))
        {
            throw new ArgumentException($"Evidence at '{location}' appears to contain credentials.");
        }
    }

    [GeneratedRegex("^[0-9a-fA-F]+$", RegexOptions.CultureInvariant)]
    private static partial Regex HexRegex();

    [GeneratedRegex(
        "(?:^|[?&;\\s])(?:access[_-]?token|api[_-]?key|authorization|password|secret|token)\\s*[:=]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialRegex();

    [GeneratedRegex(
        "^(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionRegex();
}
