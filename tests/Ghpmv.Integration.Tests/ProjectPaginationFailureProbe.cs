using System.Text.Json;
using Ghpmv.Core.GitHub;

namespace Ghpmv.Integration.Tests;

internal static class ProjectPaginationFailureProbe
{
    public static async Task WriteAsync(
        GitHubGraphQLClient client,
        string organization,
        int projectNumber,
        string projectId,
        IReadOnlyList<string> expectedIds,
        CancellationToken cancellationToken,
        Action<string>? writeDiagnostic = null)
    {
        writeDiagnostic ??= Console.WriteLine;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            // A single read-only operation compares resolver paths and both connection ends.
            // These bounded windows are evidence only, never a substitute for pagination.
            var data = await client.QueryAsync(
                """
                query($organization: String!, $number: Int!, $projectId: ID!, $ids: [ID!]!) {
                  organization(login: $organization) {
                    projectV2(number: $number) {
                      id
                      items(first: 100, archivedStates: [ARCHIVED, NOT_ARCHIVED]) {
                        totalCount
                        nodes { id }
                        pageInfo { hasNextPage hasPreviousPage startCursor endCursor }
                      }
                    }
                  }
                  node(id: $projectId) {
                    ... on ProjectV2 {
                      id
                      forward: items(first: 100, archivedStates: [ARCHIVED, NOT_ARCHIVED]) {
                        totalCount
                        nodes { id }
                        pageInfo { hasNextPage hasPreviousPage startCursor endCursor }
                      }
                      reverse: items(last: 100, archivedStates: [ARCHIVED, NOT_ARCHIVED]) {
                        totalCount
                        nodes { id }
                        pageInfo { hasNextPage hasPreviousPage startCursor endCursor }
                      }
                    }
                  }
                  nodes(ids: $ids) {
                    __typename
                    id
                    ... on ProjectV2Item {
                      isArchived
                      project { id }
                      content { __typename }
                    }
                  }
                }
                """,
                new { organization, number = projectNumber, projectId, ids = expectedIds },
                budget.Token);
            var ordinals = expectedIds.Select((id, index) => (id, index))
                .ToDictionary(pair => pair.id, pair => pair.index + 1, StringComparer.Ordinal);
            var project = data.GetProperty("node");
            var alternateProject = data.GetProperty("organization").GetProperty("projectV2");
            var direct = data.GetProperty("nodes").EnumerateArray().ToArray();
            var confirmed = direct.Where(item => item.ValueKind == JsonValueKind.Object
                && item.GetProperty("__typename").GetString() == "ProjectV2Item"
                && item.GetProperty("project").GetProperty("id").GetString() == projectId
                && !item.GetProperty("isArchived").GetBoolean()
                && item.GetProperty("content").ValueKind == JsonValueKind.Object
                && item.GetProperty("content").GetProperty("__typename").GetString() == "DraftIssue")
                .Select(item => item.GetProperty("id").GetString()!)
                .ToHashSet(StringComparer.Ordinal);
            var summary = new
            {
                projectId,
                expected = expectedIds.Count,
                directReturned = direct.Length,
                confirmedUnarchivedDraftsInProject = expectedIds.Count(confirmed.Contains),
                unconfirmedOrdinals = expectedIds.Where(id => !confirmed.Contains(id)).Select(id => ordinals[id]).ToArray(),
                nodeProjectMatches = project.GetProperty("id").GetString() == projectId,
                organizationProjectMatches = alternateProject.GetProperty("id").GetString() == projectId,
                forward = DescribeConnection(project.GetProperty("forward"), ordinals),
                reverse = DescribeConnection(project.GetProperty("reverse"), ordinals),
                organization = DescribeConnection(alternateProject.GetProperty("items"), ordinals),
            };
            writeDiagnostic($"Project pagination failure comparison (read-only, 100-item windows): {JsonSerializer.Serialize(summary)}");
        }
        catch (Exception exception)
        {
            writeDiagnostic($"Project pagination failure comparison failed: type={exception.GetType().Name}; preserving the readiness timeout.");
        }
        finally
        {
            IntegrationApiTrace.WriteRecentRequests(client, "pagination-comparison", writeDiagnostic);
        }
    }

    private static object DescribeConnection(JsonElement connection, Dictionary<string, int> ordinals)
    {
        var ids = connection.GetProperty("nodes").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!).ToArray();
        var page = connection.GetProperty("pageInfo");
        return new
        {
            totalCount = connection.GetProperty("totalCount").GetInt32(),
            returned = ids.Length,
            distinct = ids.Distinct(StringComparer.Ordinal).Count(),
            creationOrdinals = ids.Select(id => ordinals.TryGetValue(id, out var ordinal) ? ordinal : (int?)null).ToArray(),
            hasNextPage = page.GetProperty("hasNextPage").GetBoolean(),
            hasPreviousPage = page.GetProperty("hasPreviousPage").GetBoolean(),
        };
    }
}
