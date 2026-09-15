using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ghpmv.Core.GitHub;

namespace Ghpmv.Cli;

internal sealed class ImportFailureDiagnostics
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

    public ImportFailureDiagnostics(
        string targetOwner,
        string ownerType,
        int? requestedTargetProjectNumber,
        bool browserAutomationEnabled)
    {
        _targetOwner = targetOwner;
        _ownerType = ownerType;
        _requestedTargetProjectNumber = requestedTargetProjectNumber;
        _browserAutomationEnabled = browserAutomationEnabled;
        _targetProjectNumber = requestedTargetProjectNumber;
    }

    public void SetStage(string stage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        lock (_sync)
        {
            _stage = stage;
        }
    }

    public void SetTargetProject(int projectNumber, string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        lock (_sync)
        {
            _targetProjectNumber = projectNumber;
            _targetProjectUrl = url;
        }
    }

    public void WriteProgress(string message)
    {
        Console.Error.WriteLine(message);

        lock (_sync)
        {
            if (_progress.Count == MaximumProgressEntries)
            {
                _progress.Dequeue();
            }

            _progress.Enqueue(new ImportProgressEntry
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Message = message,
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

        ImportProgressEntry[] progress;
        int? targetProjectNumber;
        string? targetProjectUrl;
        string stage;
        lock (_sync)
        {
            progress = [.. _progress];
            targetProjectNumber = _targetProjectNumber;
            targetProjectUrl = _targetProjectUrl;
            stage = _stage;
        }

        var report = new ImportFailureReport
        {
            OccurredAtUtc = DateTimeOffset.UtcNow,
            Command = "import",
            TargetOwner = _targetOwner,
            OwnerType = _ownerType,
            RequestedTargetProjectNumber = _requestedTargetProjectNumber,
            TargetProjectNumber = targetProjectNumber,
            TargetProjectUrl = targetProjectUrl,
            BrowserAutomationEnabled = _browserAutomationEnabled,
            Stage = stage,
            Progress = progress,
            Exceptions = DescribeExceptions(exception),
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
        var httpException = exception as HttpRequestException;
        details.Add(new ImportExceptionDetail
        {
            Depth = depth,
            Type = exception.GetType().FullName ?? exception.GetType().Name,
            Message = graphQlException is null
                ? exception.Message
                : "GitHub GraphQL request failed. See the command's stderr output for the server response.",
            StackTrace = exception.StackTrace,
            ErrorType = graphQlException?.ErrorType,
            StatusCode = FormatStatusCode(graphQlException?.StatusCode ?? httpException?.StatusCode),
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

    private static string? FormatStatusCode(HttpStatusCode? statusCode) =>
        statusCode is null
            ? null
            : $"{(int)statusCode.Value} {statusCode.Value}";
}

internal sealed record ImportFailureReport
{
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

    public required ImportExceptionDetail[] Exceptions { get; init; }
}

internal sealed record ImportProgressEntry
{
    public required DateTimeOffset TimestampUtc { get; init; }

    public required string Message { get; init; }
}

internal sealed record ImportExceptionDetail
{
    public required int Depth { get; init; }

    public required string Type { get; init; }

    public required string Message { get; init; }

    public string? StackTrace { get; init; }

    public string? ErrorType { get; init; }

    public string? StatusCode { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ImportFailureReport))]
internal sealed partial class ImportFailureJsonContext : JsonSerializerContext;
