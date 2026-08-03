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
            await error.WriteLineAsync("Run 'package-medic doctor --help' for usage.").ConfigureAwait(false);
            return 2;
        }

        if (options.ShowVersion)
        {
            await output.WriteLineAsync(PackageMedicAnalyzer.Version).ConfigureAwait(false);
            return 0;
        }

        if (options.ShowHelp)
        {
            await output.WriteAsync(HelpText).ConfigureAwait(false);
            return 0;
        }

        try
        {
            ValidateOutputPaths(options);
            var analyzer = new PackageMedicAnalyzer();
            Action<string>? progress = options.Verbosity == OutputVerbosity.Quiet
                ? null
                : message => error.WriteLine(message);
            var outcome = await analyzer.AnalyzeAsync(options.Path, options.NoRestore, progress, cancellationToken).ConfigureAwait(false);

            var rendered = await RenderResultAsync(outcome.Result, options).ConfigureAwait(false);
            var additionalSarif = options.SarifOutputPath is null
                ? null
                : options.Format == OutputFormat.Sarif
                    ? rendered
                    : RenderSarif(outcome.Result);

            if (additionalSarif is not null)
            {
                await AtomicOutputFile.WriteAsync(
                    options.SarifOutputPath!,
                    additionalSarif,
                    cancellationToken).ConfigureAwait(false);
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

            if (outcome.HasOperationalError)
            {
                return 2;
            }

            return ReachesThreshold(outcome.Result.Diagnostics, options.FailOn) ? 1 : 0;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("error: analysis was cancelled.").ConfigureAwait(false);
            return 2;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            await error.WriteLineAsync($"error: {exception.Message}").ConfigureAwait(false);
            return 2;
        }
    }

    private static void ValidateOutputPaths(CliOptions options)
    {
        if (options.OutputPath is null || options.SarifOutputPath is null)
        {
            return;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(
                Path.GetFullPath(options.OutputPath),
                Path.GetFullPath(options.SarifOutputPath),
                comparison))
        {
            throw new ArgumentException("--output and --sarif-output must use different paths.");
        }
    }

    private static bool ReachesThreshold(IReadOnlyList<Diagnostic> diagnostics, FailOnLevel failOn) => failOn switch
    {
        FailOnLevel.None => false,
        FailOnLevel.Warning => diagnostics.Any(item => item.Severity >= DiagnosticSeverity.Warning),
        FailOnLevel.Error => diagnostics.Any(item => item.Severity >= DiagnosticSeverity.Error),
        _ => false,
    };

    private static async Task<string> RenderResultAsync(AnalysisResult result, CliOptions options)
    {
        if (options.Format == OutputFormat.Json)
        {
            return ResultJsonSerializer.Serialize(result) + "\n";
        }

        if (options.Format == OutputFormat.Sarif)
        {
            return RenderSarif(result);
        }

        using var writer = new StringWriter();
        await TextResultWriter.WriteAsync(result, options.Verbosity, writer).ConfigureAwait(false);
        return writer.ToString();
    }

    private static string RenderSarif(AnalysisResult result) =>
        SarifResultSerializer.Serialize(result, FindRepositoryRoot(result.Target)) + "\n";

    private static string FindRepositoryRoot(string target)
    {
        var fullTarget = Path.GetFullPath(target);
        var startingDirectory = File.Exists(fullTarget)
            ? Path.GetDirectoryName(fullTarget)
            : fullTarget;
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

    private static string HelpText => $"""
PackageMedic {PackageMedicAnalyzer.Version}

Usage:
  package-medic doctor [path] [options]
  package-medic --version
  package-medic --help

Arguments:
  path                         .csproj, .sln, .slnx, or directory (default: current directory)

Options:
  --no-restore                 Analyze existing project.assets.json files without restoring
  --format text|json|sarif     Output format (default: text)
  --output, -o <path>          Write the report to a file instead of standard output
  --sarif-output <path>        Also write a SARIF report from the same analysis
  --fail-on none|warning|error Exit 1 at or above this severity (default: warning)
  --verbosity quiet|normal|detailed
                               Diagnostic output detail (default: normal)
  --version                    Print the PackageMedic version
  --help                       Show this help

Exit codes:
  0  Analysis completed below the configured --fail-on threshold
  1  At least one diagnostic reached the configured --fail-on threshold
  2  Usage, restore, configuration, or analysis error
""";
}

public enum OutputFormat
{
    Text,
    Json,
    Sarif,
}

public enum FailOnLevel
{
    None,
    Warning,
    Error,
}

public enum OutputVerbosity
{
    Quiet,
    Normal,
    Detailed,
}

public sealed record CliOptions(
    string? Path,
    bool NoRestore,
    OutputFormat Format,
    FailOnLevel FailOn,
    OutputVerbosity Verbosity,
    string? OutputPath,
    string? SarifOutputPath,
    bool ShowVersion,
    bool ShowHelp)
{
    public static CliOptions Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return new CliOptions(null, false, OutputFormat.Text, FailOnLevel.Warning, OutputVerbosity.Normal, null, null, false, true);
        }

        if (arguments.Count == 1 && arguments[0] is "--version" or "-v")
        {
            return new CliOptions(null, false, OutputFormat.Text, FailOnLevel.Warning, OutputVerbosity.Normal, null, null, true, false);
        }

        if (arguments.Count == 1 && arguments[0] is "--help" or "-h")
        {
            return new CliOptions(null, false, OutputFormat.Text, FailOnLevel.Warning, OutputVerbosity.Normal, null, null, false, true);
        }

        if (!arguments[0].Equals("doctor", StringComparison.OrdinalIgnoreCase))
        {
            throw new UsageException($"Unknown command '{arguments[0]}'. Expected 'doctor'.");
        }

        string? path = null;
        var noRestore = false;
        var format = OutputFormat.Text;
        var failOn = FailOnLevel.Warning;
        var verbosity = OutputVerbosity.Normal;
        string? outputPath = null;
        string? sarifOutputPath = null;
        var showVersion = false;
        var showHelp = false;

        for (var index = 1; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument == "--no-restore")
            {
                noRestore = true;
            }
            else if (argument is "--help" or "-h")
            {
                showHelp = true;
            }
            else if (argument is "--version" or "-v")
            {
                showVersion = true;
            }
            else if (TryReadOption(arguments, ref index, "--format", out var formatValue))
            {
                format = ParseEnum<OutputFormat>(formatValue, "--format", "text|json|sarif");
            }
            else if (TryReadOption(arguments, ref index, "--output", out var outputValue) ||
                     TryReadOption(arguments, ref index, "-o", out outputValue))
            {
                if (string.IsNullOrWhiteSpace(outputValue))
                {
                    throw new UsageException("Option '--output' requires a non-empty path.");
                }

                outputPath = outputValue;
            }
            else if (TryReadOption(arguments, ref index, "--sarif-output", out var sarifOutputValue))
            {
                if (string.IsNullOrWhiteSpace(sarifOutputValue))
                {
                    throw new UsageException("Option '--sarif-output' requires a non-empty path.");
                }

                sarifOutputPath = sarifOutputValue;
            }
            else if (TryReadOption(arguments, ref index, "--fail-on", out var failOnValue))
            {
                failOn = ParseEnum<FailOnLevel>(failOnValue, "--fail-on", "none|warning|error");
            }
            else if (TryReadOption(arguments, ref index, "--verbosity", out var verbosityValue))
            {
                verbosity = ParseEnum<OutputVerbosity>(verbosityValue, "--verbosity", "quiet|normal|detailed");
            }
            else if (argument.StartsWith("-", StringComparison.Ordinal))
            {
                throw new UsageException($"Unknown option '{argument}'.");
            }
            else if (path is null)
            {
                path = argument;
            }
            else
            {
                throw new UsageException("Only one target path can be specified.");
            }
        }

        return new CliOptions(path, noRestore, format, failOn, verbosity, outputPath, sarifOutputPath, showVersion, showHelp);
    }

    private static bool TryReadOption(IReadOnlyList<string> arguments, ref int index, string name, out string value)
    {
        var argument = arguments[index];
        if (argument.StartsWith(name + "=", StringComparison.Ordinal))
        {
            value = argument[(name.Length + 1)..];
            return true;
        }

        if (argument != name)
        {
            value = string.Empty;
            return false;
        }

        if (index + 1 >= arguments.Count)
        {
            throw new UsageException($"Option '{name}' requires a value.");
        }

        value = arguments[++index];
        return true;
    }

    private static T ParseEnum<T>(string value, string option, string expected)
        where T : struct
    {
        if (Enum.TryParse<T>(value, ignoreCase: true, out var result))
        {
            return result;
        }

        throw new UsageException($"Invalid value '{value}' for {option}; expected {expected}.");
    }
}

public sealed class UsageException(string message) : Exception(message);
