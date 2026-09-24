using System.Globalization;
using System.Text.Json;

namespace Ghpmv.Core.GitHub;

public sealed record GraphQLErrorDiagnostic
{
    public required string? Type { get; init; }

    public required string Message { get; init; }

    public required IReadOnlyList<string> Path { get; init; }
}

public static class GraphQLDiagnosticSanitizer
{
    private const string Redacted = "[redacted]";
    internal const int MaximumErrors = 16;
    internal const int MaximumPathSegments = 32;
    internal const string ErrorsTruncated = "Additional GraphQL errors truncated.";
    internal const string PathTruncated = "[path truncated]";

    public static string OperationName(string value) => value switch
    {
        "createProjectV2" or "updateProjectV2" or "deleteProjectV2"
            or "createProjectV2Field" or "updateProjectV2Field"
            or "createIssueField" or "updateIssueField" or "createProjectV2IssueField"
            or "setIssueFieldValue" or "addProjectV2ItemById" or "addProjectV2DraftIssue"
            or "updateProjectV2ItemFieldValue" or "updateProjectV2ItemPosition"
            or "archiveProjectV2Item" or "unarchiveProjectV2Item" or "deleteProjectV2Item"
            or "updateProjectV2Collaborators" or "linkProjectV2ToTeam" or "linkProjectV2ToRepository"
            or "markProjectV2AsTemplate" or "unmarkProjectV2AsTemplate"
            or "createProjectV2View" or "updateProjectV2View" or "createProjectV2StatusUpdate" => value,
        _ => "mutation",
    };

    public static string? ErrorType(string? value) => value switch
    {
        null => null,
        "BAD_USER_INPUT" or "FORBIDDEN" or "INSUFFICIENT_SCOPES" or "NOT_FOUND"
            or "UNAUTHORIZED" or "UNPROCESSABLE" or "INTERNAL" or "INTERNAL_SERVER_ERROR"
            or "RATE_LIMITED" or "MAX_NODE_LIMIT_EXCEEDED" or "GRAPHQL_VALIDATION_FAILED"
            or "GRAPHQL_PARSE_FAILED" => value,
        _ => Redacted,
    };

    public static string? RequestId(string? value)
    {
        if (value is null) return null;
        if (value.Length is < 19 or > 40) return Redacted;

        // Accept the observed five-part GitHub format, not arbitrary hex tokens or UUIDs.
        // This validates structure, not the authenticity of the responding endpoint.
        var segments = value.Split(':');
        if (segments.Length != 5 || segments[0].Length != 4 || segments[^1].Length != 8)
        {
            return Redacted;
        }

        return segments.All(segment => segment.Length is >= 1 and <= 8
                && segment.All(char.IsAsciiHexDigit))
            ? value
            : Redacted;
    }

    internal static IReadOnlyList<GraphQLErrorDiagnostic> Errors(JsonElement errors, string query)
    {
        if (errors.ValueKind != JsonValueKind.Array)
        {
            return [new() { Type = null, Message = "Malformed GraphQL errors value.", Path = [] }];
        }

        return Errors(errors.EnumerateArray(), query);
    }

    internal static IReadOnlyList<GraphQLErrorDiagnostic> Errors(IEnumerable<JsonElement> errors, string query)
    {
        // Only field response names, never names from input syntax, may survive in a path.
        var identifiers = GraphQLResponseNames.Parse(query);
        var result = new List<GraphQLErrorDiagnostic>(MaximumErrors + 1);
        foreach (var error in errors)
        {
            if (result.Count == MaximumErrors)
            {
                result.Add(new() { Type = null, Message = ErrorsTruncated, Path = [] });
                break;
            }

            result.Add(new()
            {
                Type = ErrorType(GetString(error, "type")),
                Message = SafeMessage(GetString(error, "message")),
                Path = error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("path", out var path)
                    && path.ValueKind == JsonValueKind.Array
                        ? SanitizePath(path.EnumerateArray(), identifiers)
                        : [],
            });
        }

        return result;
    }

    internal static IReadOnlyList<string> SanitizePath(IEnumerable<JsonElement> path, HashSet<string> identifiers)
    {
        var result = new List<string>(MaximumPathSegments + 1);
        foreach (var segment in path)
        {
            if (result.Count == MaximumPathSegments)
            {
                result.Add(PathTruncated);
                break;
            }

            result.Add(segment.ValueKind == JsonValueKind.String
                && segment.GetString() is { Length: <= GraphQLResponseNames.MaximumNameLength } name
                && identifiers.Contains(name)
                    ? name
                    : segment.ValueKind == JsonValueKind.Number && segment.TryGetInt32(out var index) && index >= 0
                        ? index.ToString(CultureInfo.InvariantCulture)
                        : Redacted);
        }

        return result;
    }

    internal static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string SafeMessage(string? message)
    {
        // Emit fixed summaries rather than attempting to blacklist every possible secret format.
        if (message?.Contains("Something went wrong while executing your query", StringComparison.OrdinalIgnoreCase) is true)
        {
            return "GitHub reported an internal error while executing the query.";
        }

        if (message?.Contains("temporary conflict", StringComparison.OrdinalIgnoreCase) is true)
        {
            return "GitHub reported a temporary conflict.";
        }

        if (message?.Contains("Resource not accessible", StringComparison.OrdinalIgnoreCase) is true)
        {
            return "GitHub reported that the resource is not accessible.";
        }

        if (message?.Contains("Could not resolve to", StringComparison.OrdinalIgnoreCase) is true)
        {
            return "GitHub could not resolve a referenced resource.";
        }

        return "Server message redacted (unrecognized format).";
    }
}
