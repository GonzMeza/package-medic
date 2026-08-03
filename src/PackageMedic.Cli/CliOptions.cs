using PackageMedic.Core;

namespace PackageMedic.Cli;

public enum CliCommand
{
    Help,
    Version,
    Doctor,
    Audit,
    Diff,
    Init,
    BaselineCreate,
    BaselineUpdate,
    Rules,
    Explain,
    Clean,
}

public enum OutputFormat
{
    Text,
    Json,
    Sarif,
}

public enum OutputVerbosity
{
    Quiet,
    Normal,
    Detailed,
}

public sealed record CliOptions(
    CliCommand Command,
    string? Path = null,
    bool NoRestore = false,
    OutputFormat Format = OutputFormat.Text,
    PolicyFailureLevel? FailOn = null,
    PolicyFailureLevel? FailOnNew = null,
    OutputVerbosity Verbosity = OutputVerbosity.Normal,
    string? OutputPath = null,
    string? SarifOutputPath = null,
    string? ConfigurationPath = null,
    bool NoConfiguration = false,
    string? BaselinePath = null,
    int? RestoreTimeoutSeconds = null,
    int? EvaluationTimeoutSeconds = null,
    bool Force = false,
    bool DryRun = false,
    bool AuditVulnerabilities = false,
    bool IncludeTransitive = false,
    string? GitReference = null,
    string? RuleCode = null,
    bool ShowHelp = false,
    int? MaxParallelism = null)
{
    public static CliOptions Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0 || arguments.Count == 1 && arguments[0] is "--help" or "-h")
        {
            return new CliOptions(CliCommand.Help, ShowHelp: true);
        }

        if (arguments.Count == 1 && arguments[0] is "--version" or "-v")
        {
            return new CliOptions(CliCommand.Version);
        }

        return arguments[0].ToLowerInvariant() switch
        {
            "doctor" => ParseScan(arguments, 1, CliCommand.Doctor),
            "audit" => ParseScan(arguments, 1, CliCommand.Audit),
            "diff" => ParseDiff(arguments),
            "clean" => ParseScan(arguments, 1, CliCommand.Clean),
            "init" => ParseInit(arguments),
            "baseline" => ParseBaseline(arguments),
            "rules" => ParseRules(arguments),
            "explain" => ParseExplain(arguments),
            _ => throw new UsageException($"Unknown command '{arguments[0]}'."),
        };
    }

    private static CliOptions ParseDiff(IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 2 || arguments[1] is "--help" or "-h")
        {
            return new CliOptions(CliCommand.Diff, ShowHelp: true);
        }

        var reference = arguments[1].Trim();
        if (reference.Length == 0 || reference.StartsWith("-", StringComparison.Ordinal))
        {
            throw new UsageException("Usage: package-medic diff <git-ref> [path] [options].");
        }

        return ParseScan(arguments, 2, CliCommand.Diff) with { GitReference = reference };
    }

    private static CliOptions ParseInit(IReadOnlyList<string> arguments)
    {
        string? path = null;
        var force = false;
        var help = false;
        for (var index = 1; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument == "--force") force = true;
            else if (argument is "--help" or "-h") help = true;
            else if (argument.StartsWith("-", StringComparison.Ordinal)) throw Unknown(argument);
            else if (path is null) path = argument;
            else throw new UsageException("Only one configuration path can be specified.");
        }

        return new CliOptions(CliCommand.Init, path, Force: force, ShowHelp: help);
    }

    private static CliOptions ParseBaseline(IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 2 || arguments[1] is "--help" or "-h")
        {
            return new CliOptions(CliCommand.BaselineCreate, ShowHelp: true);
        }

        var command = arguments[1].ToLowerInvariant() switch
        {
            "create" => CliCommand.BaselineCreate,
            "update" => CliCommand.BaselineUpdate,
            _ => throw new UsageException("Expected 'baseline create' or 'baseline update'."),
        };
        var parsed = ParseScan(arguments, 2, command);
        if (!parsed.ShowHelp && command == CliCommand.BaselineCreate && string.IsNullOrWhiteSpace(parsed.OutputPath))
        {
            throw new UsageException("'baseline create' requires --output <path>.");
        }

        return parsed;
    }

    private static CliOptions ParseRules(IReadOnlyList<string> arguments)
    {
        if (arguments.Count > 2 || arguments.Count == 2 && arguments[1] is not ("--help" or "-h"))
        {
            throw new UsageException("The 'rules' command does not accept arguments.");
        }

        return new CliOptions(CliCommand.Rules, ShowHelp: arguments.Count == 2);
    }

    private static CliOptions ParseExplain(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 2 && arguments[1] is "--help" or "-h")
        {
            return new CliOptions(CliCommand.Explain, ShowHelp: true);
        }

        if (arguments.Count != 2 || arguments[1].StartsWith("-", StringComparison.Ordinal))
        {
            throw new UsageException("Usage: package-medic explain <PM code>.");
        }

        return new CliOptions(CliCommand.Explain, RuleCode: arguments[1].ToUpperInvariant());
    }

    private static CliOptions ParseScan(IReadOnlyList<string> arguments, int start, CliCommand command)
    {
        string? path = null;
        var noRestore = false;
        var format = OutputFormat.Text;
        PolicyFailureLevel? failOn = null;
        PolicyFailureLevel? failOnNew = null;
        var verbosity = OutputVerbosity.Normal;
        string? output = null;
        string? sarifOutput = null;
        string? config = null;
        var noConfig = false;
        string? baseline = null;
        int? restoreTimeout = null;
        int? evaluationTimeout = null;
        var force = false;
        var dryRun = false;
        var auditVulnerabilities = command == CliCommand.Audit;
        var includeTransitive = false;
        var help = false;
        int? maxParallelism = null;

        for (var index = start; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument == "--no-restore") noRestore = true;
            else if (argument == "--no-config") noConfig = true;
            else if (argument == "--force") force = true;
            else if (argument == "--dry-run") dryRun = true;
            else if (argument == "--audit") auditVulnerabilities = true;
            else if (argument == "--include-transitive")
            {
                auditVulnerabilities = true;
                includeTransitive = true;
            }
            else if (argument is "--help" or "-h") help = true;
            else if (TryReadOption(arguments, ref index, "--format", out var value))
                format = ParseEnum<OutputFormat>(value, "--format", "text|json|sarif");
            else if (TryReadOption(arguments, ref index, "--output", out value) || TryReadOption(arguments, ref index, "-o", out value))
                output = NonEmpty(value, "--output");
            else if (TryReadOption(arguments, ref index, "--sarif-output", out value))
                sarifOutput = NonEmpty(value, "--sarif-output");
            else if (TryReadOption(arguments, ref index, "--fail-on", out value))
                failOn = ParseEnum<PolicyFailureLevel>(value, "--fail-on", "none|warning|error");
            else if (TryReadOption(arguments, ref index, "--fail-on-new", out value))
                failOnNew = ParseEnum<PolicyFailureLevel>(value, "--fail-on-new", "none|warning|error");
            else if (TryReadOption(arguments, ref index, "--verbosity", out value))
                verbosity = ParseEnum<OutputVerbosity>(value, "--verbosity", "quiet|normal|detailed");
            else if (TryReadOption(arguments, ref index, "--config", out value)) config = NonEmpty(value, "--config");
            else if (TryReadOption(arguments, ref index, "--baseline", out value)) baseline = NonEmpty(value, "--baseline");
            else if (TryReadOption(arguments, ref index, "--restore-timeout", out value)) restoreTimeout = PositiveInt(value, "--restore-timeout");
            else if (TryReadOption(arguments, ref index, "--evaluation-timeout", out value)) evaluationTimeout = PositiveInt(value, "--evaluation-timeout");
            else if (TryReadOption(arguments, ref index, "--max-parallelism", out value))
                maxParallelism = BoundedInt(value, "--max-parallelism", 1, 32);
            else if (argument.StartsWith("-", StringComparison.Ordinal)) throw Unknown(argument);
            else if (path is null) path = argument;
            else throw new UsageException("Only one target path can be specified.");
        }

        if (config is not null && noConfig)
        {
            throw new UsageException("--config and --no-config cannot be used together.");
        }

        if (command == CliCommand.Clean && !help && !dryRun)
        {
            throw new UsageException("PackageMedic clean is read-only and requires --dry-run.");
        }

        if (force && command != CliCommand.BaselineCreate)
        {
            throw new UsageException("--force is only supported by 'baseline create' and 'init'.");
        }

        if (dryRun && command != CliCommand.Clean)
        {
            throw new UsageException("--dry-run is only supported by 'clean'.");
        }

        if (command is not (CliCommand.Doctor or CliCommand.Audit or CliCommand.Diff) && sarifOutput is not null)
        {
            throw new UsageException("--sarif-output is only supported by 'doctor', 'audit', and 'diff'.");
        }

        if (command == CliCommand.Clean && (output is not null || format != OutputFormat.Text))
        {
            throw new UsageException("'clean --dry-run' writes its plan to standard output and does not accept --output or --format.");
        }

        if (command == CliCommand.BaselineCreate && baseline is not null)
        {
            throw new UsageException("'baseline create' cannot compare against an existing --baseline.");
        }

        if (command is CliCommand.BaselineCreate or CliCommand.BaselineUpdate && format != OutputFormat.Text)
        {
            throw new UsageException("Baseline commands always write the PackageMedic baseline JSON schema and do not accept --format.");
        }

        if (auditVulnerabilities && command is not (CliCommand.Doctor or CliCommand.Audit or CliCommand.Diff))
        {
            throw new UsageException("--audit and --include-transitive are only supported by 'doctor', 'audit', and 'diff'.");
        }

        if (command == CliCommand.Diff && baseline is not null)
        {
            throw new UsageException("'diff' compares against its Git reference directly and does not accept --baseline.");
        }

        if (command == CliCommand.Diff && failOnNew is not null)
        {
            throw new UsageException("'diff' already gates only added or worsened findings and does not accept --fail-on-new.");
        }

        return new CliOptions(
            command, path, noRestore, format, failOn, failOnNew, verbosity, output, sarifOutput,
            config, noConfig, baseline, restoreTimeout, evaluationTimeout, force, dryRun,
            auditVulnerabilities, includeTransitive, ShowHelp: help, MaxParallelism: maxParallelism);
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

    private static T ParseEnum<T>(string value, string option, string expected) where T : struct
    {
        if (Enum.TryParse<T>(value, true, out var parsed)) return parsed;
        throw new UsageException($"Invalid value '{value}' for {option}; expected {expected}.");
    }

    private static string NonEmpty(string value, string option) =>
        !string.IsNullOrWhiteSpace(value) ? value : throw new UsageException($"Option '{option}' requires a non-empty path.");

    private static int PositiveInt(string value, string option) =>
        int.TryParse(value, out var parsed) && parsed is >= 1 and <= 3600
            ? parsed
            : throw new UsageException($"Option '{option}' must be an integer between 1 and 3600 seconds.");

    private static int BoundedInt(string value, string option, int minimum, int maximum) =>
        int.TryParse(value, out var parsed) && parsed >= minimum && parsed <= maximum
            ? parsed
            : throw new UsageException($"Option '{option}' must be an integer between {minimum} and {maximum}.");

    private static UsageException Unknown(string argument) => new($"Unknown option '{argument}'.");
}

public sealed class UsageException(string message) : Exception(message);
