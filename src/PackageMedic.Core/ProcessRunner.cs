using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PackageMedic.Core;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string CombinedOutput => string.Concat(StandardOutput, Environment.NewLine, StandardError);
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken);
}

public sealed partial class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited between cancellation and the kill request.
            }

            throw;
        }

        return new ProcessResult(
            process.ExitCode,
            RedactSecrets(await stdoutTask.ConfigureAwait(false)),
            RedactSecrets(await stderrTask.ConfigureAwait(false)));
    }

    internal static string RedactSecrets(string value)
    {
        value = CredentialsInUrlRegex().Replace(value, "${scheme}[REDACTED]@");
        return SecretAssignmentRegex().Replace(value, "${name}=[REDACTED]");
    }

    [GeneratedRegex("(?<scheme>https?://)[^/@\\s:]+:[^/@\\s]+@", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialsInUrlRegex();

    [GeneratedRegex("(?<name>(?:password|token|apikey|api_key|username))\\s*=\\s*[^;\\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex SecretAssignmentRegex();
}
