using Ghpmv.Core.GitHub;

namespace Ghpmv.Integration.Tests;

internal static class TemporaryProjectFixture
{
    public static async Task<(string Id, int Number)> CreateAsync(
        GitHubGraphQLClient client,
        string organization,
        string title,
        CancellationToken cancellationToken)
    {
        var ownerData = await client.QueryAsync(
            "query($login: String!) { organization(login: $login) { id } }",
            new { login = organization },
            cancellationToken);
        var ownerId = ownerData.GetProperty("organization").GetProperty("id").GetString()!;
        var clientMutationId = Guid.NewGuid().ToString("N");
        var projectData = await client.MutationAsync(
            "createProjectV2",
            """
            mutation($ownerId: ID!, $title: String!, $clientMutationId: String!) {
              createProjectV2(input: { ownerId: $ownerId, title: $title, clientMutationId: $clientMutationId }) {
                projectV2 { id number }
              }
            }
            """,
            new { ownerId, title },
            target: ownerId,
            clientMutationId: clientMutationId,
            requiredResultPath: "projectV2.id",
            cancellationToken: cancellationToken);
        var project = projectData.GetProperty("createProjectV2").GetProperty("projectV2");
        return (project.GetProperty("id").GetString()!, project.GetProperty("number").GetInt32());
    }

    public static async Task DeleteAllByTitleAsync(
        GitHubGraphQLClient client,
        string organization,
        string title,
        CancellationToken cancellationToken)
    {
        var projectIds = new List<string>();
        await foreach (var node in client.QueryPaginatedAsync(
            """
            query($login: String!, $first: Int!, $after: String) {
              organization(login: $login) {
                projectsV2(first: $first, after: $after) {
                  nodes { id title }
                  pageInfo { hasNextPage endCursor }
                }
              }
            }
            """,
            new { login = organization, first = 100 },
            "organization.projectsV2",
            cancellationToken: cancellationToken))
        {
            if (string.Equals(node.GetProperty("title").GetString(), title, StringComparison.Ordinal))
            {
                projectIds.Add(node.GetProperty("id").GetString()!);
            }
        }

        foreach (var projectId in projectIds)
        {
            await client.MutationAsync(
                "deleteProjectV2",
                """
                mutation($projectId: ID!, $clientMutationId: String!) {
                  deleteProjectV2(input: { projectId: $projectId, clientMutationId: $clientMutationId }) {
                    projectV2 { id }
                  }
                }
                """,
                new { projectId },
                MutationRetryPolicy.Idempotent,
                target: projectId,
                requiredResultPath: "projectV2.id",
                cancellationToken: cancellationToken);
        }
    }
}
