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

    public static string? RequestId(string? value) => value is null
        ? null
        : value.Length is > 0 and <= 128
            && value.All(character => char.IsAsciiHexDigit(character) || character is ':' or '-')
            ? value
            : Redacted;

    internal static IReadOnlyList<GraphQLErrorDiagnostic> Errors(JsonElement errors, string query)
    {
        if (errors.ValueKind != JsonValueKind.Array)
        {
            return [new() { Type = null, Message = "Malformed GraphQL errors value.", Path = [] }];
        }

        // Only field response names, never names from input syntax, may survive in a path.
        var identifiers = GraphQLResponseNames.Parse(query);
        return errors.EnumerateArray().Select(error => new GraphQLErrorDiagnostic
        {
            Type = ErrorType(GetString(error, "type")),
            Message = SafeMessage(GetString(error, "message")),
            Path = error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("path", out var path)
                && path.ValueKind == JsonValueKind.Array
                    ? path.EnumerateArray().Select(segment =>
                        segment.ValueKind == JsonValueKind.String && identifiers.Contains(segment.GetString()!)
                            ? segment.GetString()!
                            : segment.ValueKind == JsonValueKind.Number && segment.TryGetInt32(out var index) && index >= 0
                                ? index.ToString(CultureInfo.InvariantCulture)
                                : Redacted).ToArray()
                    : [],
        }).ToArray();
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
