namespace PackageMedic.Core;

public sealed record AnalysisExecutionOptions
{
    private static readonly int DefaultParallelism = Math.Clamp(Environment.ProcessorCount, 1, 4);

    public AnalysisExecutionOptions(
        TimeSpan restoreTimeout,
        TimeSpan msBuildEvaluationTimeout,
        int? maxDegreeOfParallelism = null)
    {
        RestoreTimeout = restoreTimeout;
        MsBuildEvaluationTimeout = msBuildEvaluationTimeout;
        MaxDegreeOfParallelism = maxDegreeOfParallelism ?? DefaultParallelism;
    }

    public static AnalysisExecutionOptions Default { get; } = new(
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(1));

    public TimeSpan RestoreTimeout { get; }

    public TimeSpan MsBuildEvaluationTimeout { get; }

    public int MaxDegreeOfParallelism { get; }

    public AnalysisExecutionOptions Validate()
    {
        ValidateTimeout(RestoreTimeout, nameof(RestoreTimeout));
        ValidateTimeout(MsBuildEvaluationTimeout, nameof(MsBuildEvaluationTimeout));
        if (MaxDegreeOfParallelism is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxDegreeOfParallelism),
                "Parallelism must be between 1 and 32.");
        }

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
