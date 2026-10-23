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
        using var handler = new Handler(
            Page(["one", "unexpected"]),
            """{"data":{"node":null}}""",
            """{"data":{"nodes":[null]}}""");
        using var client = IntegrationTestSettings.CreateClient("test-token", handler);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => ProjectItemReadiness.WaitAsync(
            client, "project", ["one", "two"], TestContext.Current.CancellationToken, timeout: TimeSpan.Zero));

        Assert.Contains("omits 1 of 2 mutation-confirmed items", exception.Message, StringComparison.Ordinal);
        Assert.Equal(3, handler.Bodies.Count);
    }

    [Fact]
    public async Task Timeout_logs_safe_project_connections_and_direct_lookup_with_request_metadata()
    {
        using var handler = new Handler(
            Page([]),
            """
            {"data":{"node":{"__typename":"ProjectV2","id":"project","number":42,"closed":false,"viewerCanUpdate":true,
              "includingArchived":{"totalCount":0,"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}},
              "defaultItems":{"totalCount":1,"nodes":[{"id":"one"}],"pageInfo":{"hasNextPage":true,"endCursor":"cursor"}},
              "title":"PRIVATE-PROJECT-TITLE"}}}
            """,
            """
            {"data":{"nodes":[{"__typename":"ProjectV2Item","id":"one","isArchived":false,"project":{"id":"project"},
              "content":{"title":"PRIVATE-DRAFT-TITLE"}},null]}}
            """);
        using var client = IntegrationTestSettings.CreateClient("PRIVATE-TOKEN", handler);
        List<string> output = [];

        await Assert.ThrowsAsync<TimeoutException>(() => ProjectItemReadiness.WaitAsync(
            client, "project", ["one", "two"], TestContext.Current.CancellationToken,
            timeout: TimeSpan.Zero, writeDiagnostic: output.Add));

        var log = string.Join("\n", output);
        Assert.Contains("projectId=project", log, StringComparison.Ordinal);
        Assert.Contains("missingIdSample=[\"one\",\"two\"]", log, StringComparison.Ordinal);
        Assert.Contains("\"includingArchived\":{\"totalCount\":0", log, StringComparison.Ordinal);
        Assert.Contains("\"defaultItems\":{\"totalCount\":1", log, StringComparison.Ordinal);
        Assert.Contains("\"hasNextPage\":true,\"endCursor\":\"cursor\"", log, StringComparison.Ordinal);
        Assert.Contains("\"isArchived\":false,\"projectId\":\"project\"},null]", log, StringComparison.Ordinal);
        Assert.DoesNotContain("requestId", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("request-", log, StringComparison.Ordinal);
        Assert.Contains("\"status\":200", log, StringComparison.Ordinal);
        Assert.Contains("\"rateLimitRemaining\":\"4999\"", log, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", log, StringComparison.Ordinal);
        using var probe = JsonDocument.Parse(handler.Bodies[2]);
        Assert.Equal(2, probe.RootElement.GetProperty("variables").GetProperty("ids").GetArrayLength());
    }

    [Fact]
    public async Task Timeout_bounds_missing_id_samples_and_probe_requests()
    {
        var ids = Enumerable.Range(1, 120).Select(index => $"item-{index}").ToArray();
        using var handler = new Handler(
            Page([]), """{"data":{"node":null}}""", """{"data":{"nodes":[null,null,null,null,null]}}""");
        using var client = IntegrationTestSettings.CreateClient("test-token", handler);
        List<string> output = [];

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => ProjectItemReadiness.WaitAsync(
            client, "project", ids, TestContext.Current.CancellationToken,
            timeout: TimeSpan.Zero, writeDiagnostic: output.Add));

        Assert.Contains("omits 120 of 120", exception.Message, StringComparison.Ordinal);
        Assert.Equal(3, handler.Bodies.Count);
        using var probe = JsonDocument.Parse(handler.Bodies[2]);
        Assert.Equal(5, probe.RootElement.GetProperty("variables").GetProperty("ids").GetArrayLength());
        Assert.Contains(output, line => line.EndsWith("(project-connections): null", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"data":null,"errors":[{"type":"FORBIDDEN","message":"PRIVATE-ERROR"}]}""")]
    [InlineData("""{"data":{"node":{}}}""")]
    public async Task Probe_failures_do_not_mask_timeout_or_prevent_the_other_probe(string response)
    {
        using var handler = new Handler(Page([]), response, """{"data":{"nodes":[null]}}""");
        using var client = IntegrationTestSettings.CreateClient("test-token", handler);
        List<string> output = [];

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => ProjectItemReadiness.WaitAsync(
            client, "project", ["one"], TestContext.Current.CancellationToken,
            timeout: TimeSpan.Zero, writeDiagnostic: output.Add));

        Assert.Contains("omits 1 of 1", exception.Message, StringComparison.Ordinal);
        Assert.Contains(output, line => line.Contains("preserving the original readiness timeout", StringComparison.Ordinal));
        Assert.Contains(output, line => line.Contains("(missing-item-nodes): [null]", StringComparison.Ordinal));
        Assert.DoesNotContain("PRIVATE", string.Join("\n", output), StringComparison.Ordinal);
        Assert.DoesNotContain("requestId", string.Join("\n", output), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("request-", string.Join("\n", output), StringComparison.Ordinal);
        Assert.Equal(3, handler.Bodies.Count);
    }

    [Fact]
    public async Task Api_trace_retains_only_five_recent_requests_and_never_payloads()
    {
        using var handler = new Handler(Enumerable.Repeat("""{"data":{"secret":"PRIVATE-RESPONSE"}}""", 8).ToArray());
        using var client = IntegrationTestSettings.CreateClient("PRIVATE-TOKEN", handler);
        List<string> output = [];
        for (var index = 0; index < 8; index++)
        {
            await client.QueryAsync("query { secret }", new { secret = "PRIVATE-REQUEST" }, TestContext.Current.CancellationToken);
        }

        IntegrationApiTrace.WriteRecentRequests(client, "test", output.Add);
        IntegrationApiTrace.WriteRecentRequests(client, "drained", output.Add);

        Assert.Equal(5, output.Count);
        Assert.Contains("\"sequence\":4", output[0], StringComparison.Ordinal);
        Assert.Contains("\"sequence\":8", output[^1], StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", string.Join("\n", output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Readiness_logs_preceding_creation_request_without_adding_probes_on_success()
    {
        using var handler = new Handler("""{"data":{"created":{"id":"one"}}}""", Page(["one"]));
        using var client = IntegrationTestSettings.CreateClient("test-token", handler);
        List<string> output = [];
        await client.QueryAsync("mutation { create }", cancellationToken: TestContext.Current.CancellationToken);

        await ProjectItemReadiness.WaitAsync(
            client, "project", ["one"], TestContext.Current.CancellationToken, writeDiagnostic: output.Add);

        Assert.Contains(output, line => line.StartsWith("Project item API trace (before-poll):", StringComparison.Ordinal)
            && line.Contains("\"sequence\":1", StringComparison.Ordinal));
        Assert.Contains(output, line => line.StartsWith("Project item API trace (poll):", StringComparison.Ordinal)
            && line.Contains("\"sequence\":2", StringComparison.Ordinal));
        Assert.Equal(2, handler.Bodies.Count);
        Assert.DoesNotContain(output, line => line.Contains("probe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancellation_during_diagnostics_preserves_timeout_and_stops_sending_requests()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var handler = new Handler(Page([]), """{"data":{"node":null}}""")
        {
            OnRequest = count =>
            {
                if (count == 2)
                {
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                }
            },
        };
        using var client = IntegrationTestSettings.CreateClient("test-token", handler);
        List<string> output = [];

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => ProjectItemReadiness.WaitAsync(
            client, "project", ["one"], cancellation.Token, timeout: TimeSpan.Zero, writeDiagnostic: output.Add));

        Assert.Contains("omits 1 of 1", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(2, output.Count(line => line.Contains("preserving the original readiness timeout", StringComparison.Ordinal)));
        Assert.Contains(output, line => line.Contains("\"status\":null", StringComparison.Ordinal));
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
        public Action<int>? OnRequest { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            OnRequest?.Invoke(Bodies.Count);
            if (Bodies.Count > responses.Length)
            {
                throw new InvalidOperationException(
                    $"Unexpected request #{Bodies.Count}; only {responses.Length} responses were configured.");
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses[Bodies.Count - 1], Encoding.UTF8, "application/json"),
            };
            response.Headers.Add("X-GitHub-Request-Id", $"request-{Bodies.Count}");
            response.Headers.Add("X-RateLimit-Remaining", "4999");
            return response;
        }
    }
}
