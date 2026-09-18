using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Ghpmv.Core.GitHub;

/// <summary>
/// GraphQL client for the GitHub API (M1).
/// Supports cursor pagination, primary/secondary rate-limit handling and 5xx retries.
/// </summary>
public sealed class GitHubGraphQLClient : IDisposable
{
    private static readonly Uri DefaultEndpoint = new("https://api.github.com/graphql");

    private const int MaxSecondaryRateLimitRetries = 5;
    private const int MaxServerErrorRetries = 3;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly HttpClient _httpClient;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public GitHubGraphQLClient(string token, Uri? baseUrl = null)
        : this(token, baseUrl, new HttpClientHandler(), delayAsync: null)
    {
    }

    internal GitHubGraphQLClient(string token, Uri? baseUrl, HttpMessageHandler handler, Func<TimeSpan, CancellationToken, Task>? delayAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(handler);

        _httpClient = new HttpClient(handler) { BaseAddress = baseUrl ?? DefaultEndpoint };
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ghpmv");
        _httpClient.DefaultRequestHeaders.Add("GraphQL-Features", "issue_fields");
        _delayAsync = delayAsync ?? (static (delay, ct) => Task.Delay(delay, ct));
    }

    /// <summary>Invoked with a human-readable message before every rate-limit/retry wait.</summary>
    public Action<string>? OnRetry { get; set; }

    public string EndpointHost => _httpClient.BaseAddress!.Host;

    /// <summary>Optional invocation-owned sink. The caller must dispose it.</summary>
    public SensitiveApiDiagnostics? SensitiveDiagnostics { get; init; }

    /// <summary>
    /// Normalizes a GraphQL API base URL given on the command line: accepts both
    /// "https://api.TENANT.ghe.com" and "https://api.TENANT.ghe.com/graphql" (with or
    /// without a trailing slash) and returns the endpoint URI ending in "/graphql".
    /// </summary>
    public static Uri NormalizeBaseUrl(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new FormatException($"'{baseUrl}' is not an absolute http(s) URL.");
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            throw new FormatException($"'{baseUrl}' must use HTTPS. HTTP is allowed only for loopback test endpoints.");
        }

        if (!trimmed.EndsWith("/graphql", StringComparison.OrdinalIgnoreCase))
        {
            trimmed += "/graphql";
        }

        return new Uri(trimmed);
    }

    /// <summary>Executes a GraphQL query and returns the "data" element.</summary>
    public async Task<JsonElement> QueryAsync(string query, object? variables = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var payload = JsonSerializer.Serialize(new { query, variables });
        return await ExecuteOperationAsync(payload, query, mutation: null, retryInternalErrors: true, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<JsonElement> QueryWithoutInternalErrorRetryAsync(
        string query,
        object? variables,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var payload = JsonSerializer.Serialize(new { query, variables });
        return await ExecuteOperationAsync(payload, query, mutation: null, retryInternalErrors: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a GraphQL mutation. Create operations stop on ambiguous transport or
    /// server failures; only callers that guarantee idempotency may enable retries.
    /// </summary>
    public async Task<JsonElement> MutationAsync(
        string operationName,
        string mutation,
        object? variables = null,
        MutationRetryPolicy retryPolicy = MutationRetryPolicy.Create,
        string? target = null,
        string? clientMutationId = null,
        string? requiredResultPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
            ArgumentException.ThrowIfNullOrWhiteSpace(mutation);
            ArgumentException.ThrowIfNullOrWhiteSpace(requiredResultPath);

            clientMutationId ??= Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            var variableMap = ToVariableMap(variables);
            variableMap["clientMutationId"] = clientMutationId;
            var payload = JsonSerializer.Serialize(new { query = mutation, variables = variableMap });
            var context = new MutationContext(operationName, clientMutationId, DateTimeOffset.UtcNow, target, retryPolicy, requiredResultPath);
            return await ExecuteOperationAsync(payload, mutation, context, retryInternalErrors: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (Ghpmv.Core.Import.MigrationDiagnostics.Capture(exception, operationName))
        {
            throw;
        }
    }

    private async Task<JsonElement> ExecuteOperationAsync(
        string payload,
        string query,
        MutationContext? mutation,
        bool retryInternalErrors,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteOperationCoreAsync(payload, query, mutation, retryInternalErrors, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (Ghpmv.Core.Import.MigrationDiagnostics.Capture(
            exception, mutation?.OperationName ?? Ghpmv.Core.Import.MigrationDiagnostics.Current?.Operation ?? "query"))
        {
            throw;
        }
    }

    private async Task<JsonElement> ExecuteOperationCoreAsync(
        string payload,
        string query,
        MutationContext? mutation,
        bool retryInternalErrors,
        CancellationToken cancellationToken)
    {
        var temporaryConflictRetries = 0;
        var incompleteResultRetries = 0;
        var internalErrorRetries = 0;
        var attempt = 0;

        while (true)
        {
            using var response = await ExecuteAsync(payload, mutation, () => attempt++, cancellationToken).ConfigureAwait(false);
            var document = response.Document;

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("errors", out var errors))
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Object
                    && (mutation is null || HasExpectedMutationResult(data, mutation)))
                {
                    return data.Clone();
                }

                if (mutation is { RetryPolicy: MutationRetryPolicy.Create })
                {
                    throw CreateAmbiguousMutationException(
                        mutation,
                        "GitHub returned a success response without the expected mutation result.",
                        "missing-mutation-result",
                        statusCode: response.StatusCode,
                        requestId: response.RequestId,
                        retryCount: attempt - 1);
                }

                if (mutation is { RetryPolicy: MutationRetryPolicy.Idempotent }
                    && incompleteResultRetries < MaxServerErrorRetries)
                {
                    var backoff = GetBackoff(incompleteResultRetries);
                    incompleteResultRetries++;
                    await NotifyAndDelayAsync(
                        string.Create(CultureInfo.InvariantCulture, $"Incomplete mutation result; backing off {backoff.TotalSeconds:0}s (attempt {incompleteResultRetries}/{MaxServerErrorRetries})."),
                        backoff,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new GitHubGraphQLException(
                    mutation is null
                        ? "GraphQL success response did not contain an object-valued data property."
                        : $"GraphQL success response did not contain the expected '{GraphQLDiagnosticSanitizer.OperationName(mutation.OperationName)}' result.")
                {
                    StatusCode = response.StatusCode,
                    RequestId = response.RequestId,
                    FailureReason = "missing-result",
                    OperationKind = mutation is null ? "query" : "mutation",
                    RetryCount = attempt - 1,
                };
            }

            CaptureFailure(mutation, response.StatusCode, response.RequestId, attempt - 1, response.Body);
            var errorsJson = errors.GetRawText();
            var errorType = errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0
                ? GraphQLDiagnosticSanitizer.GetString(errors[0], "type")
                : null;
            var diagnostics = GraphQLDiagnosticSanitizer.Errors(errors, query);

            if (mutation is { RetryPolicy: MutationRetryPolicy.Create }
                && HasMutationPayload(document.RootElement, mutation.OperationName))
            {
                throw CreateAmbiguousMutationException(
                    mutation,
                    "GitHub returned a GraphQL error that may have occurred after the create side effect.",
                    "graphql-error-with-mutation-payload",
                    statusCode: response.StatusCode,
                    requestId: response.RequestId,
                    errorsJson: errorsJson,
                    errorType: errorType,
                    diagnostics: diagnostics,
                    retryCount: attempt - 1);
            }

            // Projects V2 mutations occasionally fail with UNPROCESSABLE
            // "…temporary conflict. Please try again." — the API explicitly asks
            // for a retry and the failed attempt has no side effects.
            if (temporaryConflictRetries < MaxServerErrorRetries
                && errorsJson.Contains("temporary conflict", StringComparison.OrdinalIgnoreCase))
            {
                var backoff = GetBackoff(temporaryConflictRetries);
                temporaryConflictRetries++;
                await NotifyAndDelayAsync(
                    string.Create(CultureInfo.InvariantCulture, $"Temporary conflict reported by the API; retrying in {backoff.TotalSeconds:0}s (attempt {temporaryConflictRetries}/{MaxServerErrorRetries})."),
                    backoff,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (mutation is { RetryPolicy: MutationRetryPolicy.Create }
                && !ContainsOnlyKnownPreSideEffectErrors(errors))
            {
                throw CreateAmbiguousMutationException(
                    mutation,
                    "GitHub returned a GraphQL error that may have occurred after the create side effect.",
                    "graphql-error-with-uncertain-side-effect",
                    statusCode: response.StatusCode,
                    requestId: response.RequestId,
                    errorsJson: errorsJson,
                    errorType: errorType,
                    diagnostics: diagnostics,
                    retryCount: attempt - 1);
            }

            if (retryInternalErrors
                && internalErrorRetries < MaxServerErrorRetries
                && mutation is null or { RetryPolicy: MutationRetryPolicy.Idempotent }
                && errorsJson.Contains("Something went wrong while executing your query", StringComparison.OrdinalIgnoreCase))
            {
                var backoff = GetBackoff(internalErrorRetries);
                internalErrorRetries++;
                await NotifyAndDelayAsync(
                    string.Create(CultureInfo.InvariantCulture, $"GitHub reported an internal GraphQL error; retrying in {backoff.TotalSeconds:0}s (attempt {internalErrorRetries}/{MaxServerErrorRetries})."),
                    backoff,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            var operationKind = mutation is null ? "query" : "mutation";
            var safeType = GraphQLDiagnosticSanitizer.ErrorType(errorType);
            throw new GitHubGraphQLException(
                $"GitHub GraphQL {operationKind} failed. "
                + string.Join(" ", diagnostics.Select(error => error.Message).Distinct(StringComparer.Ordinal)))
            {
                ErrorsJson = errorsJson,
                ErrorType = safeType,
                StatusCode = response.StatusCode,
                RequestId = response.RequestId,
                FailureReason = "graphql-error",
                GraphQlErrors = diagnostics,
                OperationKind = operationKind,
                RetryCount = attempt - 1,
            };
        }
    }

    /// <summary>
    /// Executes a cursor-paginated GraphQL query and yields every node of the connection
    /// found at <paramref name="connectionPath"/> (dot-separated path inside "data",
    /// e.g. "organization.projectsV2"). The connection must select
    /// "nodes" and "pageInfo { hasNextPage endCursor }", and the query must declare a
    /// cursor variable (default name "after").
    /// </summary>
    public async IAsyncEnumerable<JsonElement> QueryPaginatedAsync(
        string query,
        object? variables,
        string connectionPath,
        string cursorVariableName = "after",
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cursorVariableName);

        var variableMap = ToVariableMap(variables);
        string? cursor = null;

        while (true)
        {
            variableMap[cursorVariableName] = cursor;
            var data = await QueryAsync(query, variableMap, cancellationToken).ConfigureAwait(false);

            var connection = NavigatePath(data, connectionPath);
            if (connection.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
            {
                foreach (var node in nodes.EnumerateArray())
                {
                    yield return node;
                }
            }

            var pageInfo = connection.GetProperty("pageInfo");
            if (!pageInfo.GetProperty("hasNextPage").GetBoolean())
            {
                yield break;
            }

            cursor = pageInfo.GetProperty("endCursor").GetString();
        }
    }

    /// <summary>Returns the login of the authenticated user (connectivity check).</summary>
    public async Task<string> GetViewerLoginAsync(CancellationToken cancellationToken = default)
    {
        var data = await QueryAsync("query { viewer { login } }", cancellationToken: cancellationToken).ConfigureAwait(false);
        return data.GetProperty("viewer").GetProperty("login").GetString()
            ?? throw new GitHubGraphQLException("viewer.login was null.");
    }

    public void Dispose() => _httpClient.Dispose();

    /// <summary>Sends the payload with the full retry/rate-limit policy and returns the parsed body.</summary>
    private async Task<GraphQLResponse> ExecuteAsync(
        string payload,
        MutationContext? mutation,
        Func<int> nextAttempt,
        CancellationToken cancellationToken)
    {
        var secondaryRateLimitRetries = 0;
        var serverErrorRetries = 0;

        // A sent StringContent cannot be reused, so each attempt builds a fresh request.
        HttpRequestMessage CreateRequest() => new(HttpMethod.Post, (Uri?)null)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        while (true)
        {
            var retryCount = nextAttempt();
            using var request = CreateRequest();
            HttpResponseMessage? response = null;
            string body;
            HttpStatusCode status;
            try
            {
                response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                status = response.StatusCode;
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                var responseStatus = response?.StatusCode;
                var responseRequestId = GetRequestId(response);
                response?.Dispose();
                if (mutation is { RetryPolicy: MutationRetryPolicy.Create })
                {
                    throw CreateAmbiguousMutationException(
                        mutation, "GitHub transport failed.", "transport-failure", statusCode: responseStatus, requestId: responseRequestId, retryCount: retryCount);
                }

                if (serverErrorRetries >= MaxServerErrorRetries)
                {
                    throw new GitHubGraphQLException(
                        $"Network error persisted after {MaxServerErrorRetries} retries.")
                    {
                        StatusCode = responseStatus,
                        RequestId = responseRequestId,
                        FailureReason = "transport-failure",
                        OperationKind = mutation is null ? "query" : "mutation",
                        RetryCount = retryCount,
                    };
                }

                var networkBackoff = GetBackoff(serverErrorRetries);
                serverErrorRetries++;
                await NotifyAndDelayAsync(
                    string.Create(CultureInfo.InvariantCulture, $"Network error; backing off {networkBackoff.TotalSeconds:0}s (attempt {serverErrorRetries}/{MaxServerErrorRetries})."),
                    networkBackoff,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (OperationCanceledException) when (mutation is { RetryPolicy: MutationRetryPolicy.Create })
            {
                var responseStatus = response?.StatusCode;
                var responseRequestId = GetRequestId(response);
                response?.Dispose();
                throw CreateAmbiguousMutationException(
                    mutation, "GitHub request was cancelled or timed out.", "request-cancelled-or-timed-out", statusCode: responseStatus, requestId: responseRequestId, retryCount: retryCount);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                response?.Dispose();
                if (serverErrorRetries >= MaxServerErrorRetries)
                {
                    throw new GitHubGraphQLException(
                        $"Request timeout persisted after {MaxServerErrorRetries} retries.")
                    {
                        FailureReason = "request-timeout",
                        OperationKind = mutation is null ? "query" : "mutation",
                        RetryCount = retryCount,
                    };
                }

                var timeoutBackoff = GetBackoff(serverErrorRetries);
                serverErrorRetries++;
                await NotifyAndDelayAsync(
                    string.Create(CultureInfo.InvariantCulture, $"Request timed out; backing off {timeoutBackoff.TotalSeconds:0}s (attempt {serverErrorRetries}/{MaxServerErrorRetries})."),
                    timeoutBackoff,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (OperationCanceledException)
            {
                response?.Dispose();
                throw new OperationCanceledException("GitHub request cancelled.", cancellationToken);
            }

            using var _ = response;
            var requestId = GetRequestId(response);

            if (response.IsSuccessStatusCode)
            {
                // A create has a definitive response now. Do not make returning that
                // result depend on a cancellable wait after the side effect completed.
                if (mutation is not { RetryPolicy: MutationRetryPolicy.Create })
                {
                    await WaitForPrimaryRateLimitResetAsync(response, cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    return new GraphQLResponse(JsonDocument.Parse(body), status, requestId, body);
                }
                catch (JsonException)
                {
                    if (mutation is { RetryPolicy: MutationRetryPolicy.Create })
                    {
                        throw CreateAmbiguousMutationException(
                            mutation,
                            "GitHub returned an incomplete or malformed success response.",
                            "malformed-response",
                            statusCode: status,
                            requestId: requestId,
                            retryCount: retryCount);
                    }

                    if (serverErrorRetries >= MaxServerErrorRetries)
                    {
                        throw new GitHubGraphQLException(
                            $"Malformed success response persisted after {MaxServerErrorRetries} retries.")
                        {
                            StatusCode = status,
                            RequestId = requestId,
                            FailureReason = "malformed-response",
                            OperationKind = mutation is null ? "query" : "mutation",
                            RetryCount = retryCount,
                        };
                    }

                    var malformedResponseBackoff = GetBackoff(serverErrorRetries);
                    serverErrorRetries++;
                    await NotifyAndDelayAsync(
                        string.Create(CultureInfo.InvariantCulture, $"Malformed success response; backing off {malformedResponseBackoff.TotalSeconds:0}s (attempt {serverErrorRetries}/{MaxServerErrorRetries})."),
                        malformedResponseBackoff,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }

            CaptureFailure(mutation, status, requestId, retryCount, body);

            // (a) 403/429 with an explicit Retry-After header.
            if (status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                && GetRetryAfter(response) is { } retryAfter)
            {
                await NotifyAndDelayAsync(
                    string.Create(CultureInfo.InvariantCulture, $"Rate limited ({(int)status}); honoring Retry-After: waiting {retryAfter.TotalSeconds:0}s before retrying."),
                    retryAfter,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            // (b) Secondary rate limit without Retry-After: exponential backoff 1s, 2s, 4s... capped at 60s.
            if (status == HttpStatusCode.Forbidden
                && body.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase))
            {
                if (secondaryRateLimitRetries >= MaxSecondaryRateLimitRetries)
                {
                    throw new GitHubGraphQLException($"Secondary rate limit persisted after {MaxSecondaryRateLimitRetries} retries.")
                    {
                        StatusCode = status,
                        RequestId = requestId,
                        FailureReason = "secondary-rate-limit-exhausted",
                        OperationKind = mutation is null ? "query" : "mutation",
                        RetryCount = retryCount,
                    };
                }

                var backoff = GetBackoff(secondaryRateLimitRetries);
                secondaryRateLimitRetries++;
                await NotifyAndDelayAsync(
                    string.Create(CultureInfo.InvariantCulture, $"Secondary rate limit hit; backing off {backoff.TotalSeconds:0}s (attempt {secondaryRateLimitRetries}/{MaxSecondaryRateLimitRetries})."),
                    backoff,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            // (c) Transient server errors: exponential backoff, up to 3 retries.
            if ((int)status >= 500)
            {
                if (mutation is { RetryPolicy: MutationRetryPolicy.Create })
                {
                    throw CreateAmbiguousMutationException(
                        mutation,
                        string.Create(CultureInfo.InvariantCulture, $"GitHub returned HTTP {(int)status} ({status})."),
                        "http-server-error",
                        statusCode: status,
                        requestId: requestId,
                        retryCount: retryCount);
                }

                if (serverErrorRetries >= MaxServerErrorRetries)
                {
                    throw new GitHubGraphQLException($"Server error {(int)status} persisted after {MaxServerErrorRetries} retries.")
                    {
                        StatusCode = status,
                        RequestId = requestId,
                        FailureReason = "http-server-error",
                        OperationKind = mutation is null ? "query" : "mutation",
                        RetryCount = retryCount,
                    };
                }

                var backoff = GetBackoff(serverErrorRetries);
                serverErrorRetries++;
                await NotifyAndDelayAsync(
                    string.Create(CultureInfo.InvariantCulture, $"Server error {(int)status}; backing off {backoff.TotalSeconds:0}s (attempt {serverErrorRetries}/{MaxServerErrorRetries})."),
                    backoff,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Non-retryable HTTP failure (401, 404, ...).
            throw new GitHubGraphQLException($"GraphQL request failed with HTTP {(int)status}.")
            {
                StatusCode = status,
                RequestId = requestId,
                FailureReason = "http-error",
                OperationKind = mutation is null ? "query" : "mutation",
                RetryCount = retryCount,
            };
        }
    }

    private void CaptureFailure(MutationContext? mutation, HttpStatusCode status, string? requestId, int retryCount, string body)
    {
        try
        {
            SensitiveDiagnostics?.Record(
                mutation is null ? ApiOperation.GraphQlQuery : ApiOperation.GraphQlMutation,
                status, requestId, retryCount, body);
        }
        catch (IOException exception) when (mutation is { RetryPolicy: MutationRetryPolicy.Create })
        {
            // A diagnostic I/O failure must not clear the durable pending create operation.
            throw CreateAmbiguousMutationException(
                mutation, "Sensitive API diagnostics could not be saved; inspect target state before retrying.",
                "sensitive-diagnostic-write-failure", exception, status, requestId, retryCount: retryCount);
        }
    }

    /// <summary>(d) If the primary rate limit is exhausted, waits until X-RateLimit-Reset.</summary>
    private async Task WaitForPrimaryRateLimitResetAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!TryGetHeader(response, "X-RateLimit-Remaining", out var remaining) || remaining != "0")
        {
            return;
        }

        if (!TryGetHeader(response, "X-RateLimit-Reset", out var resetValue)
            || !long.TryParse(resetValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var resetEpoch))
        {
            return;
        }

        var wait = DateTimeOffset.FromUnixTimeSeconds(resetEpoch) - DateTimeOffset.UtcNow;
        if (wait <= TimeSpan.Zero)
        {
            return;
        }

        await NotifyAndDelayAsync(
            string.Create(CultureInfo.InvariantCulture, $"Primary rate limit exhausted; waiting {wait.TotalSeconds:0}s until reset."),
            wait,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task NotifyAndDelayAsync(string message, TimeSpan delay, CancellationToken cancellationToken)
    {
        OnRetry?.Invoke(message);
        await _delayAsync(delay, cancellationToken).ConfigureAwait(false);
    }

    private static TimeSpan GetBackoff(int retryCount)
    {
        var backoff = TimeSpan.FromSeconds(Math.Pow(2, retryCount));
        return backoff > MaxBackoff ? MaxBackoff : backoff;
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta >= TimeSpan.Zero ? delta : TimeSpan.Zero;
        }

        if (retryAfter.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait >= TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private static bool TryGetHeader(HttpResponseMessage response, string name, out string? value)
    {
        if (response.Headers.TryGetValues(name, out var values))
        {
            value = values.FirstOrDefault();
            return value is not null;
        }

        value = null;
        return false;
    }

    private static string? GetRequestId(HttpResponseMessage? response) =>
        response is not null && TryGetHeader(response, "X-GitHub-Request-Id", out var header)
            ? GraphQLDiagnosticSanitizer.RequestId(header)
            : null;

    private static AmbiguousMutationResultException CreateAmbiguousMutationException(
        MutationContext mutation,
        string detail,
        string failureReason,
        Exception? innerException = null,
        HttpStatusCode? statusCode = null,
        string? requestId = null,
        string? errorsJson = null,
        string? errorType = null,
        IReadOnlyList<GraphQLErrorDiagnostic>? diagnostics = null,
        int retryCount = 0)
        => new(
            mutation.OperationName,
            mutation.ClientMutationId,
            mutation.AttemptedAt,
            mutation.Target,
            detail,
            innerException)
        {
            StatusCode = statusCode,
            RequestId = requestId,
            FailureReason = failureReason,
            OperationKind = "mutation",
            RetryCount = retryCount,
            ErrorsJson = errorsJson,
            ErrorType = GraphQLDiagnosticSanitizer.ErrorType(errorType),
            GraphQlErrors = diagnostics ?? [],
        };

    private sealed record GraphQLResponse(JsonDocument Document, HttpStatusCode StatusCode, string? RequestId, string Body) : IDisposable
    {
        public void Dispose() => Document.Dispose();
    }

    private static bool HasExpectedMutationResult(JsonElement data, MutationContext mutation)
    {
        if (!data.TryGetProperty(mutation.OperationName, out var current)
            || current.ValueKind != JsonValueKind.Object
            || !current.EnumerateObject().Any())
        {
            return false;
        }

        foreach (var segment in mutation.RequiredResultPath!.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return false;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => !string.IsNullOrWhiteSpace(current.GetString()),
            JsonValueKind.Object => current.EnumerateObject().Any(),
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            _ => true,
        };
    }

    private static bool ContainsOnlyKnownPreSideEffectErrors(JsonElement errors)
    {
        if (errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() == 0)
        {
            return false;
        }

        foreach (var error in errors.EnumerateArray())
        {
            if (error.ValueKind != JsonValueKind.Object
                || !error.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            if (type.GetString() is not ("BAD_USER_INPUT" or "FORBIDDEN" or "INSUFFICIENT_SCOPES" or "NOT_FOUND" or "UNAUTHORIZED"))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasMutationPayload(JsonElement root, string operationName)
        => root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty(operationName, out _);

    /// <summary>Converts an arbitrary variables object into a mutable map so the cursor can be injected.</summary>
    private static Dictionary<string, object?> ToVariableMap(object? variables)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (variables is null)
        {
            return map;
        }

        var element = JsonSerializer.SerializeToElement(variables);
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("variables must serialize to a JSON object.", nameof(variables));
        }

        foreach (var property in element.EnumerateObject())
        {
            map[property.Name] = property.Value.Clone();
        }

        return map;
    }

    /// <summary>Walks a dot-separated property path (e.g. "organization.projectsV2") inside "data".</summary>
    private static JsonElement NavigatePath(JsonElement data, string connectionPath)
    {
        var current = data;
        foreach (var segment in connectionPath.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
            {
                throw new GitHubGraphQLException($"Connection path '{connectionPath}' not found in response (missing segment '{segment}').");
            }

            current = next;
        }

        return current;
    }

    private sealed record MutationContext(
        string OperationName,
        string ClientMutationId,
        DateTimeOffset AttemptedAt,
        string? Target,
        MutationRetryPolicy RetryPolicy,
        string? RequiredResultPath);
}
