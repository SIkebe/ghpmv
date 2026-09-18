using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ghpmv.Core.GitHub;

/// <summary>
/// An explicitly enabled, invocation-owned sink for failed API response bodies only.
/// Never attach this sink based on saved configuration or environment variables.
/// </summary>
public sealed class SensitiveApiDiagnostics : IDisposable
{
    public const int MaximumBodyCharacters = 65_536;
    private readonly Lock _sync = new();
    private readonly Action<string> _warning;
    private FileStream? _stream;
    private bool _disposed;
    private bool _failed;

    public SensitiveApiDiagnostics(string directory, Action<string> warning)
        : this(directory, warning, null)
    {
    }

    public SensitiveApiDiagnostics(string directory, Action<string> warning, ApiDiagnosticSession? session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(warning);
        FilePath = Path.GetFullPath(Path.Combine(directory, $"ghpmv-sensitive-api-{Guid.NewGuid():N}.jsonl"));
        _warning = warning;
        Session = session ?? new ApiDiagnosticSession();
        if (Session.SensitiveDiagnostics is not null)
        {
            throw new ArgumentException("A diagnostic session can own only one sensitive sink.", nameof(session));
        }
        Session.SensitiveDiagnostics = this;
    }

    public string FilePath { get; }
    public ApiDiagnosticSession Session { get; }
    internal Func<string, FileStreamOptions, FileStream> OpenFile { get; init; } = static (path, options) => new(path, options);
    internal string? CreatedFilePath { get { lock (_sync) return _stream is null ? null : FilePath; } }
    internal string CaptureState
    {
        get { lock (_sync) return _failed ? (_stream is null ? "unavailable" : "partial") : (_stream is null ? "not-created" : "available"); }
    }

    internal void Record(ApiOperation operation, HttpStatusCode status, string? requestId, int retryCount, string body,
        ApiRequestAttempt? attempt = null)
    {
        attempt ??= Session.BeginAttempt();
        lock (_sync)
        {
            if (_disposed)
            {
                attempt.SensitiveResponseCapture = "failed";
                throw ApiDiagnosticSession.Attach(new ObjectDisposedException(nameof(SensitiveApiDiagnostics)), attempt);
            }
            if (_failed)
            {
                attempt.SensitiveResponseCapture = "failed";
                throw ApiDiagnosticSession.Attach(WriteFailure(attempt), attempt);
            }

            try
            {
                if (_stream is null)
                {
                    _warning($"warning: SENSITIVE API diagnostics will be written to {FilePath}. Failed response bodies may contain secrets or private data; review before sharing.");
                    var options = new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.None,
                        Options = FileOptions.WriteThrough,
                    };
                    if (!OperatingSystem.IsWindows())
                    {
                        options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    }

                    _stream = OpenFile(FilePath, options);
                }

                var length = Math.Min(body.Length, MaximumBodyCharacters);
                if (length < body.Length && length > 0 && char.IsHighSurrogate(body[length - 1]))
                {
                    length--;
                }

                var entry = new SensitiveApiResponse
                {
                    RunId = attempt.RunId,
                    AttemptId = attempt.AttemptId,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Operation = operation.ToString(),
                    StatusCode = (int)status,
                    RequestId = GraphQLDiagnosticSanitizer.RequestId(requestId),
                    RetryCount = retryCount,
                    OriginalBodyCharacters = body.Length,
                    Truncated = length < body.Length,
                    Body = body[..length],
                };
                JsonSerializer.Serialize(_stream, entry, SensitiveApiJsonContext.Default.SensitiveApiResponse);
                _stream.WriteByte((byte)'\n');
                _stream.Flush(flushToDisk: true);
                attempt.SensitiveResponseCapture = "captured";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _failed = true;
                attempt.SensitiveResponseCapture = "failed";
                throw ApiDiagnosticSession.Attach(WriteFailure(attempt), attempt);
            }
        }
    }

    private IOException WriteFailure(ApiRequestAttempt? attempt = null)
    {
        var message = $"Sensitive API diagnostic file could not be written: {FilePath}. No response body was redirected to ordinary logs. The file may be incomplete; inspect target state before retrying mutations.";
        if (attempt is not null)
        {
            message += $" (runId {attempt.RunId}, attemptId {attempt.AttemptId}, sensitiveResponseCapture {attempt.SensitiveResponseCapture})";
        }
        _warning($"error: {message}");
        return new IOException(message);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _stream?.Dispose();
            }
            catch (IOException)
            {
                _failed = true;
                throw WriteFailure();
            }
        }
    }
}

internal enum ApiOperation
{
    GraphQlQuery,
    GraphQlMutation,
    RestGet,
    RestPost,
    RestPut,
    RestDelete,
    RestValidationProbe,
}

internal sealed record SensitiveApiResponse
{
    public required string RunId { get; init; }
    public required string AttemptId { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
    public required string Operation { get; init; }
    public required int StatusCode { get; init; }
    public string? RequestId { get; init; }
    public required int RetryCount { get; init; }
    public required int OriginalBodyCharacters { get; init; }
    public required bool Truncated { get; init; }
    public required string Body { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SensitiveApiResponse))]
internal sealed partial class SensitiveApiJsonContext : JsonSerializerContext;
