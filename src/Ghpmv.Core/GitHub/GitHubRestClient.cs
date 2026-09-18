using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Ghpmv.Core.GitHub;

/// <summary>Minimal GitHub REST client used by fixture setup for repository/Issue/PR bootstrapping.</summary>
public sealed class GitHubRestClient : IDisposable
{
    private static readonly Uri DefaultBaseUri = new("https://api.github.com/");
    private static readonly ConditionalWeakTable<Exception, GitHubRestFailureDiagnostic> Failures = new();
    private readonly ConditionalWeakTable<HttpResponseMessage, ApiRequestAttempt> _attempts = new();

    private readonly HttpClient _httpClient;

    public GitHubRestClient(string token, Uri? baseUri = null)
        : this(token, baseUri, new HttpClientHandler())
    {
    }

    internal GitHubRestClient(string token, Uri? baseUri, HttpMessageHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(handler);
        _httpClient = new HttpClient(handler) { BaseAddress = EnsureTrailingSlash(baseUri ?? DefaultBaseUri) };
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ghpmv");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    internal Uri BaseUri => _httpClient.BaseAddress!;

    /// <summary>Optional invocation-owned sink. The caller must dispose it.</summary>
    public SensitiveApiDiagnostics? SensitiveDiagnostics
    {
        get => DiagnosticSession.SensitiveDiagnostics;
        init { if (value is not null) DiagnosticSession = value.Session; }
    }

    public ApiDiagnosticSession DiagnosticSession { get; init; } = new();

    public static GitHubRestFailureDiagnostic? GetFailureDiagnostic(HttpRequestException exception) =>
        GetFailureDiagnostic((Exception)exception);

    public static GitHubRestFailureDiagnostic? GetFailureDiagnostic(Exception exception) =>
        Failures.TryGetValue(exception, out var diagnostic) ? diagnostic : null;

    public static Uri ToRestBaseUri(Uri graphQlEndpoint)
    {
        ArgumentNullException.ThrowIfNull(graphQlEndpoint);
        return new Uri(graphQlEndpoint, ".");
    }

    private static Uri EnsureTrailingSlash(Uri baseUri)
    {
        if (baseUri.AbsolutePath.EndsWith('/'))
        {
            return baseUri;
        }

        var builder = new UriBuilder(baseUri)
        {
            Path = baseUri.AbsolutePath + "/",
        };
        return builder.Uri;
    }

    public async Task<JsonElement?> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await SendAsync(request, ApiOperation.RestGet, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            await CaptureFailureAsync(response, ApiOperation.RestGet, cancellationToken).ConfigureAwait(false);
            return null;
        }

        return await ReadJsonAsync(response, ApiOperation.RestGet, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> PostAsync(string path, object body, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = CreateJsonContent(body) };
        using var response = await SendAsync(request, ApiOperation.RestPost, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(response, ApiOperation.RestPost, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> PutAsync(string path, object body, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = CreateJsonContent(body) };
        using var response = await SendAsync(request, ApiOperation.RestPut, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(response, ApiOperation.RestPut, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitHubRestProbeResponse> PostValidationProbeAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2026-03-10");
        using var response = await SendAsync(request, ApiOperation.RestValidationProbe, cancellationToken).ConfigureAwait(false);
        var text = await ReadBodyAsync(response, ApiOperation.RestValidationProbe, cancellationToken).ConfigureAwait(false);
        CaptureFailure(response, ApiOperation.RestValidationProbe, text);
        response.Headers.TryGetValues("X-Accepted-GitHub-Permissions", out var acceptedPermissions);
        var permissions = acceptedPermissions is null ? null : string.Join(",", acceptedPermissions);
        return new GitHubRestProbeResponse(
            response.StatusCode,
            permissions is null || permissions.Contains("issue_fields=write", StringComparison.OrdinalIgnoreCase),
            text.Contains("Invalid input: data cannot be null", StringComparison.OrdinalIgnoreCase)
                || text.Contains("missing_field", StringComparison.OrdinalIgnoreCase)
                || text.Contains("missing required keys", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Validation Failed", StringComparison.OrdinalIgnoreCase),
            GetRequestId(response))
        {
            RequestAttempt = _attempts.GetValue(response, _ => throw new InvalidOperationException()),
        };
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        using var response = await SendAsync(request, ApiOperation.RestDelete, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            await CaptureFailureAsync(response, ApiOperation.RestDelete, cancellationToken).ConfigureAwait(false);
            return;
        }

        _ = await ReadJsonAsync(response, ApiOperation.RestDelete, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _httpClient.Dispose();

    private static StringContent CreateJsonContent(object body)
        => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, ApiOperation operation, CancellationToken cancellationToken)
    {
        var attempt = DiagnosticSession.BeginAttempt();
        try
        {
            var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            _attempts.Add(response, attempt);
            return response;
        }
        catch (HttpRequestException exception)
        {
            throw Failure("GitHub REST transport failed.", operation, exception.StatusCode, null, "transport-failure", attempt);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure("GitHub REST request timed out.", operation, null, null, "request-timeout", attempt);
        }
        catch (OperationCanceledException)
        {
            throw ApiDiagnosticSession.Attach(new OperationCanceledException("GitHub REST request cancelled.", cancellationToken), attempt);
        }
    }

    private static string? GetRequestId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-GitHub-Request-Id", out var values)
            ? GraphQLDiagnosticSanitizer.RequestId(values.FirstOrDefault())
            : null;

    private static HttpRequestException Failure(
        string message,
        ApiOperation operation,
        HttpStatusCode? statusCode,
        string? requestId,
        string failureReason,
        ApiRequestAttempt attempt)
    {
        var exception = new HttpRequestException(
            $"{message} (runId {attempt.RunId}, attemptId {attempt.AttemptId}, sensitiveResponseCapture {attempt.SensitiveResponseCapture})",
            null, statusCode);
        Failures.Add(exception, new(operation.ToString(), GraphQLDiagnosticSanitizer.RequestId(requestId), 0, failureReason)
        {
            RequestAttempt = attempt,
            StatusCode = statusCode,
        });
        return ApiDiagnosticSession.Attach(exception, attempt);
    }

    internal static InvalidOperationException ProbeFailure(string message, GitHubRestProbeResponse response)
    {
        if (response.RequestAttempt is { } correlation)
        {
            message += $" (runId {correlation.RunId}, attemptId {correlation.AttemptId}, sensitiveResponseCapture {correlation.SensitiveResponseCapture})";
        }
        var exception = new InvalidOperationException(message);
        Failures.Add(exception, new(ApiOperation.RestValidationProbe.ToString(), response.RequestId, 0, "validation-probe-failure")
        {
            RequestAttempt = response.RequestAttempt,
            StatusCode = response.StatusCode,
        });
        return response.RequestAttempt is { } attempt ? ApiDiagnosticSession.Attach(exception, attempt) : exception;
    }

    private void CaptureFailure(HttpResponseMessage response, ApiOperation operation, string body)
    {
        if (!response.IsSuccessStatusCode)
        {
            SensitiveDiagnostics?.Record(operation, response.StatusCode, GetRequestId(response), 0, body,
                _attempts.GetValue(response, _ => throw new InvalidOperationException()));
        }
    }

    private async Task CaptureFailureAsync(HttpResponseMessage response, ApiOperation operation, CancellationToken cancellationToken)
    {
        if (SensitiveDiagnostics is not null)
        {
            CaptureFailure(response, operation, await ReadBodyAsync(response, operation, cancellationToken).ConfigureAwait(false));
        }
    }

    private async Task<string> ReadBodyAsync(HttpResponseMessage response, ApiOperation operation, CancellationToken cancellationToken)
    {
        var attempt = _attempts.GetValue(response, _ => throw new InvalidOperationException());
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw Failure("GitHub REST response could not be read.", operation, response.StatusCode,
                GetRequestId(response), "transport-failure", attempt);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure("GitHub REST request timed out.", operation, response.StatusCode,
                GetRequestId(response), "request-timeout", attempt);
        }
        catch (OperationCanceledException)
        {
            throw ApiDiagnosticSession.Attach(new OperationCanceledException("GitHub REST request cancelled.", cancellationToken), attempt);
        }
    }

    private async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, ApiOperation operation, CancellationToken cancellationToken)
    {
        var text = await ReadBodyAsync(response, operation, cancellationToken).ConfigureAwait(false);
        var attempt = _attempts.GetValue(response, _ => throw new InvalidOperationException());
        if (!response.IsSuccessStatusCode)
        {
            CaptureFailure(response, operation, text);
            throw Failure(
                $"GitHub REST error {(int)response.StatusCode} (operation {operation}, request ID {GetRequestId(response) ?? "unavailable"}, retries 0).",
                operation,
                response.StatusCode,
                GetRequestId(response),
                "http-error", attempt);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return default;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw Failure("GitHub REST returned malformed JSON.", operation, response.StatusCode, GetRequestId(response), "malformed-response", attempt);
        }
    }
}

public sealed record GitHubRestFailureDiagnostic(
    string Operation,
    string? RequestId,
    int RetryCount,
    string FailureReason)
{
    public ApiRequestAttempt? RequestAttempt { get; init; }
    public HttpStatusCode? StatusCode { get; init; }
}

public sealed record GitHubRestProbeResponse(
    HttpStatusCode StatusCode,
    bool AcceptsIssueFieldsWrite,
    bool ReportsMissingInput,
    string? RequestId)
{
    public ApiRequestAttempt? RequestAttempt { get; init; }
}
