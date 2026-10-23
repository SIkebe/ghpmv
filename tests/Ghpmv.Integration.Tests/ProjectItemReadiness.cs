using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;

namespace Ghpmv.Integration.Tests;

internal static class ProjectItemReadiness
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    public static async Task WaitForImportAsync(
        GitHubGraphQLClient client,
        string projectId,
        string logDirectory,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        var log = await ImportLog.LoadAsync(logDirectory, cancellationToken);
        if (log is null || log.Items.Count != expectedCount)
        {
            throw new InvalidOperationException("The import log does not contain all expected mutation-confirmed item IDs.");
        }

        await WaitAsync(client, projectId, log.Items.Values.ToArray(), cancellationToken);
    }

    public static async Task WaitAsync(
        GitHubGraphQLClient client,
        string projectId,
        IReadOnlyCollection<string> expectedIds,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Action<string>? writeDiagnostic = null)
    {
        var expected = expectedIds.ToHashSet(StringComparer.Ordinal);
        if (expected.Count != expectedIds.Count || expected.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Expected item IDs must be nonempty and distinct.", nameof(expectedIds));
        }

        var startedAt = Stopwatch.GetTimestamp();
        var deadline = timeout ?? Timeout;
        delayAsync ??= Task.Delay;
        writeDiagnostic ??= Console.WriteLine;
        writeDiagnostic($"Project item readiness context: projectId={projectId}, expected={expected.Count}.");
        IntegrationApiTrace.WriteRecentRequests(client, "before-poll", writeDiagnostic);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HashSet<string> visible = new(StringComparer.Ordinal);
            await foreach (var item in client.QueryPaginatedAsync(
                """
                query($projectId: ID!, $after: String) {
                  node(id: $projectId) {
                    ... on ProjectV2 {
                      items(first: 100, after: $after, archivedStates: [ARCHIVED, NOT_ARCHIVED]) {
                        nodes { id }
                        pageInfo { hasNextPage endCursor }
                      }
                    }
                  }
                }
                """,
                new { projectId },
                "node.items",
                cancellationToken: cancellationToken))
            {
                visible.Add(item.GetProperty("id").GetString()
                    ?? throw new GitHubGraphQLException("Project items connection returned a null item ID."));
            }

            var missingIds = expected.Except(visible, StringComparer.Ordinal).ToArray();
            var missing = missingIds.Length;
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            writeDiagnostic(string.Create(
                CultureInfo.InvariantCulture,
                $"Project item readiness: expected={expected.Count}, visible={visible.Count}, missing={missing}, elapsed={elapsed.TotalSeconds:F1}s."));
            IntegrationApiTrace.WriteRecentRequests(client, "poll", writeDiagnostic);
            if (missing == 0)
            {
                return;
            }

            var remaining = deadline - elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                var failure = new TimeoutException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"GitHub's items connection still omits {missing} of {expected.Count} mutation-confirmed items after {elapsed.TotalSeconds:F1}s. No item assertion was bypassed."));
                await WriteTimeoutDiagnosticsAsync(client, projectId, missingIds, writeDiagnostic, cancellationToken);
                throw failure;
            }

            await delayAsync(remaining < PollInterval ? remaining : PollInterval, cancellationToken);
        }
    }

    private static async Task WriteTimeoutDiagnosticsAsync(
        GitHubGraphQLClient client,
        string projectId,
        string[] missingIds,
        Action<string> write,
        CancellationToken cancellationToken)
    {
        var sample = missingIds.Take(5).ToArray();
        write($"Project item readiness timeout: projectId={projectId}, missingIdSample={JsonSerializer.Serialize(sample)}. "
            + "Read-only probes have a shared 15s budget; they cannot make this test pass.");
        using var diagnosticsCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        diagnosticsCancellation.CancelAfter(TimeSpan.FromSeconds(15));

        await ProbeAsync(
            "project-connections",
            """
            query($projectId: ID!) {
              node(id: $projectId) {
                __typename
                id
                ... on ProjectV2 {
                  number
                  closed
                  viewerCanUpdate
                  includingArchived: items(first: 5, archivedStates: [ARCHIVED, NOT_ARCHIVED]) {
                    totalCount
                    nodes { id }
                    pageInfo { hasNextPage endCursor }
                  }
                  defaultItems: items(first: 5) {
                    totalCount
                    nodes { id }
                    pageInfo { hasNextPage endCursor }
                  }
                }
              }
            }
            """,
            new { projectId });
        await ProbeAsync(
            "missing-item-nodes",
            """
            query($ids: [ID!]!) {
              nodes(ids: $ids) {
                __typename
                id
                ... on ProjectV2Item {
                  isArchived
                  project { id }
                }
              }
            }
            """,
            new { ids = sample });

        async Task ProbeAsync(string stage, string query, object variables)
        {
            try
            {
                var data = await client.QueryAsync(query, variables, diagnosticsCancellation.Token);
                var summary = stage == "project-connections"
                    ? DescribeProject(data.GetProperty("node"))
                    : data.GetProperty("nodes").EnumerateArray().Take(5).Select(DescribeItem).ToArray();
                write($"Project item readiness probe ({stage}): {JsonSerializer.Serialize(summary)}");
            }
            catch (Exception exception)
            {
                // Probes are secondary evidence and must not replace the readiness failure.
                write($"Project item readiness probe ({stage}) failed: type={exception.GetType().Name}; "
                    + "preserving the original readiness timeout.");
                if (exception is GitHubGraphQLException apiFailure)
                {
                    write($"Project item readiness probe API failure: status={apiFailure.StatusCode}, "
                        + $"errorType={apiFailure.ErrorType}, reason={apiFailure.FailureReason}.");
                }
            }
            finally
            {
                IntegrationApiTrace.WriteRecentRequests(client, stage, write);
            }
        }

    }

    private static object? DescribeProject(JsonElement node) => node.ValueKind == JsonValueKind.Null ? null : new
    {
        type = node.GetProperty("__typename").GetString(),
        id = node.GetProperty("id").GetString(),
        number = node.TryGetProperty("number", out var number) ? number.GetInt32() : (int?)null,
        closed = node.TryGetProperty("closed", out var closed) ? closed.GetBoolean() : (bool?)null,
        viewerCanUpdate = node.TryGetProperty("viewerCanUpdate", out var canUpdate) ? canUpdate.GetBoolean() : (bool?)null,
        includingArchived = DescribeConnection(node, "includingArchived"),
        defaultItems = DescribeConnection(node, "defaultItems"),
    };

    private static object? DescribeConnection(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out var connection))
        {
            return null;
        }

        var page = connection.GetProperty("pageInfo");
        return new
        {
            totalCount = connection.GetProperty("totalCount").GetInt32(),
            ids = connection.GetProperty("nodes").EnumerateArray().Take(5)
                .Select(item => item.GetProperty("id").GetString()).ToArray(),
            hasNextPage = page.GetProperty("hasNextPage").GetBoolean(),
            endCursor = page.GetProperty("endCursor").GetString(),
        };
    }

    private static object? DescribeItem(JsonElement node) => node.ValueKind == JsonValueKind.Null ? null : new
    {
        type = node.GetProperty("__typename").GetString(),
        id = node.GetProperty("id").GetString(),
        isArchived = node.TryGetProperty("isArchived", out var archived) ? archived.GetBoolean() : (bool?)null,
        projectId = node.TryGetProperty("project", out var project) ? project.GetProperty("id").GetString() : null,
    };
}
