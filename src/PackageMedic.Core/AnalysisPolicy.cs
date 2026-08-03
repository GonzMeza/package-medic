using System.Text;
using System.Text.RegularExpressions;

namespace PackageMedic.Core;

public sealed record PolicyTimeouts(TimeSpan Restore, TimeSpan Evaluation)
{
    public static PolicyTimeouts Default { get; } = new(
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(1));
}

public sealed record AnalysisPolicyOverrides(
    PolicyFailureLevel? FailOn = null,
    PolicyFailureLevel? FailOnNew = null,
    string? Baseline = null,
    int? RestoreTimeoutSeconds = null,
    int? EvaluationTimeoutSeconds = null);

public sealed record SuppressedDiagnostic(Diagnostic Diagnostic, PolicySuppression Suppression);

public sealed record PolicyApplication(
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<SuppressedDiagnostic> SuppressedDiagnostics,
    IReadOnlyList<Diagnostic> ExcludedDiagnostics,
    IReadOnlyList<Diagnostic> DisabledDiagnostics);

public sealed class AnalysisPolicy
{
    private readonly IReadOnlyList<Regex> excludeMatchers;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<PreparedSuppression>> suppressionsByRule;

    internal AnalysisPolicy(
        PolicyFailureLevel failOn,
        PolicyFailureLevel? failOnNew,
        string? baselinePath,
        IReadOnlyList<string> exclude,
        IReadOnlyDictionary<string, PolicyRule> rules,
        IReadOnlyList<PolicySuppression> suppressions,
        PolicyTimeouts timeouts)
    {
        FailOn = failOn;
        FailOnNew = failOnNew;
        BaselinePath = baselinePath;
        Exclude = exclude;
        Rules = rules;
        Suppressions = suppressions;
        Timeouts = timeouts;
        excludeMatchers = exclude.Select(GlobMatcher.Create).ToArray();
        suppressionsByRule = suppressions
            .Select(item => new PreparedSuppression(
                item,
                item.Path is null ? null : GlobMatcher.Create(item.Path)))
            .GroupBy(item => item.Suppression.Rule, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<PreparedSuppression>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    public PolicyFailureLevel FailOn { get; }

    public PolicyFailureLevel? FailOnNew { get; }

    public string? BaselinePath { get; }

    public IReadOnlyList<string> Exclude { get; }

    public IReadOnlyDictionary<string, PolicyRule> Rules { get; }

    public IReadOnlyList<PolicySuppression> Suppressions { get; }

    public PolicyTimeouts Timeouts { get; }

    public PolicyApplication Apply(IReadOnlyList<Diagnostic> diagnostics, string targetRoot)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);

        var accepted = new List<Diagnostic>(diagnostics.Count);
        var suppressed = new List<SuppressedDiagnostic>();
        var excluded = new List<Diagnostic>();
        var disabled = new List<Diagnostic>();

        foreach (var diagnostic in diagnostics)
        {
            var rule = Rules.GetValueOrDefault(diagnostic.Code);
            if (rule is { Enabled: false })
            {
                disabled.Add(diagnostic);
                continue;
            }

            if (IsExcluded(diagnostic.File, targetRoot) || IsExcluded(diagnostic.Project, targetRoot))
            {
                excluded.Add(diagnostic);
                continue;
            }

            var effectiveDiagnostic = rule?.Severity is { } severity && diagnostic.Severity != severity
                ? diagnostic with { Severity = severity }
                : diagnostic;
            var suppression = suppressionsByRule.TryGetValue(effectiveDiagnostic.Code, out var candidates)
                ? candidates.FirstOrDefault(item => Matches(item, effectiveDiagnostic, targetRoot))
                : null;
            if (suppression is not null)
            {
                suppressed.Add(new SuppressedDiagnostic(effectiveDiagnostic, suppression.Suppression));
                continue;
            }

            accepted.Add(effectiveDiagnostic);
        }

        return new PolicyApplication(accepted, suppressed, excluded, disabled);
    }

    public bool IsExcluded(string? path, string targetRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalizedPath = NormalizePath(path, targetRoot);
        return excludeMatchers.Any(matcher => matcher.IsMatch(normalizedPath));
    }

    private static bool Matches(PreparedSuppression prepared, Diagnostic diagnostic, string targetRoot)
    {
        var suppression = prepared.Suppression;
        if (!suppression.Rule.Equals(diagnostic.Code, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (suppression.Path is not null)
        {
            var file = NormalizePath(diagnostic.File ?? diagnostic.Project ?? string.Empty, targetRoot);
            if (!prepared.PathMatcher!.IsMatch(file))
            {
                return false;
            }
        }

        return suppression.Package is null || ContainsPackageIdentifier(diagnostic, suppression.Package);
    }

    private static bool ContainsPackageIdentifier(Diagnostic diagnostic, string package)
    {
        var searchable = string.Join(
            '\n',
            diagnostic.Title,
            diagnostic.Explanation,
            diagnostic.Evidence,
            diagnostic.SuggestedAction);
        var start = 0;
        while ((start = searchable.IndexOf(package, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var beforeIsBoundary = start == 0 || !IsPackageCharacter(searchable[start - 1]);
            var after = start + package.Length;
            var afterIsBoundary = after == searchable.Length || !IsPackageCharacter(searchable[after]);
            if (beforeIsBoundary && afterIsBoundary)
            {
                return true;
            }

            start++;
        }

        return false;
    }

    private static bool IsPackageCharacter(char value) => char.IsAsciiLetterOrDigit(value) || value is '.' or '-' or '_';

    private static string NormalizePath(string path, string targetRoot)
    {
        var normalized = path;
        if (Path.IsPathRooted(normalized))
        {
            try
            {
                normalized = Path.GetRelativePath(Path.GetFullPath(targetRoot), Path.GetFullPath(normalized));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Keep the original path; matching remains read-only and deterministic.
            }
        }

        return TrimCurrentDirectoryPrefix(normalized.Replace('\\', '/'));
    }

    private static string TrimCurrentDirectoryPrefix(string path)
    {
        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        return path.StartsWith("/", StringComparison.Ordinal) ? path[1..] : path;
    }

    private static class GlobMatcher
    {
        private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

        public static Regex Create(string pattern)
        {
            var normalizedPattern = TrimCurrentDirectoryPrefix(pattern.Replace('\\', '/'));
            if (!normalizedPattern.Contains('/', StringComparison.Ordinal))
            {
                normalizedPattern = $"**/{normalizedPattern}";
            }

            return new Regex(
                ToRegex(normalizedPattern),
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking,
                MatchTimeout);
        }

        private static string ToRegex(string pattern)
        {
            var expression = new StringBuilder("^");
            for (var index = 0; index < pattern.Length; index++)
            {
                var current = pattern[index];
                if (current == '*' && index + 1 < pattern.Length && pattern[index + 1] == '*')
                {
                    index++;
                    if (index + 1 < pattern.Length && pattern[index + 1] == '/')
                    {
                        index++;
                        expression.Append("(?:.*/)?");
                    }
                    else
                    {
                        expression.Append(".*");
                    }
                }
                else if (current == '*')
                {
                    expression.Append("[^/]*");
                }
                else if (current == '?')
                {
                    expression.Append("[^/]");
                }
                else
                {
                    expression.Append(Regex.Escape(current.ToString()));
                }
            }

            return expression.Append('$').ToString();
        }
    }

    private sealed record PreparedSuppression(PolicySuppression Suppression, Regex? PathMatcher);
}

public static class AnalysisPolicyResolver
{
    public static AnalysisPolicy Resolve(
        PackageMedicConfiguration configuration,
        string configurationDirectory,
        AnalysisPolicyOverrides? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationDirectory);
        overrides ??= new AnalysisPolicyOverrides();

        ValidateOverrideTimeout(overrides.RestoreTimeoutSeconds, nameof(overrides.RestoreTimeoutSeconds));
        ValidateOverrideTimeout(overrides.EvaluationTimeoutSeconds, nameof(overrides.EvaluationTimeoutSeconds));

        var baseline = overrides.Baseline ?? configuration.Baseline;
        var baselinePath = baseline is null
            ? null
            : Path.GetFullPath(baseline, Path.GetFullPath(configurationDirectory));
        var restoreSeconds = overrides.RestoreTimeoutSeconds
            ?? configuration.Timeouts.RestoreSeconds
            ?? (int)PolicyTimeouts.Default.Restore.TotalSeconds;
        var evaluationSeconds = overrides.EvaluationTimeoutSeconds
            ?? configuration.Timeouts.EvaluationSeconds
            ?? (int)PolicyTimeouts.Default.Evaluation.TotalSeconds;

        return new AnalysisPolicy(
            overrides.FailOn ?? configuration.FailOn ?? PolicyFailureLevel.Warning,
            overrides.FailOnNew ?? configuration.FailOnNew,
            baselinePath,
            configuration.Exclude,
            configuration.Rules,
            configuration.Suppressions,
            new PolicyTimeouts(TimeSpan.FromSeconds(restoreSeconds), TimeSpan.FromSeconds(evaluationSeconds)));
    }

    private static void ValidateOverrideTimeout(int? seconds, string property)
    {
        if (seconds is < 1 or > 3600)
        {
            throw new ArgumentOutOfRangeException(property, seconds, "Timeout must be between 1 and 3600 seconds.");
        }
    }
}
