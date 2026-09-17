using System.Net;

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

    /// <summary>Raw JSON of the "errors" array returned by the GraphQL endpoint, if any.</summary>
    public string? ErrorsJson { get; init; }

    /// <summary>The "type" of the first GraphQL error (e.g. NOT_FOUND), if any.</summary>
    public string? ErrorType { get; init; }

    /// <summary>The HTTP status code of the failing response, including HTTP 200 GraphQL errors.</summary>
    public HttpStatusCode? StatusCode { get; init; }

    public string? RequestId { get; init; }

    /// <summary>A locally defined reason, independent of the untrusted server message.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Safe projections of server errors; raw response data must not be persisted.</summary>
    public IReadOnlyList<GraphQLErrorDiagnostic> GraphQlErrors { get; init; } = [];
}
