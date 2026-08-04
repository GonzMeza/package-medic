using System.Text.RegularExpressions;

namespace PackageMedic.Core;

public enum RestoreProcessFailureKind
{
    None,
    Rejected,
    TimedOut,
    OutputLimitExceeded,
}

public sealed record RestoreExecutionResult(
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<string> Errors,
    RestoreProcessFailureKind FailureKind)
{
    public bool Succeeded => FailureKind == RestoreProcessFailureKind.None && Errors.Count == 0;
}

public sealed partial class RestoreRunner
{
    private readonly IProcessRunner processRunner;
    private readonly TimeSpan timeout;
    private readonly int maxDegreeOfParallelism;

    public RestoreRunner(IProcessRunner processRunner)
        : this(
            processRunner,
            AnalysisExecutionOptions.Default.RestoreTimeout,
            AnalysisExecutionOptions.Default.MaxDegreeOfParallelism)
    {
    }

    public RestoreRunner(IProcessRunner processRunner, TimeSpan timeout)
        : this(processRunner, timeout, AnalysisExecutionOptions.Default.MaxDegreeOfParallelism)
    {
    }

    public RestoreRunner(IProcessRunner processRunner, TimeSpan timeout, int maxDegreeOfParallelism)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan || timeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The restore timeout must be greater than zero and no longer than one hour.");
        }

        if (maxDegreeOfParallelism is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism));
        }

        this.timeout = timeout;
        this.maxDegreeOfParallelism = maxDegreeOfParallelism;
    }

    public async Task<(IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<string> Errors)> RestoreAsync(
        DiscoveryResult discovery,
        Action<string>? progress,
        CancellationToken cancellationToken,
        bool forceEvaluate = false,
        string? packagesDirectory = null)
    {
        var result = await RestoreDetailedAsync(
            discovery,
            progress,
            cancellationToken,
            forceEvaluate,
            packagesDirectory).ConfigureAwait(false);
        return (result.Diagnostics, result.Errors);
    }

    public async Task<RestoreExecutionResult> RestoreDetailedAsync(
        DiscoveryResult discovery,
        Action<string>? progress,
        CancellationToken cancellationToken,
        bool forceEvaluate = false,
        string? packagesDirectory = null)
    {
        if (packagesDirectory is not null && !Path.IsPathFullyQualified(packagesDirectory))
        {
            throw new ArgumentException("The isolated packages directory must be absolute.", nameof(packagesDirectory));
        }

        var progressGate = new object();
        var targets = discovery.RestoreTargets
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var outcomes = new RestoreTargetOutcome?[targets.Length];
        var indexes = Enumerable.Range(0, targets.Length).ToArray();
        var solutionIndexes = indexes.Where(index => IsSolution(targets[index])).ToArray();
        var projectIndexes = indexes.Where(index => !IsSolution(targets[index])).ToArray();
        await RestoreBatchAsync(solutionIndexes, maximumParallelism: 1).ConfigureAwait(false);
        await RestoreBatchAsync(projectIndexes, maxDegreeOfParallelism).ConfigureAwait(false);
        return new RestoreExecutionResult(
            Deduplicate(outcomes.SelectMany(item => item!.Diagnostics)),
            outcomes.SelectMany(item => item!.Errors).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            AggregateFailure(outcomes.Select(item => item!.FailureKind)));

        async Task RestoreBatchAsync(IReadOnlyList<int> batch, int maximumParallelism)
        {
            await Parallel.ForEachAsync(
                batch,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = maximumParallelism,
                },
                async (index, token) =>
                {
                    outcomes[index] = await RestoreTargetAsync(
                        targets[index],
                        progress,
                        progressGate,
                        token,
                        forceEvaluate,
                        packagesDirectory).ConfigureAwait(false);
                }).ConfigureAwait(false);
        }
    }

    private static bool IsSolution(string path) =>
        Path.GetExtension(path).Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".slnx", StringComparison.OrdinalIgnoreCase);

    private async Task<RestoreTargetOutcome> RestoreTargetAsync(
        string target,
        Action<string>? progress,
        object progressGate,
        CancellationToken cancellationToken,
        bool forceEvaluate,
        string? packagesDirectory)
    {
        lock (progressGate)
        {
            progress?.Invoke($"Running dotnet restore for {Path.GetFileName(target)} (configured NuGet feeds may be contacted)...");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        ProcessResult result;
        try
        {
            var arguments = new List<string>
            {
                "restore",
                target,
                "--nologo",
                "--verbosity",
                "minimal",
            };
            if (forceEvaluate)
            {
                arguments.Add("--force-evaluate");
            }

            if (packagesDirectory is not null)
            {
                Directory.CreateDirectory(packagesDirectory);
                arguments.Add("--packages");
                arguments.Add(packagesDirectory);
            }

            result = await processRunner.RunAsync(
                "dotnet",
                arguments,
                Path.GetDirectoryName(target)!,
                timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RestoreTargetOutcome(
                [],
                [$"dotnet restore timed out for '{target}' after {timeout.TotalSeconds:0} seconds."],
                RestoreProcessFailureKind.TimedOut);
        }

        var errors = new List<string>();
        if (result.ExitCode != 0)
        {
            var detail = CompactError(result);
            errors.Add(
                $"dotnet restore failed for '{target}' with exit code {result.ExitCode}." +
                (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" {detail}"));
        }

        if (result.StandardOutputTruncated || result.StandardErrorTruncated)
        {
            errors.Add(
                $"dotnet restore output exceeded the {ProcessRunner.DefaultMaximumOutputCharacters}-character safety limit for '{target}'.");
        }

        var failureKind = result.StandardOutputTruncated || result.StandardErrorTruncated
            ? RestoreProcessFailureKind.OutputLimitExceeded
            : result.ExitCode == 0
                ? RestoreProcessFailureKind.None
                : RestoreProcessFailureKind.Rejected;
        return new RestoreTargetOutcome(ParseNuGetDiagnostics(result.CombinedOutput, target), errors, failureKind);
    }

    private sealed record RestoreTargetOutcome(
        IReadOnlyList<Diagnostic> Diagnostics,
        IReadOnlyList<string> Errors,
        RestoreProcessFailureKind FailureKind);

    private static RestoreProcessFailureKind AggregateFailure(IEnumerable<RestoreProcessFailureKind> failures)
    {
        var materialized = failures.ToArray();
        if (materialized.Contains(RestoreProcessFailureKind.TimedOut))
        {
            return RestoreProcessFailureKind.TimedOut;
        }

        if (materialized.Contains(RestoreProcessFailureKind.OutputLimitExceeded))
        {
            return RestoreProcessFailureKind.OutputLimitExceeded;
        }

        return materialized.Contains(RestoreProcessFailureKind.Rejected)
            ? RestoreProcessFailureKind.Rejected
            : RestoreProcessFailureKind.None;
    }

    public static IReadOnlyList<Diagnostic> ParseNuGetDiagnostics(string output, string fallbackFile)
    {
        var diagnostics = new List<Diagnostic>();
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = NuGetDiagnosticRegex().Match(rawLine);
            if (!match.Success)
            {
                continue;
            }

            var severity = match.Groups["level"].Value.Equals("error", StringComparison.OrdinalIgnoreCase)
                ? DiagnosticSeverity.Error
                : DiagnosticSeverity.Warning;
            var code = match.Groups["code"].Value.ToUpperInvariant();
            var message = match.Groups["message"].Value.Trim();
            var location = match.Groups["file"].Success ? match.Groups["file"].Value.Trim() : fallbackFile;
            diagnostics.Add(new Diagnostic(
                "PM005",
                severity,
                "NuGet restore problem",
                $"NuGet reported {code} during restore.",
                null,
                location,
                null,
                message,
                "Resolve the underlying NuGet restore issue, then run PackageMedic again.",
                DiagnosticConfidence.High,
                code));
        }

        return Deduplicate(diagnostics);
    }

    internal static IReadOnlyList<Diagnostic> Deduplicate(IEnumerable<Diagnostic> diagnostics) => diagnostics
        .DistinctBy(item => (item.Code, item.OriginalCode, item.Severity, item.File, item.Line, item.Evidence))
        .OrderByDescending(item => item.Severity)
        .ThenBy(item => item.OriginalCode, StringComparer.Ordinal)
        .ThenBy(item => item.File, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.Line)
        .ToArray();

    private static string CompactError(ProcessResult result)
    {
        var value = ProcessRunner.RedactSecrets(
            string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError);
        var compact = string.Join(
            ' ',
            value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3));
        return compact.Length <= 500 ? compact : string.Concat(compact.AsSpan(0, 500), "...");
    }

    [GeneratedRegex("(?:(?<file>.+?)\\s*:\\s*)?(?<level>warning|error)\\s+(?<code>NU\\d{4})\\s*:\\s*(?<message>.+?)(?:\\s+\\[[^]]+\\])?$", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex NuGetDiagnosticRegex();
}
