using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Ghpmv.Core.GitHub;

/// <summary>Minimal GitHub REST client used by fixture setup for repository/Issue/PR bootstrapping.</summary>
public sealed class GitHubRestClient : IDisposable
{
    private static readonly Uri DefaultBaseUri = new("https://api.github.com/");

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
    public SensitiveApiDiagnostics? SensitiveDiagnostics { get; init; }

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
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
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
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(response, ApiOperation.RestPost, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> PutAsync(string path, object body, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = CreateJsonContent(body) };
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
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
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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
            GetRequestId(response));
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
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

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new HttpRequestException("GitHub REST transport failed.", null, exception.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("GitHub REST request timed out.");
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException("GitHub REST request cancelled.", cancellationToken);
        }
    }

    private static string? GetRequestId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-GitHub-Request-Id", out var values)
            ? GraphQLDiagnosticSanitizer.RequestId(values.FirstOrDefault())
            : null;

    private void CaptureFailure(HttpResponseMessage response, ApiOperation operation, string body)
    {
        if (!response.IsSuccessStatusCode)
        {
            SensitiveDiagnostics?.Record(operation, response.StatusCode, GetRequestId(response), 0, body);
        }
    }

    private async Task CaptureFailureAsync(HttpResponseMessage response, ApiOperation operation, CancellationToken cancellationToken)
    {
        if (SensitiveDiagnostics is not null)
        {
            CaptureFailure(response, operation, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
    }

    private async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, ApiOperation operation, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            CaptureFailure(response, operation, text);
            throw new HttpRequestException(
                $"GitHub REST error {(int)response.StatusCode} (operation {operation}, request ID {GetRequestId(response) ?? "unavailable"}, retries 0).",
                null,
                response.StatusCode);
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
            throw new HttpRequestException("GitHub REST returned malformed JSON.", null, response.StatusCode);
        }
    }
}

public sealed record GitHubRestProbeResponse(
    HttpStatusCode StatusCode,
    bool AcceptsIssueFieldsWrite,
    bool ReportsMissingInput,
    string? RequestId);
