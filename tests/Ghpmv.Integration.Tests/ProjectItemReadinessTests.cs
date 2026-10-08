using System.Net;
using System.Text;
using System.Text.Json;
using Ghpmv.Core.GitHub;

namespace Ghpmv.Integration.Tests;

public class ProjectItemReadinessTests
{
    [Fact]
    public async Task Readiness_enumerates_all_120_mutation_ids_across_pages()
    {
        var ids = Enumerable.Range(1, 120).Select(index => $"item-{index}").ToArray();
        using var handler = new Handler(
            Page(ids[..50], "page-1"),
            Page(ids[50..100], "page-2"),
            Page(ids[100..]));
        using var client = IntegrationTestSettings.CreateClient("test-token", handler);

        await ProjectItemReadiness.WaitAsync(client, "project", ids, TestContext.Current.CancellationToken);

        Assert.Equal(3, handler.Bodies.Count);
        using var first = JsonDocument.Parse(handler.Bodies[0]);
        using var second = JsonDocument.Parse(handler.Bodies[1]);
        using var third = JsonDocument.Parse(handler.Bodies[2]);
        Assert.Equal(JsonValueKind.Null, first.RootElement.GetProperty("variables").GetProperty("after").ValueKind);
        Assert.Equal("page-1", second.RootElement.GetProperty("variables").GetProperty("after").GetString());
        Assert.Equal("page-2", third.RootElement.GetProperty("variables").GetProperty("after").GetString());
        Assert.All(handler.Bodies, body => Assert.Contains("ARCHIVED, NOT_ARCHIVED", body, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Readiness_requires_all_ids_in_one_observation_not_a_union_of_partial_reads()
    {
        using var handler = new Handler(Page(["one"]), Page(["two"]), Page(["one", "two", "auto-added"]));
        using var client = IntegrationTestSettings.CreateClient("test-token", handler);
        var delays = 0;

        await ProjectItemReadiness.WaitAsync(
            client,
            "project",
            ["one", "two"],
            TestContext.Current.CancellationToken,
            delayAsync: (_, _) =>
            {
                delays++;
                return Task.CompletedTask;
            });

        Assert.Equal(3, handler.Bodies.Count);
        Assert.Equal(2, delays);
    }

    [Fact]
    public async Task Equal_count_with_wrong_ids_times_out_instead_of_claiming_readiness()
    {
        using var handler = new Handler(Page(["one", "unexpected"]));
        using var client = IntegrationTestSettings.CreateClient("test-token", handler);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => ProjectItemReadiness.WaitAsync(
            client, "project", ["one", "two"], TestContext.Current.CancellationToken, timeout: TimeSpan.Zero));

        Assert.Contains("omits 1 of 2 mutation-confirmed items", exception.Message, StringComparison.Ordinal);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task Cancellation_during_poll_delay_stops_before_another_query()
    {
        using var handler = new Handler(Page(["one"]));
        using var client = IntegrationTestSettings.CreateClient("test-token", handler);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectItemReadiness.WaitAsync(
            client,
            "project",
            ["one", "two"],
            cancellation.Token,
            delayAsync: (_, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(token);
            }));

        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task Graphql_failure_is_not_replaced_by_readiness_success_or_a_timeout()
    {
        using var handler = new Handler("""{"data":null,"errors":[{"type":"FORBIDDEN","message":"Denied"}]}""");
        using var client = IntegrationTestSettings.CreateClient("test-token", handler);

        var exception = await Assert.ThrowsAsync<GitHubGraphQLException>(() => ProjectItemReadiness.WaitAsync(
            client, "project", ["one"], TestContext.Current.CancellationToken));

        Assert.Equal("FORBIDDEN", exception.ErrorType);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task Duplicate_expected_ids_are_rejected_without_querying()
    {
        using var handler = new Handler();
        using var client = IntegrationTestSettings.CreateClient("test-token", handler);

        await Assert.ThrowsAsync<ArgumentException>(() => ProjectItemReadiness.WaitAsync(
            client, "project", ["one", "one"], TestContext.Current.CancellationToken));

        Assert.Empty(handler.Bodies);
    }

    private static string Page(string[] ids, string? cursor = null) => JsonSerializer.Serialize(new
    {
        data = new
        {
            node = new
            {
                items = new
                {
                    nodes = ids.Select(id => new { id }),
                    pageInfo = new { hasNextPage = cursor is not null, endCursor = cursor },
                },
            },
        },
    });

    private sealed class Handler(params string[] responses) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses[Bodies.Count - 1], Encoding.UTF8, "application/json"),
            };
        }
    }
}
