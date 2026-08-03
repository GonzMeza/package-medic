using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PackageMedic.Core;

public sealed record DiagnosticIdentity(string Fingerprint, string? RelativePath);

/// <summary>
/// Creates the stable, repository-portable identity used by SARIF and PackageMedic baselines.
/// </summary>
public static partial class DiagnosticFingerprint
{
    public const string Algorithm = "packageMedicDiagnostic/v1";

    public static DiagnosticIdentity Create(Diagnostic diagnostic, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        var root = RepositoryRoot.Parse(repositoryRoot);
        return Create(diagnostic, root);
    }

    public static string Compute(Diagnostic diagnostic, string repositoryRoot) =>
        Create(diagnostic, repositoryRoot).Fingerprint;

    public static string? GetRelativePath(string? file, string repositoryRoot) =>
        RepositoryRoot.TryGetRelativeUri(file, RepositoryRoot.Parse(repositoryRoot));

    internal static DiagnosticIdentity Create(Diagnostic diagnostic, RepositoryRoot root)
    {
        var relativePath = RepositoryRoot.TryGetRelativeUri(diagnostic.File, root);
        var fingerprintInput = string.Join(
            "\n",
            diagnostic.Code,
            diagnostic.OriginalCode ?? string.Empty,
            relativePath ?? string.Empty,
            SanitizeText(diagnostic.Evidence, root));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput)))
            .ToLowerInvariant();

        return new DiagnosticIdentity(fingerprint, relativePath);
    }

    internal static string SanitizeText(string value, RepositoryRoot root)
    {
        var redacted = ProcessRunner.RedactSecrets(value);
        if (root.Normalized != "/")
        {
            redacted = redacted.Replace(root.Original, "%SRCROOT%", root.Comparison);
            if (!root.Original.Equals(root.Normalized, StringComparison.Ordinal))
            {
                redacted = redacted.Replace(root.Normalized, "%SRCROOT%", root.Comparison);
            }
        }

        redacted = redacted.Replace('\\', '/');
        redacted = WindowsAbsolutePathRegex().Replace(redacted, "[ABSOLUTE_PATH]");
        return UnixAbsolutePathRegex().Replace(redacted, "[ABSOLUTE_PATH]");
    }

    [GeneratedRegex("(?<![:A-Za-z0-9])(?:[A-Za-z]:/|//[^/\\s]+/[^/\\s]+/)[^\\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsAbsolutePathRegex();

    [GeneratedRegex("(?<![:/%A-Za-z0-9])/(?:[^/\\s]+/)+[^/\\s]*", RegexOptions.CultureInvariant)]
    private static partial Regex UnixAbsolutePathRegex();
}

internal sealed record RepositoryRoot(
    string Original,
    string Normalized,
    bool IsWindows,
    StringComparison Comparison)
{
    public static RepositoryRoot Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var original = value.Trim();
        var normalized = NormalizeSeparators(original);
        var isWindows = IsWindowsAbsolute(normalized);
        if (!isWindows && !IsUnixAbsolute(normalized))
        {
            throw new ArgumentException("The repository root must be an absolute Windows or Unix path.", nameof(value));
        }

        var isFileSystemRoot = normalized == "/" ||
                               (isWindows && normalized.Length == 3 && normalized[1] == ':' && normalized[2] == '/');
        if (!isFileSystemRoot)
        {
            original = original.TrimEnd('/', '\\');
            normalized = normalized.TrimEnd('/');
        }

        return new RepositoryRoot(
            original,
            normalized,
            isWindows,
            isWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public static string? TryGetRelativeUri(string? file, RepositoryRoot root)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return null;
        }

        var normalizedFile = NormalizeSeparators(file.Trim());
        string relative;
        if (IsWindowsAbsolute(normalizedFile) || IsUnixAbsolute(normalizedFile))
        {
            var fileIsWindows = IsWindowsAbsolute(normalizedFile);
            var rootEndsWithSeparator = root.Normalized.EndsWith("/", StringComparison.Ordinal);
            if (fileIsWindows != root.IsWindows ||
                !normalizedFile.StartsWith(root.Normalized, root.Comparison) ||
                (!rootEndsWithSeparator && normalizedFile.Length > root.Normalized.Length &&
                 normalizedFile[root.Normalized.Length] != '/'))
            {
                return null;
            }

            relative = normalizedFile[root.Normalized.Length..].TrimStart('/');
        }
        else
        {
            relative = normalizedFile.TrimStart('/');
        }

        var segments = new List<string>();
        foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    return null;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            return null;
        }

        return string.Join('/', segments.Select(Uri.EscapeDataString));
    }

    private static string NormalizeSeparators(string value) => value.Replace('\\', '/');

    private static bool IsWindowsAbsolute(string value) =>
        (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '/') ||
        value.StartsWith("//", StringComparison.Ordinal);

    private static bool IsUnixAbsolute(string value) => value.StartsWith("/", StringComparison.Ordinal);
}
