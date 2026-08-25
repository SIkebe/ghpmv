using Ghpmv.Core.Fixtures;
using Ghpmv.Core.Snapshot;
using Ghpmv.Core.GitHub;

namespace Ghpmv.Integration.Tests;

internal static class IntegrationFixtureSnapshot
{
    public static async Task<ProjectSnapshot> CreateKnownAsync(
        GitHubGraphQLClient client,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var viewerLogin = await client.GetViewerLoginAsync(cancellationToken);
        return NormalizeKnownSnapshot(
            FixtureProjectBuilder.CreateSnapshot(
                "gpm-fixture",
                IntegrationTestSettings.FixtureRepositoryFullName,
                viewerLogin,
                IntegrationTestSettings.FixturePullRequestNumber),
            viewerLogin);
    }

    internal static ProjectSnapshot NormalizeKnownSnapshot(
        ProjectSnapshot snapshot,
        string viewerLogin)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewerLogin);
        return snapshot with
        {
            Fields =
            [
                new FieldSnapshot { Name = "Title", DataType = "TITLE" },
                new FieldSnapshot { Name = "Assignees", DataType = "ASSIGNEES" },
                new FieldSnapshot { Name = "Linked pull requests", DataType = "LINKED_PULL_REQUESTS" },
                new FieldSnapshot { Name = "Sub-issues progress", DataType = "SUB_ISSUES_PROGRESS" },
                .. snapshot.Fields.Where(field =>
                    !string.Equals(field.Name, "Title", StringComparison.Ordinal)
                    && !string.Equals(field.Name, "Assignees", StringComparison.Ordinal)
                    && !string.Equals(field.Name, "Linked pull requests", StringComparison.Ordinal)
                    && !string.Equals(field.Name, "Sub-issues progress", StringComparison.Ordinal)),
            ],
            Items = snapshot.Items.Select(item =>
            {
                var values = item.FieldValues
                    .Select(value => value with { IsIssueField = value.IsIssueField ?? false })
                    .ToList();
                var title = item.Draft?.Title ?? item.Type switch
                {
                    "ISSUE" => $"Fixture issue {item.Number}",
                    "PULL_REQUEST" => "Fixture pull request",
                    _ => null,
                };
                if (title is not null
                    && !values.Any(value => string.Equals(value.FieldName, "Title", StringComparison.Ordinal)))
                {
                    values.Insert(0, new FieldValueSnapshot
                    {
                        FieldName = "Title",
                        IsIssueField = false,
                        Text = title,
                    });
                }

                return item with
                {
                    Draft = item.Draft is null
                        ? null
                        : item.Draft with { Creator = item.Draft.Creator ?? viewerLogin },
                    FieldValues = values,
                };
            }).ToArray(),
        };
    }

    public static ProjectSnapshot SelectCanonicalItems(ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var repository = IntegrationTestSettings.FixtureRepositoryFullName;
        ItemSnapshot[] items =
        [
            Draft(snapshot, FixtureProjectBuilder.RoadmapLongTitle),
            Draft(snapshot, "Fixture draft 2"),
            Draft(snapshot, "Fixture draft 3"),
            snapshot.Items.Single(item =>
                item.Type == "ISSUE"
                && string.Equals(item.Repository, repository, StringComparison.OrdinalIgnoreCase)
                && item.Number == 1),
            snapshot.Items.Single(item =>
                item.Type == "PULL_REQUEST"
                && string.Equals(item.Repository, repository, StringComparison.OrdinalIgnoreCase)
                && item.Number == 3),
            Draft(snapshot, "Fixture archived draft"),
            Draft(snapshot, "Fixture assigned draft"),
        ];

        return snapshot with
        {
            Items = items.Select((item, position) => item with { Position = position }).ToArray(),
        };
    }

    internal static IReadOnlyList<StatusUpdateSnapshot> SelectExpectedStatusUpdates(
        IReadOnlyList<StatusUpdateSnapshot> actual,
        IReadOnlyList<StatusUpdateSnapshot> expected)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(expected);

        var positions = new List<int>(expected.Count);
        foreach (var (actualUpdate, actualIndex) in actual.Select((update, index) => (update, index)))
        {
            var expectedIndex = expected
                .Select((expectedUpdate, index) => (expectedUpdate, index))
                .Where(entry => StatusUpdateMatches(entry.expectedUpdate, actualUpdate))
                .Select(entry => entry.index)
                .Cast<int?>()
                .SingleOrDefault();
            if (expectedIndex is null)
            {
                continue;
            }

            if (expectedIndex.Value < positions.Count)
            {
                // The stable shared fixture contains a known legacy duplicate from
                // before setup became idempotent. Select one canonical occurrence.
                continue;
            }
            if (expectedIndex.Value > positions.Count)
            {
                throw new InvalidOperationException(
                    "Expected fixture status updates were not in reverse chronological order.");
            }

            positions.Add(actualIndex);
        }

        if (positions.Count != expected.Count)
        {
            throw new InvalidOperationException(
                $"Expected fixture status update '{expected[positions.Count].Body}' was not found.");
        }

        return positions.Select(position => actual[position]).ToArray();
    }

    private static bool StatusUpdateMatches(
        StatusUpdateSnapshot expected,
        StatusUpdateSnapshot actual)
        => string.Equals(NormalizeBody(expected.Body), NormalizeBody(actual.Body), StringComparison.Ordinal)
            && string.Equals(expected.Status, actual.Status, StringComparison.Ordinal)
            && string.Equals(expected.StartDate, actual.StartDate, StringComparison.Ordinal)
            && string.Equals(expected.TargetDate, actual.TargetDate, StringComparison.Ordinal);

    private static string NormalizeBody(string body)
        => body.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static ItemSnapshot Draft(ProjectSnapshot snapshot, string title)
        => snapshot.Items.Single(item =>
            item.Type == "DRAFT_ISSUE"
            && string.Equals(item.Draft?.Title, title, StringComparison.Ordinal));

    public static async Task RemoveUnexpectedItemsAsync(
        GitHubGraphQLClient client,
        string org,
        int projectNumber,
        ProjectSnapshot expected,
        CancellationToken cancellationToken)
    {
        var expectedKeys = expected.Items.Select(ItemKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> unexpectedKeys = [];
        // Keep observing for the full window: Auto-add can create an unexpected item
        // several seconds after an initially clean read.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var (projectId, nodes) = await QueryItemsAsync();
            var unexpectedNodes = nodes
                .Where(node => !expectedKeys.Contains(ItemKey(node)))
                .ToArray();
            unexpectedKeys = unexpectedNodes
                .Select(ItemKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (attempt == 7 && unexpectedNodes.Length == 0)
            {
                return;
            }

            foreach (var node in unexpectedNodes)
            {
                await client.QueryAsync(
                    """
                    mutation($projectId: ID!, $itemId: ID!) {
                      deleteProjectV2Item(input: { projectId: $projectId, itemId: $itemId }) {
                        deletedItemId
                      }
                    }
                    """,
                    new { projectId, itemId = node.GetProperty("id").GetString()! },
                    cancellationToken);
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        var (_, finalNodes) = await QueryItemsAsync();
        unexpectedKeys = finalNodes
            .Where(node => !expectedKeys.Contains(ItemKey(node)))
            .Select(ItemKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (unexpectedKeys.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Project #{projectNumber} kept adding unexpected items: [{string.Join(", ", unexpectedKeys)}].");

        async Task<(string ProjectId, System.Text.Json.JsonElement[] Nodes)> QueryItemsAsync()
        {
            var data = await client.QueryAsync(
                """
                query($org: String!, $number: Int!) {
                  organization(login: $org) {
                    projectV2(number: $number) {
                      id
                      items(first: 100, archivedStates: [ARCHIVED, NOT_ARCHIVED]) {
                        nodes {
                          id
                          type
                          content {
                            ... on DraftIssue { title }
                            ... on Issue { number repository { nameWithOwner } }
                            ... on PullRequest { number repository { nameWithOwner } }
                          }
                        }
                      }
                    }
                  }
                }
                """,
                new { org, number = projectNumber },
                cancellationToken);
            var project = data.GetProperty("organization").GetProperty("projectV2");
            return (
                project.GetProperty("id").GetString()!,
                project.GetProperty("items").GetProperty("nodes").EnumerateArray().ToArray());
        }
    }

    private static string ItemKey(ItemSnapshot item) => item.Type == "DRAFT_ISSUE"
        ? $"DRAFT_ISSUE:{item.Draft?.Title}"
        : $"{item.Type}:{item.Repository}#{item.Number}";

    private static string ItemKey(System.Text.Json.JsonElement item)
    {
        var type = item.GetProperty("type").GetString()!;
        var content = item.GetProperty("content");
        return type == "DRAFT_ISSUE"
            ? $"DRAFT_ISSUE:{content.GetProperty("title").GetString()}"
            : $"{type}:{content.GetProperty("repository").GetProperty("nameWithOwner").GetString()}#{content.GetProperty("number").GetInt32()}";
    }
}
