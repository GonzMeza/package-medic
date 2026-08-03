using PackageMedic.Core;

namespace PackageMedic.Cli;

public static class Program
{
    public static Task<int> Main(string[] args) => ExecuteAsync(args, Console.Out, Console.Error, CancellationToken.None);

    public static async Task<int> ExecuteAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        CliOptions options;
        try
        {
            options = CliOptions.Parse(args);
        }
        catch (UsageException exception)
        {
            await error.WriteLineAsync($"error: {exception.Message}").ConfigureAwait(false);
            await error.WriteLineAsync("Run 'package-medic --help' for usage.").ConfigureAwait(false);
            return 2;
        }

        if (options.ShowHelp || options.Command == CliCommand.Help)
        {
            await output.WriteAsync(HelpText).ConfigureAwait(false);
            return 0;
        }

        try
        {
            return options.Command switch
            {
                CliCommand.Version => await WriteVersionAsync(output).ConfigureAwait(false),
                CliCommand.Rules => await WriteRulesAsync(output).ConfigureAwait(false),
                CliCommand.Explain => await ExplainRuleAsync(options.RuleCode!, output).ConfigureAwait(false),
                CliCommand.Init => await InitializeConfigurationAsync(options, output, cancellationToken).ConfigureAwait(false),
                CliCommand.BaselineCreate or CliCommand.BaselineUpdate =>
                    await WriteBaselineAsync(options, output, error, cancellationToken).ConfigureAwait(false),
                CliCommand.Clean => await WriteCleanPlanAsync(options, output, error, cancellationToken).ConfigureAwait(false),
                CliCommand.Doctor => await RunDoctorAsync(options, output, error, cancellationToken).ConfigureAwait(false),
                _ => throw new UsageException("A command is required."),
            };
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("error: analysis was cancelled.").ConfigureAwait(false);
            return 2;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            UnauthorizedAccessException or
            IOException or
            UsageException or
            PackageMedicConfigurationException)
        {
            await error.WriteLineAsync($"error: {exception.Message}").ConfigureAwait(false);
            return 2;
        }
    }

    private static async Task<int> RunDoctorAsync(
        CliOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ValidateOutputPaths(options);
        var prepared = await AnalyzeAsync(options, error, cancellationToken).ConfigureAwait(false);
        var rendered = await RenderResultAsync(prepared, options).ConfigureAwait(false);
        var additionalSarif = options.SarifOutputPath is null
            ? null
            : options.Format == OutputFormat.Sarif
                ? rendered
                : RenderSarif(prepared);

        if (additionalSarif is not null)
        {
            await AtomicOutputFile.WriteAsync(options.SarifOutputPath!, additionalSarif, cancellationToken).ConfigureAwait(false);
            if (options.Verbosity != OutputVerbosity.Quiet)
            {
                await error.WriteLineAsync($"Wrote sarif report to {options.SarifOutputPath}").ConfigureAwait(false);
            }
        }

        if (options.OutputPath is null)
        {
            await output.WriteAsync(rendered).ConfigureAwait(false);
        }
        else
        {
            await AtomicOutputFile.WriteAsync(options.OutputPath, rendered, cancellationToken).ConfigureAwait(false);
            if (options.Verbosity != OutputVerbosity.Quiet)
            {
                await error.WriteLineAsync($"Wrote {options.Format.ToString().ToLowerInvariant()} report to {options.OutputPath}").ConfigureAwait(false);
            }
        }

        if (prepared.HasOperationalError)
        {
            return 2;
        }

        var allReached = ReachesThreshold(prepared.Result.Diagnostics, prepared.Context.Policy.FailOn);
        var newDiagnostics = prepared.Context.Baseline.Current
            .Where(item => item.State == BaselineDiagnosticState.New)
            .Select(item => item.Diagnostic);
        var newReached = prepared.Context.Policy.FailOnNew is { } failOnNew &&
                         ReachesThreshold(newDiagnostics, failOnNew);
        return allReached || newReached ? 1 : 0;
    }

    private static async Task<int> WriteBaselineAsync(
        CliOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        string? createDestination = null;
        if (options.Command == CliCommand.BaselineCreate)
        {
            createDestination = Path.GetFullPath(options.OutputPath!);
            if (File.Exists(createDestination) && !options.Force)
            {
                throw new InvalidOperationException($"Baseline '{createDestination}' already exists; use --force to replace it.");
            }
        }

        var prepared = await AnalyzeAsync(options, error, cancellationToken).ConfigureAwait(false);
        if (prepared.HasOperationalError)
        {
            return 2;
        }

        PackageMedicBaseline baseline;
        string destination;
        if (options.Command == CliCommand.BaselineCreate)
        {
            destination = createDestination!;
            baseline = BaselineSerializer.Create(prepared.Result, prepared.Context.RepositoryRoot);
        }
        else
        {
            var source = options.BaselinePath is not null
                ? Path.GetFullPath(options.BaselinePath)
                : prepared.Context.Policy.BaselinePath;
            if (source is null)
            {
                throw new InvalidOperationException("'baseline update' requires --baseline or a configured baseline.");
            }

            var previous = BaselineSerializer.Load(source);
            baseline = BaselineSerializer.Update(previous, prepared.Result, prepared.Context.RepositoryRoot);
            destination = Path.GetFullPath(options.OutputPath ?? source);
        }

        await AtomicOutputFile.WriteAsync(
            destination,
            BaselineSerializer.Serialize(baseline) + "\n",
            cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(
            $"Wrote baseline with {baseline.Entries.Count} diagnostics to {destination}").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> WriteCleanPlanAsync(
        CliOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var prepared = await AnalyzeAsync(options, error, cancellationToken).ConfigureAwait(false);
        if (prepared.HasOperationalError)
        {
            return 2;
        }

        var candidates = prepared.Result.Diagnostics.Where(item => item.Code == "PM001").ToArray();
        await output.WriteLineAsync("PackageMedic clean --dry-run").ConfigureAwait(false);
        await output.WriteLineAsync("No dependency files were modified.").ConfigureAwait(false);
        if (candidates.Length == 0)
        {
            await output.WriteLineAsync("No safe unused central-version candidates were found.").ConfigureAwait(false);
            return 0;
        }

        foreach (var diagnostic in candidates)
        {
            var location = diagnostic.File is null
                ? "unknown location"
                : $"{diagnostic.File}{(diagnostic.Line is null ? string.Empty : $":{diagnostic.Line}")}";
            await output.WriteLineAsync($"Would review/remove: {diagnostic.Evidence} ({location})").ConfigureAwait(false);
        }

        await output.WriteLineAsync($"Plan: {candidates.Length} candidate(s); apply is intentionally unavailable in 0.3.").ConfigureAwait(false);
        return 0;
    }

    private static async Task<PreparedAnalysis> AnalyzeAsync(
        CliOptions options,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = FindRepositoryRoot(options.Path);
        var (configuration, configurationPath, configurationDirectory) = LoadConfiguration(options, repositoryRoot);
        var baselineOverride = options.BaselinePath is null ? null : Path.GetFullPath(options.BaselinePath);
        var policy = AnalysisPolicyResolver.Resolve(
            configuration,
            configurationDirectory,
            new AnalysisPolicyOverrides(
                options.FailOn,
                options.FailOnNew,
                baselineOverride,
                options.RestoreTimeoutSeconds,
                options.EvaluationTimeoutSeconds));
        var executionOptions = new AnalysisExecutionOptions(policy.Timeouts.Restore, policy.Timeouts.Evaluation);
        var analyzer = new PackageMedicAnalyzer(new ProcessRunner(), executionOptions);
        Action<string>? progress = options.Verbosity == OutputVerbosity.Quiet
            ? null
            : message => error.WriteLine(message);
        var outcome = await analyzer.AnalyzeAsync(options.Path, options.NoRestore, progress, cancellationToken).ConfigureAwait(false);

        repositoryRoot = FindRepositoryRoot(outcome.Result.Target);
        var application = policy.Apply(outcome.Result.Diagnostics, repositoryRoot);
        var result = outcome.Result with
        {
            Diagnostics = application.Diagnostics,
            Summary = Recount(outcome.Result.Summary, application.Diagnostics),
        };
        var baseline = policy.BaselinePath is null
            ? new PackageMedicBaseline(PackageMedicBaseline.CurrentSchemaVersion, result.Version, [])
            : BaselineSerializer.Load(policy.BaselinePath);
        var comparison = BaselineMatcher.Compare(result, baseline, repositoryRoot);
        var context = new AnalysisReportContext(
            repositoryRoot,
            ToPortableDisplayPath(configurationPath, repositoryRoot),
            policy,
            application,
            comparison,
            ToPortableDisplayPath(policy.BaselinePath, repositoryRoot));
        return new PreparedAnalysis(result, outcome.HasOperationalError, context);
    }

    private static (PackageMedicConfiguration Configuration, string? Path, string Directory) LoadConfiguration(
        CliOptions options,
        string repositoryRoot)
    {
        if (options.NoConfiguration)
        {
            return (PackageMedicConfiguration.Default, null, repositoryRoot);
        }

        var configurationPath = options.ConfigurationPath is null
            ? FindAutomaticConfiguration(options.Path, repositoryRoot)
            : Path.GetFullPath(options.ConfigurationPath);
        if (configurationPath is null)
        {
            return (PackageMedicConfiguration.Default, null, repositoryRoot);
        }

        return (
            PackageMedicConfigurationLoader.Load(configurationPath),
            configurationPath,
            Path.GetDirectoryName(configurationPath) ?? repositoryRoot);
    }

    private static string? FindAutomaticConfiguration(string? requestedPath, string repositoryRoot)
    {
        var fullPath = Path.GetFullPath(requestedPath ?? Directory.GetCurrentDirectory());
        var start = File.Exists(fullPath) ? Path.GetDirectoryName(fullPath)! : fullPath;
        if (!Directory.Exists(start))
        {
            start = Path.GetDirectoryName(start) ?? repositoryRoot;
        }

        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".packagemedic.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (PathsEqual(directory.FullName, repositoryRoot))
            {
                break;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static async Task<int> InitializeConfigurationAsync(
        CliOptions options,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var requested = Path.GetFullPath(options.Path ?? Directory.GetCurrentDirectory());
        var destination = Directory.Exists(requested)
            ? Path.Combine(requested, ".packagemedic.json")
            : File.Exists(requested) || Path.GetExtension(requested).Equals(".json", StringComparison.OrdinalIgnoreCase)
                ? requested
                : Path.Combine(requested, ".packagemedic.json");
        if (File.Exists(destination) && !options.Force)
        {
            throw new InvalidOperationException($"Configuration '{destination}' already exists; use --force to replace it.");
        }

        await AtomicOutputFile.WriteAsync(destination, DefaultConfiguration, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"Created {destination}").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> WriteVersionAsync(TextWriter output)
    {
        await output.WriteLineAsync(PackageMedicAnalyzer.Version).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> WriteRulesAsync(TextWriter output)
    {
        await output.WriteLineAsync("Code   Severity     Rule").ConfigureAwait(false);
        foreach (var rule in DiagnosticRuleCatalog.All.OrderBy(item => item.Code, StringComparer.Ordinal))
        {
            await output.WriteLineAsync(
                $"{rule.Code,-6} {rule.DefaultSeverity.ToString().ToLowerInvariant(),-12} {rule.Name}").ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task<int> ExplainRuleAsync(string code, TextWriter output)
    {
        if (!DiagnosticRuleCatalog.TryGet(code, out var rule) || rule is null)
        {
            throw new UsageException($"Unknown diagnostic code '{code}'. Run 'package-medic rules' to list rules.");
        }

        await output.WriteLineAsync($"{rule.Code} — {rule.Name}").ConfigureAwait(false);
        await output.WriteLineAsync($"Default severity: {rule.DefaultSeverity.ToString().ToLowerInvariant()}").ConfigureAwait(false);
        await output.WriteLineAsync(rule.FullDescription).ConfigureAwait(false);
        await output.WriteLineAsync($"Documentation: {rule.HelpUri}").ConfigureAwait(false);
        return 0;
    }

    private static ScanSummary Recount(ScanSummary original, IReadOnlyList<Diagnostic> diagnostics) => original with
    {
        Errors = diagnostics.Count(item => item.Severity == DiagnosticSeverity.Error),
        Warnings = diagnostics.Count(item => item.Severity == DiagnosticSeverity.Warning),
        Information = diagnostics.Count(item => item.Severity == DiagnosticSeverity.Information),
    };

    private static bool ReachesThreshold(IEnumerable<Diagnostic> diagnostics, PolicyFailureLevel failOn) => failOn switch
    {
        PolicyFailureLevel.None => false,
        PolicyFailureLevel.Warning => diagnostics.Any(item => item.Severity >= DiagnosticSeverity.Warning),
        PolicyFailureLevel.Error => diagnostics.Any(item => item.Severity >= DiagnosticSeverity.Error),
        _ => false,
    };

    private static async Task<string> RenderResultAsync(PreparedAnalysis prepared, CliOptions options)
    {
        if (options.Format == OutputFormat.Json)
        {
            return ResultJsonSerializer.Serialize(prepared.Result, prepared.Context) + "\n";
        }

        if (options.Format == OutputFormat.Sarif)
        {
            return RenderSarif(prepared);
        }

        using var writer = new StringWriter();
        await TextResultWriter.WriteAsync(prepared.Result, options.Verbosity, writer, prepared.Context).ConfigureAwait(false);
        return writer.ToString();
    }

    private static string RenderSarif(PreparedAnalysis prepared) =>
        SarifResultSerializer.Serialize(
            prepared.Result,
            prepared.Context.RepositoryRoot,
            prepared.Context.Baseline) + "\n";

    private static void ValidateOutputPaths(CliOptions options)
    {
        if (options.OutputPath is null || options.SarifOutputPath is null)
        {
            return;
        }

        if (PathsEqual(Path.GetFullPath(options.OutputPath), Path.GetFullPath(options.SarifOutputPath)))
        {
            throw new ArgumentException("--output and --sarif-output must use different paths.");
        }
    }

    private static string FindRepositoryRoot(string? target)
    {
        var fullTarget = Path.GetFullPath(target ?? Directory.GetCurrentDirectory());
        var startingDirectory = File.Exists(fullTarget)
            ? Path.GetDirectoryName(fullTarget)
            : Directory.Exists(fullTarget)
                ? fullTarget
                : Path.GetDirectoryName(fullTarget);
        var directory = new DirectoryInfo(startingDirectory ?? Directory.GetCurrentDirectory());
        var fallback = directory.FullName;

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return fallback;
    }

    private static string? ToPortableDisplayPath(string? path, string repositoryRoot)
    {
        if (path is null)
        {
            return null;
        }

        var relative = Path.GetRelativePath(repositoryRoot, Path.GetFullPath(path));
        return relative.StartsWith("..", StringComparison.Ordinal)
            ? Path.GetFileName(path)
            : relative.Replace('\\', '/');
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string DefaultConfiguration =>
        """
        {
          "$schema": "https://raw.githubusercontent.com/GonzMeza/package-medic/main/schemas/packagemedic.schema.json",
          "schemaVersion": 1,
          "failOn": "warning",
          "exclude": [
            "**/bin/**",
            "**/obj/**"
          ],
          "rules": {
            "PM006": {
              "enabled": true,
              "severity": "warning"
            }
          },
          "suppressions": [],
          "timeouts": {
            "restoreSeconds": 300,
            "evaluationSeconds": 60
          }
        }
        """ + "\n";

    private static string HelpText => $"""
        PackageMedic {PackageMedicAnalyzer.Version}

        Usage:
          package-medic doctor [path] [options]
          package-medic init [directory|file] [--force]
          package-medic baseline create [path] --output <file> [options]
          package-medic baseline update [path] [--baseline <file>] [--output <file>] [options]
          package-medic rules
          package-medic explain <PM code>
          package-medic clean [path] --dry-run [options]
          package-medic --version

        Common scan options:
          --config <path>              Use this configuration file
          --no-config                  Do not auto-load .packagemedic.json
          --baseline <path>            Compare against this baseline
          --no-restore                 Use existing project.assets.json files
          --restore-timeout <seconds>  Restore timeout, 1-3600
          --evaluation-timeout <seconds>
                                       Per-MSBuild evaluation timeout, 1-3600
          --verbosity quiet|normal|detailed

        Doctor report and gate options:
          --format text|json|sarif     Output format (default: text)
          --output, -o <path>          Write the selected report
          --sarif-output <path>        Also write SARIF from the same analysis
          --fail-on none|warning|error Fail on any effective diagnostic
          --fail-on-new none|warning|error
                                       Fail only on diagnostics absent from baseline

        Exit codes:
          0  Analysis completed below the configured thresholds
          1  A configured diagnostic threshold was reached
          2  Usage, restore, configuration, or analysis error

        PackageMedic 0.3 remains read-only. clean only supports --dry-run.
        """;

    private sealed record PreparedAnalysis(
        AnalysisResult Result,
        bool HasOperationalError,
        AnalysisReportContext Context);
}
