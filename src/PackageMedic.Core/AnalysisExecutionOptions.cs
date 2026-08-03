namespace PackageMedic.Core;

public sealed record AnalysisExecutionOptions(
    TimeSpan RestoreTimeout,
    TimeSpan MsBuildEvaluationTimeout)
{
    public static AnalysisExecutionOptions Default { get; } = new(
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(2));

    public AnalysisExecutionOptions Validate()
    {
        ValidateTimeout(RestoreTimeout, nameof(RestoreTimeout));
        ValidateTimeout(MsBuildEvaluationTimeout, nameof(MsBuildEvaluationTimeout));
        return this;
    }

    private static void ValidateTimeout(TimeSpan timeout, string name)
    {
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(name, "Process timeouts must be between 1 second and 1 hour.");
        }
    }
}
