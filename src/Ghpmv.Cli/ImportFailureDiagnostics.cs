using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Cli;

internal sealed class ImportFailureDiagnostics : IDisposable
{
    public const string FileName = "import-error.json";

    private const int MaximumProgressEntries = 200;
    private readonly Lock _sync = new();
    private readonly Queue<ImportProgressEntry> _progress = new();
    private readonly string _targetOwner;
    private readonly string _ownerType;
    private readonly int? _requestedTargetProjectNumber;
    private readonly bool _browserAutomationEnabled;
    private int? _targetProjectNumber;
    private string? _targetProjectUrl;
    private string _stage = "initializing";
    private string? _failureStage;
    private readonly List<ImportCleanupFailure> _cleanupFailures = [];
    private MigrationProjectIdentity? _source;
    private string? _targetTitle;
    private string? _targetId;
    private string? _targetHost;
    private IDisposable? _scope;
    private readonly ApiDiagnosticSession _apiDiagnostics;

    public void SetSnapshot(ProjectSnapshot snapshot, string? requestedTitle = null)
    {
        _source = MigrationDiagnostics.Source(snapshot);
        _targetTitle = _requestedTargetProjectNumber is null ? requestedTitle ?? snapshot.Project.Title : null;
    }

    public void SetTargetIdentity(MigrationProjectIdentity identity)
    {
        _targetId = identity.Id;
        _targetTitle = identity.Title;
        _targetHost = identity.Host;
    }

    private MigrationDiagnosticContext Context(string? stage = null) => new()
    {
        Source = _source,
        Target = new()
        {
            Owner = _targetOwner,
            OwnerType = _ownerType,
            Number = _targetProjectNumber ?? _requestedTargetProjectNumber,
            Title = _targetTitle,
            Id = _targetId,
            Host = _targetHost,
        },
        Stage = stage ?? _failureStage ?? _stage,
    };

    public void AttachFailure(Exception exception) => MigrationDiagnostics.Attach(exception, Context());

    public IDisposable BeginCleanup(string stage, string operation) =>
        MigrationDiagnostics.Begin(Context(stage) with
        {
            Operation = operation,
            Element = new() { Kind = "Project", TargetId = _targetId },
        });

    public void WriteFailure(Exception exception, Action<string>? writeError = null)
    {
        AttachFailure(exception);
        var write = writeError ?? Console.Error.WriteLine;
        write($"error: {FormatExceptionForReport(exception)}");
        foreach (var line in MigrationDiagnostics.Lines(MigrationDiagnostics.Get(exception)!)) write(line);
        var details = DescribeExceptions(exception);
        var transport = details.FirstOrDefault(detail => detail.StatusCode is not null || detail.ErrorType is not null);
        if (transport?.StatusCode is { } status) write($"httpStatus: {status}");
        if (transport?.ErrorType is { } errorType) write($"errorCode: {errorType}");
        if (transport?.RequestId is { } requestId) write($"requestId: {requestId}");
        write($"runId: {_apiDiagnostics.RunId}");
        if (details.FirstOrDefault(detail => detail.AttemptId is not null) is { } correlated)
        {
            write($"attemptId: {correlated.AttemptId}");
            write($"sensitiveResponseCapture: {correlated.SensitiveResponseCapture}");
        }
        if (_apiDiagnostics.SensitiveDiagnosticsFile is { } file) write($"sensitiveDiagnosticsFile: {file}");
        write($"sensitiveDiagnosticsState: {_apiDiagnostics.SensitiveDiagnosticsState}");
        if (details.FirstOrDefault(detail => detail.RecoveryHint is not null)?.RecoveryHint is { } hint) write(hint);
    }

    public void Dispose() => _scope?.Dispose();

    public async Task<int> ReportEarlyFailureAsync(string directory, Exception exception)
    {
        CaptureFailureStage();
        WriteFailure(exception);
        await new ImportFailureFinalizer(this, directory).CompleteAsync(exception, null, null).ConfigureAwait(false);
        return 1;
    }

    public ImportFailureDiagnostics(
        string targetOwner,
        string ownerType,
        int? requestedTargetProjectNumber,
        bool browserAutomationEnabled,
        ApiDiagnosticSession? apiDiagnostics = null)
    {
        _targetOwner = targetOwner;
        _ownerType = ownerType;
        _requestedTargetProjectNumber = requestedTargetProjectNumber;
        _browserAutomationEnabled = browserAutomationEnabled;
        _apiDiagnostics = apiDiagnostics ?? new ApiDiagnosticSession();
    }

    public void SetStage(string stage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        lock (_sync)
        {
            _stage = stage;
            _scope?.Dispose();
            _scope = MigrationDiagnostics.Begin(Context(stage));
        }
    }

    public void SetTargetProject(int projectNumber, string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        lock (_sync)
        {
            _targetProjectNumber = projectNumber;
            _targetProjectUrl = MigrationDiagnostics.SafeUrl(url);
        }
    }

    public void CaptureFailureStage()
    {
        lock (_sync)
        {
            _failureStage ??= _stage;
        }
    }

    public void RecordCleanupFailure(string stage, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentNullException.ThrowIfNull(exception);

        lock (_sync)
        {
            _failureStage ??= stage;
            MigrationDiagnostics.Attach(exception, Context(stage) with
            {
                Operation = stage,
                Element = new()
                {
                    Kind = stage switch
                    {
                        "disposing-browser-session" => "BrowserSession",
                        "disposing-api-diagnostics" => "DiagnosticFile",
                        _ => "Project",
                    },
                    TargetId = stage is "disposing-browser-session" or "disposing-api-diagnostics" ? null : _targetId,
                },
            });
            _cleanupFailures.Add(new ImportCleanupFailure
            {
                Stage = stage,
                Type = exception.GetType().FullName ?? exception.GetType().Name,
                Message = FormatExceptionForReport(exception),
                Context = MigrationDiagnostics.Get(exception),
                Exceptions = DescribeExceptions(exception),
            });
        }
    }

    public void WriteProgress(string message)
    {
        WriteProgress(message, message);
    }

    public void WriteProgress(string consoleMessage, string diagnosticMessage)
    {
        Console.Error.WriteLine(MigrationDiagnostics.Text(consoleMessage, 4096));
        RecordProgress(diagnosticMessage);
    }

    internal void RecordProgress(string message)
    {
        lock (_sync)
        {
            if (_progress.Count == MaximumProgressEntries)
            {
                _progress.Dequeue();
            }

            _progress.Enqueue(new ImportProgressEntry
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Message = SanitizePersistedMessage(message),
                Context = MigrationDiagnostics.Current,
            });
        }
    }

    public async Task<string> SaveFailureAsync(
        string directory,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(exception);
        if (MigrationDiagnostics.Get(exception) is null) AttachFailure(exception);

        ImportProgressEntry[] progress;
        int? targetProjectNumber;
        string? targetProjectUrl;
        string stage;
        ImportCleanupFailure[] cleanupFailures;
        lock (_sync)
        {
            progress = [.. _progress];
            targetProjectNumber = _targetProjectNumber;
            targetProjectUrl = _targetProjectUrl;
            stage = _failureStage ?? _stage;
            cleanupFailures = [.. _cleanupFailures];
        }

        var report = new ImportFailureReport
        {
            RunId = _apiDiagnostics.RunId,
            SensitiveDiagnosticsFile = _apiDiagnostics.SensitiveDiagnosticsFile,
            SensitiveDiagnosticsState = _apiDiagnostics.SensitiveDiagnosticsState,
            OccurredAtUtc = DateTimeOffset.UtcNow,
            Command = "import",
            TargetOwner = MigrationDiagnostics.Text(_targetOwner)!,
            OwnerType = MigrationDiagnostics.Text(_ownerType)!,
            RequestedTargetProjectNumber = _requestedTargetProjectNumber,
            TargetProjectNumber = targetProjectNumber,
            TargetProjectUrl = targetProjectUrl,
            BrowserAutomationEnabled = _browserAutomationEnabled,
            Stage = stage,
            Progress = progress,
            CleanupFailures = cleanupFailures,
            Exceptions = DescribeExceptions(exception),
            Context = MigrationDiagnostics.Get(exception),
        };

        var path = Path.Combine(directory, FileName);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    report,
                    ImportFailureJsonContext.Default.ImportFailureReport,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }

        return path;
    }

    public void DeletePreviousFailure(string directory)
    {
        var path = Path.Combine(directory, FileName);
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            WriteProgress($"warning: failed to remove stale {FileName}: {exception.Message}");
        }
    }

    public static bool CanDeletePreviousFailure(
        ProjectImportLog projectLog,
        bool hasCurrentWarnings)
    {
        ArgumentNullException.ThrowIfNull(projectLog);
        return !hasCurrentWarnings
            && projectLog.HasUnresolvedWarnings is not true
            && (projectLog.CreatedProjectId is null || projectLog.ImportCompleted is true);
    }

    private static ImportExceptionDetail[] DescribeExceptions(Exception exception)
    {
        var details = new List<ImportExceptionDetail>();
        AddException(exception, depth: 0, details);
        return [.. details];
    }

    private static void AddException(
        Exception exception,
        int depth,
        List<ImportExceptionDetail> details)
    {
        var graphQlException = exception as GitHubGraphQLException;
        var ambiguousException = exception as AmbiguousMutationResultException;
        var httpException = exception as HttpRequestException;
        var restDiagnostic = GitHubRestClient.GetFailureDiagnostic(exception);
        var attempt = ApiDiagnosticSession.GetAttempt(exception);
        details.Add(new ImportExceptionDetail
        {
            RunId = attempt?.RunId,
            AttemptId = attempt?.AttemptId,
            SensitiveResponseCapture = attempt?.SensitiveResponseCapture,
            Depth = depth,
            Context = MigrationDiagnostics.Get(exception),
            Type = exception.GetType().FullName ?? exception.GetType().Name,
            Message = FormatExceptionForReport(exception),
            StackTrace = exception.StackTrace,
            ErrorType = GraphQLDiagnosticSanitizer.ErrorType(graphQlException?.ErrorType),
            StatusCode = FormatStatusCode(graphQlException?.StatusCode ?? httpException?.StatusCode ?? restDiagnostic?.StatusCode),
            RequestId = GraphQLDiagnosticSanitizer.RequestId(graphQlException?.RequestId ?? restDiagnostic?.RequestId),
            FailureReason = graphQlException?.FailureReason ?? restDiagnostic?.FailureReason,
            OperationKind = graphQlException?.OperationKind ?? restDiagnostic?.Operation,
            RetryCount = graphQlException?.RetryCount ?? restDiagnostic?.RetryCount,
            InputValidation = graphQlException?.InputValidation,
            GraphQlErrors = graphQlException?.GraphQlErrors ?? [],
            OperationName = ambiguousException?.OperationName,
            ClientMutationId = ambiguousException?.ClientMutationId,
            AttemptedAtUtc = ambiguousException?.AttemptedAt,
            Target = MigrationDiagnostics.Text(ambiguousException?.Target),
            RecoveryHint = ambiguousException is null
                ? null
                : AmbiguousMutationResultException.RecoveryHint,
        });

        if (exception is AggregateException aggregateException)
        {
            foreach (var innerException in aggregateException.InnerExceptions)
            {
                AddException(innerException, depth + 1, details);
            }
        }
        else if (exception.InnerException is not null)
        {
            AddException(exception.InnerException, depth + 1, details);
        }
    }

    public static string FormatExceptionForReport(Exception exception) =>
        exception switch
        {
            GitHubGraphQLException graphQl =>
                MigrationDiagnostics.ApiFailureSummary(graphQl),
            AggregateException =>
                "Multiple related failures occurred. See the nested exception entries for sanitized details.",
            HttpRequestException http when GitHubRestClient.GetFailureDiagnostic(http) is not null => http.Message,
            Microsoft.Playwright.PlaywrightException or HttpRequestException or TimeoutException =>
                $"{exception.GetType().Name}: migration operation failed.",
            _ when exception.InnerException is not null =>
                $"{exception.GetType().Name}: migration operation failed; see nested exception details.",
            OperationCanceledException => "Migration was canceled.",
            ArgumentException or InvalidOperationException or IOException or InvalidDataException
                or UnauthorizedAccessException or FormatException or JsonException or KeyNotFoundException =>
                SanitizePersistedMessage(exception.Message),
            _ => $"{exception.GetType().Name}: migration operation failed.",
        };

    private static string SanitizePersistedMessage(string message)
    {
        const string marker = "GraphQL error:";
        var markerIndex = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return markerIndex < 0
            ? MigrationDiagnostics.Text(message, 4096)!
            : MigrationDiagnostics.Text(message[..markerIndex], 4096) + "GitHub GraphQL request failed.";
    }

    private static string? FormatStatusCode(HttpStatusCode? statusCode) =>
        statusCode is null
            ? null
            : $"{(int)statusCode.Value} {statusCode.Value}";
}

internal sealed record ImportFailureReport
{
    public string? RunId { get; init; }
    public string? SensitiveDiagnosticsFile { get; init; }
    public string? SensitiveDiagnosticsState { get; init; }
    public MigrationDiagnosticContext? Context { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }

    public required string Command { get; init; }

    public required string TargetOwner { get; init; }

    public required string OwnerType { get; init; }

    public int? RequestedTargetProjectNumber { get; init; }

    public int? TargetProjectNumber { get; init; }

    public string? TargetProjectUrl { get; init; }

    public required bool BrowserAutomationEnabled { get; init; }

    public required string Stage { get; init; }

    public required ImportProgressEntry[] Progress { get; init; }

    public required ImportCleanupFailure[] CleanupFailures { get; init; }

    public required ImportExceptionDetail[] Exceptions { get; init; }
}

internal sealed record ImportProgressEntry
{
    public MigrationDiagnosticContext? Context { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }

    public required string Message { get; init; }
}

internal sealed record ImportExceptionDetail
{
    public string? RunId { get; init; }
    public string? AttemptId { get; init; }
    public string? SensitiveResponseCapture { get; init; }
    public MigrationDiagnosticContext? Context { get; init; }
    public required int Depth { get; init; }

    public required string Type { get; init; }

    public required string Message { get; init; }

    public string? StackTrace { get; init; }

    public string? ErrorType { get; init; }

    public string? StatusCode { get; init; }

    public string? RequestId { get; init; }

    public string? FailureReason { get; init; }

    public string? OperationKind { get; init; }

    public int? RetryCount { get; init; }

    public string? InputValidation { get; init; }

    public IReadOnlyList<GraphQLErrorDiagnostic> GraphQlErrors { get; init; } = [];

    public string? OperationName { get; init; }

    public string? ClientMutationId { get; init; }

    public DateTimeOffset? AttemptedAtUtc { get; init; }

    public string? Target { get; init; }

    public string? RecoveryHint { get; init; }
}

internal sealed record ImportCleanupFailure
{
    public ImportExceptionDetail[] Exceptions { get; init; } = [];
    public MigrationDiagnosticContext? Context { get; init; }
    public required string Stage { get; init; }

    public required string Type { get; init; }

    public required string Message { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ImportFailureReport))]
internal sealed partial class ImportFailureJsonContext : JsonSerializerContext;
