using System.Diagnostics;
using System.Globalization;
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
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        var expected = expectedIds.ToHashSet(StringComparer.Ordinal);
        if (expected.Count != expectedIds.Count || expected.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Expected item IDs must be nonempty and distinct.", nameof(expectedIds));
        }

        var startedAt = Stopwatch.GetTimestamp();
        var deadline = timeout ?? Timeout;
        delayAsync ??= Task.Delay;
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

            var missing = expected.Except(visible, StringComparer.Ordinal).Count();
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Project item readiness: expected={expected.Count}, visible={visible.Count}, missing={missing}, elapsed={elapsed.TotalSeconds:F1}s."));
            if (missing == 0)
            {
                return;
            }

            var remaining = deadline - elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"GitHub's items connection still omits {missing} of {expected.Count} mutation-confirmed items after {elapsed.TotalSeconds:F1}s. No item assertion was bypassed."));
            }

            await delayAsync(remaining < PollInterval ? remaining : PollInterval, cancellationToken);
        }
    }
}
