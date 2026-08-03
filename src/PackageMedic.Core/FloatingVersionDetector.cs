namespace PackageMedic.Core;

internal static class FloatingVersionDetector
{
    public static bool IsFloating(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var candidate = version.Trim();
        if (!candidate.Contains('*', StringComparison.Ordinal) ||
            candidate.Any(char.IsWhiteSpace) ||
            ContainsUnevaluatedMsBuildExpression(candidate))
        {
            return false;
        }

        var prereleaseSeparator = candidate.IndexOf('-');
        var release = prereleaseSeparator < 0 ? candidate : candidate[..prereleaseSeparator];
        var prerelease = prereleaseSeparator < 0 ? null : candidate[(prereleaseSeparator + 1)..];

        return IsReleasePart(release) &&
               (prerelease is null || IsPrereleasePart(prerelease));
    }

    private static bool IsReleasePart(string release)
    {
        var components = release.Split('.');
        if (components.Length is < 1 or > 4)
        {
            return false;
        }

        var wildcardSeen = false;
        for (var index = 0; index < components.Length; index++)
        {
            var component = components[index];
            if (component == "*")
            {
                if (index != components.Length - 1)
                {
                    return false;
                }

                wildcardSeen = true;
                continue;
            }

            if (wildcardSeen || component.Length == 0 || !component.All(char.IsDigit))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPrereleasePart(string prerelease)
    {
        if (prerelease.Length == 0)
        {
            return false;
        }

        var identifiers = prerelease.Split('.');
        for (var index = 0; index < identifiers.Length; index++)
        {
            var identifier = identifiers[index];
            if (identifier == "*")
            {
                return index == identifiers.Length - 1;
            }

            if (identifier.Length == 0 || identifier.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            {
                return false;
            }
        }

        return false;
    }

    private static bool ContainsUnevaluatedMsBuildExpression(string value) =>
        value.Contains("$(", StringComparison.Ordinal) ||
        value.Contains("@(", StringComparison.Ordinal) ||
        value.Contains("%(", StringComparison.Ordinal);
}
