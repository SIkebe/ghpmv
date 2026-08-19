using System.Net;
using Ghpmv.Core.Export;
using Ghpmv.Core.GitHub;

namespace Ghpmv.Integration.Tests;

/// <summary>
/// M1 integration tests against the real GitHub GraphQL API.
/// Requires the GHPMV_TEST_TOKEN environment variable (SSO-authorized for the test orgs).
/// Skipped when the variable is not set (e.g. fork PRs without secrets).
/// </summary>
public class GraphQLClientIntegrationTests
{
    private static string Org => IntegrationTestSettings.SourceOrg;

    private static string Token
    {
        get
        {
            var token = Environment.GetEnvironmentVariable("GHPMV_TEST_TOKEN");
            Assert.SkipWhen(string.IsNullOrWhiteSpace(token), "GHPMV_TEST_TOKEN is not set; skipping real-API test.");
            return token!;
        }
    }

    [Fact]
    public async Task Fixture_project_export_is_complete_or_fails_closed()
    {
        using var client = IntegrationTestSettings.CreateClient(Token);

        try
        {
            var snapshot = await new ProjectExporter(client).ExportAsync(
                Org,
                IntegrationTestSettings.FixtureProjectNumber,
                TestContext.Current.CancellationToken);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.Project.Title));
            foreach (var fieldName in (string[])
                     [
                         "Fixture Text",
                         "Fixture Number",
                         "Fixture Date",
                         "Fixture Select",
                         "Fixture Sprint",
                         "Fixture Teams",
                     ])
            {
                Assert.Contains(snapshot.Fields, field => field.Name == fieldName);
            }
        }
        catch (GitHubGraphQLException exception)
        {
            Assert.Contains("No snapshot was written", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task QueryPaginatedAsync_and_ProjectExporter_enumerate_all_items_across_real_pages()
    {
        using var client = IntegrationTestSettings.CreateClient(Token);
        var cancellationToken = TestContext.Current.CancellationToken;

        var orgData = await client.QueryAsync(
            "query($login: String!) { organization(login: $login) { id } }",
            new { login = Org },
            cancellationToken);
        var ownerId = orgData.GetProperty("organization").GetProperty("id").GetString()!;

        var title = $"ghpmv-test-{Guid.NewGuid():N}";
        var createData = await client.QueryAsync(
            "mutation($ownerId: ID!, $title: String!) { createProjectV2(input: {ownerId: $ownerId, title: $title}) { projectV2 { id number } } }",
            new { ownerId, title },
            cancellationToken);
        var project = createData.GetProperty("createProjectV2").GetProperty("projectV2");
        var projectId = project.GetProperty("id").GetString()!;
        var projectNumber = project.GetProperty("number").GetInt32();

        try
        {
            var createdTitles = Enumerable.Range(1, 120)
                .Select(i => $"Draft {i:D3}")
                .ToArray();

            // Serial on purpose: parallel writes would trip the secondary rate limit.
            foreach (var draftTitle in createdTitles)
            {
                await client.QueryAsync(
                    "mutation($projectId: ID!, $title: String!) { addProjectV2DraftIssue(input: {projectId: $projectId, title: $title}) { projectItem { id } } }",
                    new { projectId, title = draftTitle },
                    cancellationToken);
            }

            // The items connection is eventually consistent right after writes,
            // so poll until all 120 items are visible (up to ~75s).
            List<string> directTitles = [];
            for (var attempt = 0; attempt < 16; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }

                directTitles = [];
                await foreach (var node in client.QueryPaginatedAsync(
                    """
                    query($projectId: ID!, $first: Int!, $after: String) {
                      node(id: $projectId) {
                        ... on ProjectV2 {
                          items(first: $first, after: $after, archivedStates: [ARCHIVED, NOT_ARCHIVED]) {
                            nodes {
                              content {
                                ... on DraftIssue { title }
                              }
                            }
                            pageInfo { hasNextPage endCursor }
                          }
                        }
                      }
                    }
                    """,
                    new { projectId, first = 50 },
                    "node.items",
                    cancellationToken: cancellationToken))
                {
                    directTitles.Add(node.GetProperty("content").GetProperty("title").GetString()!);
                }

                if (directTitles.Count >= 120)
                {
                    break;
                }
            }

            Assert.Equal(120, directTitles.Count);
            Assert.Equal(120, directTitles.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(
                createdTitles.Order(StringComparer.Ordinal),
                directTitles.Order(StringComparer.Ordinal));

            var exporter = new ProjectExporter(client);
            IReadOnlyList<Ghpmv.Core.Snapshot.ItemSnapshot> exportedItems = [];
            for (var attempt = 0; attempt < 16; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }

                var snapshot = await exporter.ExportAsync(Org, projectNumber, cancellationToken);
                exportedItems = snapshot.Items;
                if (exportedItems.Count >= 120)
                {
                    break;
                }
            }

            Assert.Equal(120, exportedItems.Count);
            Assert.All(exportedItems, item =>
            {
                Assert.Equal("DRAFT_ISSUE", item.Type);
                Assert.NotNull(item.Draft);
            });

            var exportedTitles = exportedItems.Select(item => item.Draft!.Title).ToArray();
            Assert.Equal(120, exportedTitles.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(
                directTitles.Order(StringComparer.Ordinal),
                exportedTitles.Order(StringComparer.Ordinal));
            Assert.Equal(directTitles, exportedTitles);
            Assert.Equal(Enumerable.Range(0, 120), exportedItems.Select(item => item.Position));
        }
        finally
        {
            await client.QueryAsync(
                "mutation($projectId: ID!) { deleteProjectV2(input: {projectId: $projectId}) { projectV2 { id } } }",
                new { projectId },
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task Invalid_token_fails_with_401_without_retrying()
    {
        _ = Token; // Skip when no real-API access is configured.

        using var client = IntegrationTestSettings.CreateClient("invalid-token");
        var retries = 0;
        client.OnRetry = _ => retries++;

        var exception = await Assert.ThrowsAsync<GitHubGraphQLException>(
            () => client.GetViewerLoginAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal(0, retries);
    }
}
