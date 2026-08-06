using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace PackageMedic.Core;

public sealed record TrxTestResultLimits(
    int MaximumFiles = 256,
    long MaximumTotalBytes = 256L * 1024 * 1024,
    long MaximumFileBytes = 64L * 1024 * 1024,
    int MaximumXmlDepth = 64,
    int MaximumTestCount = 1_000_000,
    int MaximumRetainedFailures = 200,
    int MaximumIdentityLength = 1_024,
    int MaximumDirectories = 4_096)
{
    public static TrxTestResultLimits Default { get; } = new();

    public TrxTestResultLimits Validate()
    {
        if (MaximumFiles < 1 ||
            MaximumTotalBytes < 1 ||
            MaximumFileBytes < 1 ||
            MaximumFileBytes > MaximumTotalBytes ||
            MaximumXmlDepth < 1 ||
            MaximumTestCount < 1 ||
            MaximumRetainedFailures < 0 ||
            MaximumIdentityLength < 1 ||
            MaximumDirectories < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TrxTestResultLimits),
                "TRX limits must be positive and internally consistent; retained failures may be zero.");
        }

        return this;
    }
}

public enum TrxTestEvidenceStatus
{
    Passed,
    Failed,
    Incomplete,
}

public enum TrxTestEvidenceErrorKind
{
    ResultsRootUnavailable,
    UnsafeResultsRoot,
    UnsafeResultsEntry,
    NoResultFiles,
    ResultFileLimitExceeded,
    ResultFileByteLimitExceeded,
    TotalByteLimitExceeded,
    XmlDepthLimitExceeded,
    TestCountLimitExceeded,
    IdentityLengthLimitExceeded,
    MalformedXml,
    InvalidDocument,
    ContradictoryCounts,
    PartialResults,
    NoTestsDiscovered,
    ReadFailure,
}

/// <summary>
/// Describes why TRX evidence is incomplete without retaining an absolute path, XML content,
/// process output, or another machine-specific value.
/// </summary>
public sealed record TrxTestEvidenceError(
    TrxTestEvidenceErrorKind Kind,
    int? ResultFileIndex = null);

/// <summary>
/// Bounded aggregate test evidence. Failed identities are stable, sorted identifiers derived
/// from TRX test metadata; they never include durations, timestamps, machine paths, or output.
/// </summary>
public sealed record TrxTestEvidence(
    TrxTestEvidenceStatus Status,
    int Total,
    int Passed,
    int Failed,
    int Skipped,
    IReadOnlyList<string> FailedTestIdentities,
    TrxTestEvidenceError? Error)
{
    public bool IsComplete => Status != TrxTestEvidenceStatus.Incomplete;

    public bool HasAdditionalFailedTests => Failed > FailedTestIdentities.Count;

    public VerificationStageEvidence ToVerificationStageEvidence() => Status switch
    {
        TrxTestEvidenceStatus.Passed => VerificationStageEvidence.Passed,
        TrxTestEvidenceStatus.Failed => VerificationStageEvidence.Failed(VerificationFailureKind.TestsFailed),
        TrxTestEvidenceStatus.Incomplete => VerificationStageEvidence.Incomplete(MapIncompleteFailure(Error)),
        _ => throw new ArgumentOutOfRangeException(nameof(Status), Status, "Unknown TRX evidence status."),
    };

    internal static TrxTestEvidence Incomplete(
        TrxTestEvidenceErrorKind kind,
        int? resultFileIndex = null,
        int total = 0,
        int passed = 0,
        int failed = 0,
        int skipped = 0,
        IReadOnlyList<string>? failedTestIdentities = null) => new(
        TrxTestEvidenceStatus.Incomplete,
        total,
        passed,
        failed,
        skipped,
        failedTestIdentities ?? Array.Empty<string>(),
        new TrxTestEvidenceError(kind, resultFileIndex));

    private static VerificationFailureKind MapIncompleteFailure(TrxTestEvidenceError? error) => error?.Kind switch
    {
        TrxTestEvidenceErrorKind.NoTestsDiscovered => VerificationFailureKind.NoTestsDiscovered,
        TrxTestEvidenceErrorKind.ResultFileLimitExceeded or
        TrxTestEvidenceErrorKind.ResultFileByteLimitExceeded or
        TrxTestEvidenceErrorKind.TotalByteLimitExceeded or
        TrxTestEvidenceErrorKind.XmlDepthLimitExceeded or
        TrxTestEvidenceErrorKind.TestCountLimitExceeded or
        TrxTestEvidenceErrorKind.IdentityLengthLimitExceeded => VerificationFailureKind.ResultLimitExceeded,
        TrxTestEvidenceErrorKind.UnsafeResultsRoot or
        TrxTestEvidenceErrorKind.UnsafeResultsEntry => VerificationFailureKind.UnsafeEnvironment,
        _ => VerificationFailureKind.TestResultsUnavailable,
    };
}

/// <summary>
/// Reads VSTest TRX files from a PackageMedic-owned directory without following filesystem
/// links. XML is consumed once with an asynchronous forward-only reader and external entities
/// are disabled.
/// </summary>
public sealed class TrxTestResultParser
{
    private const int StreamBufferBytes = 64 * 1024;

    public async Task<TrxTestEvidence> ParseAsync(
        OwnedTemporaryDirectory resultsRoot,
        TrxTestResultLimits? configuredLimits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resultsRoot);
        var limits = (configuredLimits ?? TrxTestResultLimits.Default).Validate();
        var discovery = DiscoverResultFiles(resultsRoot.DirectoryPath, limits, cancellationToken);
        if (discovery.Error is { } discoveryError)
        {
            return TrxTestEvidence.Incomplete(discoveryError);
        }

        var files = discovery.Files!;
        if (files.Count == 0)
        {
            return TrxTestEvidence.Incomplete(TrxTestEvidenceErrorKind.NoResultFiles);
        }

        var total = 0;
        var passed = 0;
        var failed = 0;
        var skipped = 0;
        var failedIdentities = new List<string>(Math.Min(limits.MaximumRetainedFailures, 32));
        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remainingTests = limits.MaximumTestCount - total;
            var remainingFailures = limits.MaximumRetainedFailures - failedIdentities.Count;
            var fileEvidence = await ParseFileAsync(
                files[index],
                limits,
                remainingTests,
                remainingFailures,
                cancellationToken).ConfigureAwait(false);
            if (fileEvidence.Error is { } fileError)
            {
                return TrxTestEvidence.Incomplete(
                    fileError,
                    index,
                    total,
                    passed,
                    failed,
                    skipped,
                    SortAndDeduplicate(failedIdentities));
            }

            try
            {
                total = checked(total + fileEvidence.Total);
                passed = checked(passed + fileEvidence.Passed);
                failed = checked(failed + fileEvidence.Failed);
                skipped = checked(skipped + fileEvidence.Skipped);
            }
            catch (OverflowException)
            {
                return TrxTestEvidence.Incomplete(
                    TrxTestEvidenceErrorKind.TestCountLimitExceeded,
                    index,
                    total,
                    passed,
                    failed,
                    skipped,
                    SortAndDeduplicate(failedIdentities));
            }

            failedIdentities.AddRange(fileEvidence.FailedIdentities);
        }

        var retained = SortAndDeduplicate(failedIdentities);
        if (total == 0)
        {
            return TrxTestEvidence.Incomplete(
                TrxTestEvidenceErrorKind.NoTestsDiscovered,
                total: total,
                passed: passed,
                failed: failed,
                skipped: skipped,
                failedTestIdentities: retained);
        }

        return new TrxTestEvidence(
            failed == 0 ? TrxTestEvidenceStatus.Passed : TrxTestEvidenceStatus.Failed,
            total,
            passed,
            failed,
            skipped,
            retained,
            Error: null);
    }

    private static DiscoveryResult DiscoverResultFiles(
        string requestedRoot,
        TrxTestResultLimits limits,
        CancellationToken cancellationToken)
    {
        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedRoot));
            if (!Directory.Exists(root))
            {
                return DiscoveryResult.Failure(TrxTestEvidenceErrorKind.ResultsRootUnavailable);
            }

            var rootAttributes = File.GetAttributes(root);
            if (!rootAttributes.HasFlag(FileAttributes.Directory) ||
                rootAttributes.HasFlag(FileAttributes.ReparsePoint) ||
                rootAttributes.HasFlag(FileAttributes.Device))
            {
                return DiscoveryResult.Failure(TrxTestEvidenceErrorKind.UnsafeResultsRoot);
            }
        }
        catch (Exception exception) when (IsFilesystemException(exception))
        {
            return DiscoveryResult.Failure(TrxTestEvidenceErrorKind.ResultsRootUnavailable);
        }

        var files = new List<ResultFile>();
        var directories = new Stack<string>();
        directories.Push(root);
        var directoryCount = 0;
        long totalBytes = 0;
        var enumerationOptions = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        try
        {
            while (directories.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                directoryCount++;
                if (directoryCount > limits.MaximumDirectories)
                {
                    return DiscoveryResult.Failure(TrxTestEvidenceErrorKind.ResultFileLimitExceeded);
                }

                foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", enumerationOptions))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fullPath = Path.GetFullPath(entry);
                    if (!ProjectDiscovery.IsSafelyContained(root, fullPath))
                    {
                        return DiscoveryResult.Failure(TrxTestEvidenceErrorKind.UnsafeResultsEntry);
                    }

                    var attributes = File.GetAttributes(fullPath);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint) ||
                        attributes.HasFlag(FileAttributes.Device))
                    {
                        return DiscoveryResult.Failure(TrxTestEvidenceErrorKind.UnsafeResultsEntry);
                    }

                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        directories.Push(fullPath);
                        continue;
                    }

                    if (!Path.GetExtension(fullPath).Equals(".trx", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    files.Add(new ResultFile(fullPath, new FileInfo(fullPath).Length));
                    if (files.Count > limits.MaximumFiles)
                    {
                        return DiscoveryResult.Failure(TrxTestEvidenceErrorKind.ResultFileLimitExceeded);
                    }

                    var length = files[^1].Length;
                    if (length < 0 || length > limits.MaximumFileBytes)
                    {
                        return DiscoveryResult.Failure(TrxTestEvidenceErrorKind.ResultFileByteLimitExceeded);
                    }

                    totalBytes = checked(totalBytes + length);
                    if (totalBytes > limits.MaximumTotalBytes)
                    {
                        return DiscoveryResult.Failure(TrxTestEvidenceErrorKind.TotalByteLimitExceeded);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OverflowException)
        {
            return DiscoveryResult.Failure(TrxTestEvidenceErrorKind.TotalByteLimitExceeded);
        }
        catch (Exception exception) when (IsFilesystemException(exception))
        {
            return DiscoveryResult.Failure(TrxTestEvidenceErrorKind.ReadFailure);
        }

        files.Sort((left, right) => StringComparer.Ordinal.Compare(
            Path.GetRelativePath(root, left.Path).Replace(Path.DirectorySeparatorChar, '/'),
            Path.GetRelativePath(root, right.Path).Replace(Path.DirectorySeparatorChar, '/')));
        return DiscoveryResult.Success(files);
    }

    private static async Task<FileEvidence> ParseFileAsync(
        ResultFile resultFile,
        TrxTestResultLimits limits,
        int remainingTestBudget,
        int remainingFailureBudget,
        CancellationToken cancellationToken)
    {
        try
        {
            var attributes = File.GetAttributes(resultFile.Path);
            if (attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint) ||
                attributes.HasFlag(FileAttributes.Device))
            {
                return FileEvidence.Failure(TrxTestEvidenceErrorKind.UnsafeResultsEntry);
            }

            await using var stream = new FileStream(
                resultFile.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                StreamBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != resultFile.Length || stream.Length > limits.MaximumFileBytes)
            {
                return FileEvidence.Failure(TrxTestEvidenceErrorKind.ResultFileByteLimitExceeded);
            }

            var parser = new TrxDocumentParser(limits, remainingTestBudget, remainingFailureBudget);
            using var reader = XmlReader.Create(
                stream,
                new XmlReaderSettings
                {
                    Async = true,
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = limits.MaximumFileBytes,
                    MaxCharactersFromEntities = 0,
                    IgnoreComments = true,
                    IgnoreProcessingInstructions = true,
                });
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.Depth > limits.MaximumXmlDepth)
                {
                    return FileEvidence.Failure(TrxTestEvidenceErrorKind.XmlDepthLimitExceeded);
                }

                var error = parser.Process(reader);
                if (error is not null)
                {
                    return FileEvidence.Failure(error.Value);
                }
            }

            if (stream.Length != resultFile.Length)
            {
                return FileEvidence.Failure(TrxTestEvidenceErrorKind.ReadFailure);
            }

            return parser.Complete();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (XmlException)
        {
            return FileEvidence.Failure(TrxTestEvidenceErrorKind.MalformedXml);
        }
        catch (Exception exception) when (IsFilesystemException(exception))
        {
            return FileEvidence.Failure(TrxTestEvidenceErrorKind.ReadFailure);
        }
    }

    private static string[] SortAndDeduplicate(IEnumerable<string> identities) => identities
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static bool IsFilesystemException(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        System.Security.SecurityException or
        NotSupportedException;

    private sealed class TrxDocumentParser(
        TrxTestResultLimits limits,
        int remainingTestBudget,
        int remainingFailureBudget)
    {
        private readonly Stack<string> elements = new();
        private readonly List<FailureCandidate> failures = new(Math.Min(remainingFailureBudget, 32));
        private readonly HashSet<string> retainedFailureIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TestDefinition> definitions = new(StringComparer.OrdinalIgnoreCase);
        private bool rootSeen;
        private bool rootClosed;
        private bool resultsSeen;
        private bool summarySeen;
        private bool countersSeen;
        private bool partialResult;
        private string? summaryOutcome;
        private int total;
        private int passed;
        private int failed;
        private int skipped;
        private Counters? counters;
        private string? activeDefinitionId;

        public TrxTestEvidenceErrorKind? Process(XmlReader reader)
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    return ProcessStartElement(reader);
                case XmlNodeType.EndElement:
                    return ProcessEndElement(reader);
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                    if (elements.Count == 0 && !string.IsNullOrWhiteSpace(reader.Value))
                    {
                        return TrxTestEvidenceErrorKind.InvalidDocument;
                    }

                    break;
            }

            return null;
        }

        public FileEvidence Complete()
        {
            if (!rootSeen || !rootClosed || elements.Count != 0 || !resultsSeen || !summarySeen || !countersSeen || counters is null)
            {
                return FileEvidence.Failure(TrxTestEvidenceErrorKind.InvalidDocument);
            }

            if (partialResult || counters.Completed != 0 || counters.InProgress != 0 || counters.Pending != 0)
            {
                return FileEvidence.Failure(TrxTestEvidenceErrorKind.PartialResults);
            }

            var counterFailures = CheckedSum(
                counters.Failed,
                counters.Error,
                counters.Timeout,
                counters.Aborted,
                counters.PassedButRunAborted,
                counters.Disconnected,
                counters.Warning);
            var counterSkipped = CheckedSum(counters.Inconclusive, counters.NotRunnable, counters.NotExecuted);
            if (counterFailures is null || counterSkipped is null)
            {
                return FileEvidence.Failure(TrxTestEvidenceErrorKind.ContradictoryCounts);
            }

            var classified = CheckedSum(counters.Passed, counterFailures.Value, counterSkipped.Value);
            var expectedExecuted = counters.Total - counters.NotExecuted - counters.NotRunnable;
            if (classified is null ||
                counters.Total != classified.Value ||
                expectedExecuted < 0 ||
                counters.Executed != expectedExecuted ||
                total != counters.Total ||
                passed != counters.Passed ||
                failed != counterFailures.Value ||
                skipped != counterSkipped.Value ||
                total != passed + failed + skipped)
            {
                return FileEvidence.Failure(TrxTestEvidenceErrorKind.ContradictoryCounts);
            }

            if ((failed == 0 && summaryOutcome != "Completed") ||
                (failed > 0 && summaryOutcome != "Failed"))
            {
                return FileEvidence.Failure(TrxTestEvidenceErrorKind.ContradictoryCounts);
            }

            var identities = failures
                .Select(CreateStableIdentity)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (identities.Any(identity => identity.Length > limits.MaximumIdentityLength))
            {
                return FileEvidence.Failure(TrxTestEvidenceErrorKind.IdentityLengthLimitExceeded);
            }

            return new FileEvidence(total, passed, failed, skipped, identities, Error: null);
        }

        private TrxTestEvidenceErrorKind? ProcessStartElement(XmlReader reader)
        {
            var localName = reader.LocalName;
            if (elements.Count == 0)
            {
                if (rootSeen || rootClosed || reader.Depth != 0 || !localName.Equals("TestRun", StringComparison.Ordinal))
                {
                    return TrxTestEvidenceErrorKind.InvalidDocument;
                }

                rootSeen = true;
            }

            var parent = elements.TryPeek(out var parentName) ? parentName : null;
            TrxTestEvidenceErrorKind? error = null;
            if (elements.Count == 1 && localName.Equals("Results", StringComparison.Ordinal))
            {
                if (resultsSeen)
                {
                    return TrxTestEvidenceErrorKind.InvalidDocument;
                }

                resultsSeen = true;
            }
            else if (elements.Count == 2 &&
                     parent == "Results" &&
                     localName.Equals("UnitTestResult", StringComparison.Ordinal))
            {
                error = ProcessTestResult(reader);
            }
            else if (elements.Count == 1 && localName.Equals("ResultSummary", StringComparison.Ordinal))
            {
                if (summarySeen)
                {
                    return TrxTestEvidenceErrorKind.InvalidDocument;
                }

                summarySeen = true;
                summaryOutcome = reader.GetAttribute("outcome");
                if (summaryOutcome is not ("Completed" or "Failed"))
                {
                    return summaryOutcome is null
                        ? TrxTestEvidenceErrorKind.InvalidDocument
                        : TrxTestEvidenceErrorKind.PartialResults;
                }
            }
            else if (elements.Count == 2 &&
                     parent == "ResultSummary" &&
                     localName.Equals("Counters", StringComparison.Ordinal))
            {
                if (countersSeen)
                {
                    return TrxTestEvidenceErrorKind.InvalidDocument;
                }

                countersSeen = true;
                error = TryReadCounters(reader, out counters);
            }
            else if (elements.Count == 2 &&
                     parent == "TestDefinitions" &&
                     localName.Equals("UnitTest", StringComparison.Ordinal))
            {
                activeDefinitionId = reader.GetAttribute("id");
                if (activeDefinitionId is { Length: > 0 } id && id.Length > limits.MaximumIdentityLength)
                {
                    error = TrxTestEvidenceErrorKind.IdentityLengthLimitExceeded;
                }
            }
            else if (elements.Count == 3 &&
                     parent == "UnitTest" &&
                     localName.Equals("TestMethod", StringComparison.Ordinal) &&
                     activeDefinitionId is { } definitionId &&
                     retainedFailureIds.Contains(definitionId))
            {
                error = CaptureDefinition(reader, definitionId);
            }

            if (error is not null)
            {
                return error;
            }

            if (!reader.IsEmptyElement)
            {
                elements.Push(localName);
            }
            else if (elements.Count == 0)
            {
                rootClosed = true;
            }
            else if (localName == "UnitTest")
            {
                activeDefinitionId = null;
            }

            return null;
        }

        private TrxTestEvidenceErrorKind? ProcessEndElement(XmlReader reader)
        {
            if (!elements.TryPop(out var expected) || !expected.Equals(reader.LocalName, StringComparison.Ordinal))
            {
                return TrxTestEvidenceErrorKind.InvalidDocument;
            }

            if (expected == "UnitTest")
            {
                activeDefinitionId = null;
            }

            if (elements.Count == 0)
            {
                rootClosed = true;
            }

            return null;
        }

        private TrxTestEvidenceErrorKind? ProcessTestResult(XmlReader reader)
        {
            if (total >= remainingTestBudget)
            {
                return TrxTestEvidenceErrorKind.TestCountLimitExceeded;
            }

            total++;
            var outcome = reader.GetAttribute("outcome");
            if (string.IsNullOrWhiteSpace(outcome))
            {
                return TrxTestEvidenceErrorKind.InvalidDocument;
            }

            switch (outcome)
            {
                case "Passed":
                    passed++;
                    return null;
                case "Failed":
                case "Error":
                case "Timeout":
                case "Aborted":
                case "PassedButRunAborted":
                case "Disconnected":
                case "Warning":
                    failed++;
                    return CaptureFailure(reader);
                case "Inconclusive":
                case "NotRunnable":
                case "NotExecuted":
                    skipped++;
                    return null;
                case "Completed":
                case "InProgress":
                case "Pending":
                    partialResult = true;
                    skipped++;
                    return null;
                default:
                    return TrxTestEvidenceErrorKind.InvalidDocument;
            }
        }

        private TrxTestEvidenceErrorKind? CaptureFailure(XmlReader reader)
        {
            var testId = reader.GetAttribute("testId")?.Trim() ?? string.Empty;
            var testName = reader.GetAttribute("testName")?.Trim() ?? string.Empty;
            if (testId.Length > limits.MaximumIdentityLength || testName.Length > limits.MaximumIdentityLength)
            {
                return TrxTestEvidenceErrorKind.IdentityLengthLimitExceeded;
            }

            if (testId.Length == 0 && testName.Length == 0)
            {
                return TrxTestEvidenceErrorKind.InvalidDocument;
            }

            if (failures.Count < remainingFailureBudget)
            {
                failures.Add(new FailureCandidate(testId, testName));
                if (testId.Length > 0)
                {
                    retainedFailureIds.Add(testId);
                }
            }

            return null;
        }

        private TrxTestEvidenceErrorKind? CaptureDefinition(XmlReader reader, string definitionId)
        {
            var className = reader.GetAttribute("className")?.Trim() ?? string.Empty;
            var methodName = reader.GetAttribute("name")?.Trim() ?? string.Empty;
            if (className.Length > limits.MaximumIdentityLength || methodName.Length > limits.MaximumIdentityLength)
            {
                return TrxTestEvidenceErrorKind.IdentityLengthLimitExceeded;
            }

            if (!IsSafeReadableIdentityPart(className) || !IsSafeReadableIdentityPart(methodName))
            {
                return null;
            }

            var combined = $"{className}.{methodName}";
            if (combined.Length <= limits.MaximumIdentityLength)
            {
                var definition = new TestDefinition(className, methodName);
                if (definitions.TryGetValue(definitionId, out var existing) && existing != definition)
                {
                    return TrxTestEvidenceErrorKind.InvalidDocument;
                }

                definitions[definitionId] = definition;
            }

            return null;
        }

        private string CreateStableIdentity(FailureCandidate candidate)
        {
            if (candidate.TestId.Length > 0 &&
                definitions.TryGetValue(candidate.TestId, out var definition))
            {
                return $"{definition.ClassName}.{definition.MethodName}";
            }

            if (Guid.TryParse(candidate.TestId, out var testId))
            {
                return $"test:{testId:D}";
            }

            var source = candidate.TestId.Length > 0 ? candidate.TestId : candidate.TestName;
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(source.Normalize(NormalizationForm.FormC)));
            return $"test-sha256:{Convert.ToHexString(digest).ToLowerInvariant()}";
        }

        private TrxTestEvidenceErrorKind? TryReadCounters(XmlReader reader, out Counters? value)
        {
            value = null;
            if (!TryReadRequiredCounter(reader, "total", out var totalCount) ||
                !TryReadRequiredCounter(reader, "executed", out var executedCount) ||
                !TryReadRequiredCounter(reader, "passed", out var passedCount) ||
                !TryReadRequiredCounter(reader, "failed", out var failedCount) ||
                !TryReadOptionalCounter(reader, "error", out var errorCount) ||
                !TryReadOptionalCounter(reader, "timeout", out var timeoutCount) ||
                !TryReadOptionalCounter(reader, "aborted", out var abortedCount) ||
                !TryReadOptionalCounter(reader, "inconclusive", out var inconclusiveCount) ||
                !TryReadOptionalCounter(reader, "passedButRunAborted", out var passedButRunAbortedCount) ||
                !TryReadOptionalCounter(reader, "notRunnable", out var notRunnableCount) ||
                !TryReadOptionalCounter(reader, "notExecuted", out var notExecutedCount) ||
                !TryReadOptionalCounter(reader, "disconnected", out var disconnectedCount) ||
                !TryReadOptionalCounter(reader, "warning", out var warningCount) ||
                !TryReadOptionalCounter(reader, "completed", out var completedCount) ||
                !TryReadOptionalCounter(reader, "inProgress", out var inProgressCount) ||
                !TryReadOptionalCounter(reader, "pending", out var pendingCount))
            {
                return TrxTestEvidenceErrorKind.InvalidDocument;
            }

            if (totalCount > remainingTestBudget)
            {
                return TrxTestEvidenceErrorKind.TestCountLimitExceeded;
            }

            value = new Counters(
                totalCount,
                executedCount,
                passedCount,
                failedCount,
                errorCount,
                timeoutCount,
                abortedCount,
                inconclusiveCount,
                passedButRunAbortedCount,
                notRunnableCount,
                notExecutedCount,
                disconnectedCount,
                warningCount,
                completedCount,
                inProgressCount,
                pendingCount);
            return null;
        }

        private bool TryReadRequiredCounter(XmlReader reader, string name, out int value)
        {
            var text = reader.GetAttribute(name);
            return TryParseCounter(text, out value);
        }

        private bool TryReadOptionalCounter(XmlReader reader, string name, out int value)
        {
            var text = reader.GetAttribute(name);
            if (text is null)
            {
                value = 0;
                return true;
            }

            return TryParseCounter(text, out value);
        }

        private bool TryParseCounter(string? text, out int value) =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
            value >= 0 &&
            value <= limits.MaximumTestCount;

        private static int? CheckedSum(params int[] values)
        {
            try
            {
                var sum = 0;
                foreach (var value in values)
                {
                    sum = checked(sum + value);
                }

                return sum;
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        private static bool IsSafeReadableIdentityPart(string value) =>
            value.Length > 0 &&
            !value.Any(char.IsControl) &&
            !Path.IsPathFullyQualified(value) &&
            !value.Contains('/') &&
            !value.Contains('\\') &&
            !value.Contains("://", StringComparison.Ordinal);
    }

    private sealed record DiscoveryResult(
        IReadOnlyList<ResultFile>? Files,
        TrxTestEvidenceErrorKind? Error)
    {
        public static DiscoveryResult Success(IReadOnlyList<ResultFile> files) => new(files, null);

        public static DiscoveryResult Failure(TrxTestEvidenceErrorKind error) => new(null, error);
    }

    private sealed record ResultFile(string Path, long Length);

    private sealed record FileEvidence(
        int Total,
        int Passed,
        int Failed,
        int Skipped,
        IReadOnlyList<string> FailedIdentities,
        TrxTestEvidenceErrorKind? Error)
    {
        public static FileEvidence Failure(TrxTestEvidenceErrorKind error) =>
            new(0, 0, 0, 0, Array.Empty<string>(), error);
    }

    private sealed record FailureCandidate(string TestId, string TestName);

    private sealed record TestDefinition(string ClassName, string MethodName);

    private sealed record Counters(
        int Total,
        int Executed,
        int Passed,
        int Failed,
        int Error,
        int Timeout,
        int Aborted,
        int Inconclusive,
        int PassedButRunAborted,
        int NotRunnable,
        int NotExecuted,
        int Disconnected,
        int Warning,
        int Completed,
        int InProgress,
        int Pending);
}
