using Ghpmv.Core.Export;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;

namespace Ghpmv.Integration.Tests;

/// <summary>
/// User-owned project support: creates a temporary project owned by the viewer (the test
/// account) via the real API, exports it with <see cref="ProjectOwnerType.User"/>, deletes
/// the source, re-imports it as a user project and compares the read-back. A credentialed
/// integration account must be allowed to create user-owned projects.
/// Every created project is deleted in a finally block.
/// </summary>
public class UserProjectTests
{
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
    public async Task User_project_export_import_round_trip()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = IntegrationTestSettings.CreateClient(Token);

        var viewer = await client.QueryAsync("query { viewer { id login } }", cancellationToken: cancellationToken);
        var viewerId = viewer.GetProperty("viewer").GetProperty("id").GetString()!;
        var viewerLogin = viewer.GetProperty("viewer").GetProperty("login").GetString()!;

        var title = "ghpmv-user-test-" + Guid.NewGuid().ToString("N");
        string? sourceProjectId = null;
        string? importedProjectId = null;
        try
        {
            // Create a temporary user project with one custom field.
            int sourceNumber;
            try
            {
                var created = await client.QueryAsync(
                    """
                    mutation($ownerId: ID!, $title: String!) {
                      createProjectV2(input: { ownerId: $ownerId, title: $title }) {
                        projectV2 { id number url }
                      }
                    }
                    """,
                    new { ownerId = viewerId, title },
                    cancellationToken);
                var project = created.GetProperty("createProjectV2").GetProperty("projectV2");
                sourceProjectId = project.GetProperty("id").GetString()!;
                sourceNumber = project.GetProperty("number").GetInt32();
                Assert.Contains($"/users/{viewerLogin}/projects/", project.GetProperty("url").GetString(), StringComparison.OrdinalIgnoreCase);
            }
            catch (GitHubGraphQLException exception)
            {
                throw new InvalidOperationException(
                    $"Credentialed integration account '{viewerLogin}' must be able to create user-owned projects " +
                    $"({exception.ErrorType ?? "error"}).",
                    exception);
            }

            await client.QueryAsync(
                """
                mutation($projectId: ID!) {
                  createProjectV2Field(input: { projectId: $projectId, name: "User Text", dataType: TEXT }) {
                    projectV2Field { ... on ProjectV2FieldCommon { id } }
                  }
                }
                """,
                new { projectId = sourceProjectId },
                cancellationToken);

            // Export as a user project.
            var exporter = new ProjectExporter(client) { OwnerType = ProjectOwnerType.User };
            var snapshot = await exporter.ExportAsync(viewerLogin, sourceNumber, cancellationToken);
            Assert.Equal(title, snapshot.Project.Title);
            Assert.Contains(snapshot.Fields, f => f.Name == "User Text" && f.DataType == "TEXT");

            // The user's project list must include the temporary project.
            var listed = await exporter.ListProjectsAsync(viewerLogin, includeClosed: false, cancellationToken);
            Assert.Contains(listed, p => p.Number == sourceNumber && p.Title == title);

            // Delete the source so the re-import does not hit the title-conflict path.
            await DeleteProjectAsync(client, sourceProjectId);
            sourceProjectId = null;

            // Import the snapshot back as a user project and read it back.
            var importer = new ProjectImporter(client)
            {
                OwnerType = ProjectOwnerType.User,
                OperationLogDirectory = IntegrationTestSettings.CreateOperationLogDirectory(),
            };
            var result = await importer.ImportAsync(snapshot, viewerLogin, cancellationToken);
            importedProjectId = result.ProjectId;

            Assert.True(result.Created);
            Assert.Contains($"/users/{viewerLogin}/projects/", result.Url, StringComparison.OrdinalIgnoreCase);
            Assert.True(result.FieldIds.ContainsKey("User Text"));

            var readBack = await exporter.ExportAsync(viewerLogin, result.ProjectNumber, cancellationToken);
            Assert.Equal(title, readBack.Project.Title);
            Assert.Contains(readBack.Fields, f => f.Name == "User Text" && f.DataType == "TEXT");
        }
        finally
        {
            if (sourceProjectId is not null)
            {
                await DeleteProjectAsync(client, sourceProjectId);
            }

            if (importedProjectId is not null)
            {
                await DeleteProjectAsync(client, importedProjectId);
            }
        }
    }

    [Fact]
    public async Task Single_select_requires_options_for_creation_and_ignores_empty_option_updates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = IntegrationTestSettings.CreateClient(Token);
        var viewer = await client.QueryAsync("query { viewer { id } }", cancellationToken: cancellationToken);
        var viewerId = viewer.GetProperty("viewer").GetProperty("id").GetString()!;
        string? projectId = null;
        try
        {
            var created = await client.QueryAsync(
                """
                mutation($ownerId: ID!, $title: String!) {
                  createProjectV2(input: { ownerId: $ownerId, title: $title }) {
                    projectV2 { id }
                  }
                }
                """,
                new { ownerId = viewerId, title = "ghpmv-empty-select-test-" + Guid.NewGuid().ToString("N") },
                cancellationToken);
            projectId = created.GetProperty("createProjectV2").GetProperty("projectV2").GetProperty("id").GetString()!;

            foreach (var optionsInput in new[] { "", ", singleSelectOptions: null", ", singleSelectOptions: []" })
            {
                var exception = await Assert.ThrowsAsync<GitHubGraphQLException>(() => client.QueryAsync(
                    """
                    mutation($projectId: ID!) {
                      createProjectV2Field(input: { projectId: $projectId, name: "Priority", dataType: SINGLE_SELECT
                    """ + optionsInput + """
                      }) {
                        projectV2Field { ... on ProjectV2FieldCommon { id } }
                      }
                    }
                    """,
                    new { projectId },
                    cancellationToken));
                Assert.Equal("UNPROCESSABLE", exception.ErrorType);
            }

            var fieldData = await client.QueryAsync(
                """
                mutation($projectId: ID!) {
                  createProjectV2Field(input: {
                    projectId: $projectId, name: "Priority", dataType: SINGLE_SELECT,
                    singleSelectOptions: [{ name: "Low", color: GREEN, description: "Low priority" }]
                  }) {
                    projectV2Field { ... on ProjectV2SingleSelectField { id options { id name } } }
                  }
                }
                """,
                new { projectId },
                cancellationToken);
            var field = fieldData.GetProperty("createProjectV2Field").GetProperty("projectV2Field");
            var fieldId = field.GetProperty("id").GetString()!;
            var sourceOption = Assert.Single(field.GetProperty("options").EnumerateArray());
            var optionId = sourceOption.GetProperty("id").GetString();
            Assert.Equal("Low", sourceOption.GetProperty("name").GetString());

            var updated = await client.QueryAsync(
                """
                mutation($fieldId: ID!) {
                  updateProjectV2Field(input: { fieldId: $fieldId, singleSelectOptions: [] }) {
                    projectV2Field { ... on ProjectV2SingleSelectField { id options { id name } } }
                  }
                }
                """,
                new { fieldId },
                cancellationToken);
            var updatedOption = Assert.Single(updated.GetProperty("updateProjectV2Field").GetProperty("projectV2Field").GetProperty("options").EnumerateArray());
            Assert.Equal(optionId, updatedOption.GetProperty("id").GetString());
            Assert.Equal("Low", updatedOption.GetProperty("name").GetString());

            var readBack = await client.QueryAsync(
                """
                query($fieldId: ID!) {
                  node(id: $fieldId) { ... on ProjectV2SingleSelectField { options { id name } } }
                }
                """,
                new { fieldId },
                cancellationToken);
            var readBackOption = Assert.Single(readBack.GetProperty("node").GetProperty("options").EnumerateArray());
            Assert.Equal(optionId, readBackOption.GetProperty("id").GetString());
            Assert.Equal("Low", readBackOption.GetProperty("name").GetString());
        }
        finally
        {
            if (projectId is not null)
            {
                await DeleteProjectAsync(client, projectId);
            }
        }
    }

    private static async Task DeleteProjectAsync(GitHubGraphQLClient client, string projectId)
    {
        await client.QueryAsync(
            "mutation($projectId: ID!) { deleteProjectV2(input: { projectId: $projectId }) { projectV2 { id } } }",
            new { projectId },
            CancellationToken.None);
    }
}
