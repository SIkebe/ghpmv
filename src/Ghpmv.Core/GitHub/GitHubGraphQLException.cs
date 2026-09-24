using System.Net;
using System.Globalization;

namespace Ghpmv.Core.GitHub;

/// <summary>
/// Thrown when a GitHub GraphQL request fails, either at the HTTP level
/// (after retries are exhausted) or because the response contains GraphQL errors.
/// </summary>
public class GitHubGraphQLException : Exception
{
    public GitHubGraphQLException()
    {
    }

    public GitHubGraphQLException(string message)
        : base(message)
    {
    }

    public GitHubGraphQLException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Raw errors used only for internal classification, never diagnostic serialization.</summary>
    internal string? ErrorsJson { get; init; }

    /// <summary>The "type" of the first GraphQL error (e.g. NOT_FOUND), if any.</summary>
    public string? ErrorType { get; init; }

    /// <summary>The HTTP status code of the failing response, including HTTP 200 GraphQL errors.</summary>
    public HttpStatusCode? StatusCode { get; init; }

    public string? RequestId { get; init; }

    public ApiRequestAttempt? RequestAttempt => ApiDiagnosticSession.GetAttempt(this);

    /// <summary>A locally defined reason, independent of the untrusted server message.</summary>
    public string? FailureReason { get; init; }

    public string? OperationKind { get; init; }

    public int RetryCount { get; init; }

    public string? InputValidation { get; internal set; }

    public override string Message
    {
        get
        {
            var context = FailureReason is null
                ? string.Empty
                : string.Create(CultureInfo.InvariantCulture,
                    $" (operation {(OperationKind is "query" or "mutation" ? OperationKind : "unknown")}, HTTP {(StatusCode is { } status ? ((int)status).ToString(CultureInfo.InvariantCulture) : "unavailable")}, code {GraphQLDiagnosticSanitizer.ErrorType(ErrorType) ?? "unknown"}, request ID {GraphQLDiagnosticSanitizer.RequestId(RequestId) ?? "unavailable"}, retries {RetryCount}).");
            var correlation = RequestAttempt is { } attempt
                ? $" (runId {attempt.RunId}, attemptId {attempt.AttemptId}, sensitiveResponseCapture {attempt.SensitiveResponseCapture})"
                : string.Empty;
            return base.Message + context + correlation + (InputValidation is null ? string.Empty : $" {InputValidation}");
        }
    }

    /// <summary>Safe projections of server errors; raw response data must not be persisted.</summary>
    public IReadOnlyList<GraphQLErrorDiagnostic> GraphQlErrors { get; init; } = [];
}
