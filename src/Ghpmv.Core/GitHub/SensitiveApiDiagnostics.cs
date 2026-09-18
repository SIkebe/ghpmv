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
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(warning);
        FilePath = Path.GetFullPath(Path.Combine(directory, $"ghpmv-sensitive-api-{Guid.NewGuid():N}.jsonl"));
        _warning = warning;
    }

    public string FilePath { get; }

    internal void Record(ApiOperation operation, HttpStatusCode status, string? requestId, int retryCount, string body)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_failed)
            {
                throw WriteFailure();
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

                    _stream = new FileStream(FilePath, options);
                }

                var length = Math.Min(body.Length, MaximumBodyCharacters);
                if (length < body.Length && length > 0 && char.IsHighSurrogate(body[length - 1]))
                {
                    length--;
                }

                var entry = new SensitiveApiResponse
                {
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
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _failed = true;
                throw WriteFailure();
            }
        }
    }

    private IOException WriteFailure()
    {
        var message = $"Sensitive API diagnostic file could not be written: {FilePath}. No response body was redirected to ordinary logs. The file may be incomplete; inspect target state before retrying mutations.";
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
