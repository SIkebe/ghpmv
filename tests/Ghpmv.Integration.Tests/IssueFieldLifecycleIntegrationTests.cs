using Ghpmv.Core.Export;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Integration.Tests;

/// <summary>
/// Verifies the organization Issue Field create, link, update, and delete lifecycle
/// against the real GraphQL API without relying on the shared fixture catalog.
/// </summary>
public class IssueFieldLifecycleIntegrationTests
{
    private static string TargetOrg => IntegrationTestSettings.TargetOrg;

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
    public async Task Import_creates_links_updates_and_deletes_organization_issue_field()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new GitHubGraphQLClient(Token);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var fieldName = $"ghpmv-ci-if-{suffix}";
        var title = $"ghpmv-issue-field-test-{suffix}";
        var createLogDirectory = IntegrationTestSettings.CreateOperationLogDirectory();
        var updateLogDirectory = IntegrationTestSettings.CreateOperationLogDirectory();
        string? projectId = null;
        string? issueFieldId = null;

        try
        {
            var project = await CreateProjectAsync(client, title, cancellationToken);
            projectId = project.Id;
            var initial = Snapshot(
                title,
                IssueField(
                    fieldName,
                    description: "Created by the ghpmv live API test.",
                    visibility: "ALL",
                    Option("Alpha", "RED", "First option"),
                    Option("Beta", "BLUE", "Second option")));

            var created = await new ProjectImporter(client)
            {
                OperationLogDirectory = createLogDirectory,
            }.ImportIntoAsync(initial, TargetOrg, project.Number, cancellationToken);

            Assert.Empty(created.FieldIds);
            issueFieldId = Assert.Contains(fieldName, created.IssueFieldIds);
            Assert.Equal(["Alpha", "Beta"], created.IssueFieldOptionIds[fieldName].Keys);
            var createdField = await ExportUntilIssueFieldMatchesAsync(
                client,
                project.Number,
                initial.Fields.Single(),
                cancellationToken);
            AssertIssueField(initial.Fields.Single(), createdField);

            var updated = Snapshot(
                title,
                IssueField(
                    fieldName,
                    description: "Updated by the ghpmv live API test.",
                    visibility: "ORG_ONLY",
                    // GitHub rejects an update payload that reuses an existing option name.
                    Option("Gamma", "GREEN", "Third option"),
                    Option("Delta", "YELLOW", "Fourth option")));
            var updateResult = await new ProjectImporter(client)
            {
                OperationLogDirectory = updateLogDirectory,
            }.ImportIntoAsync(updated, TargetOrg, project.Number, cancellationToken);

            Assert.Equal(issueFieldId, updateResult.IssueFieldIds[fieldName]);
            Assert.Equal(["Gamma", "Delta"], updateResult.IssueFieldOptionIds[fieldName].Keys);
            var updatedField = await ExportUntilIssueFieldMatchesAsync(
                client,
                project.Number,
                updated.Fields.Single(),
                cancellationToken);
            AssertIssueField(updated.Fields.Single(), updatedField);

            await DeleteProjectAsync(client, projectId);
            projectId = null;
            await DeleteIssueFieldAsync(client, issueFieldId);
            issueFieldId = null;
            await WaitUntilIssueFieldIsDeletedAsync(client, fieldName, cancellationToken);
        }
        finally
        {
            try
            {
                if (projectId is not null)
                {
                    await DeleteProjectAsync(client, projectId);
                }
            }
            finally
            {
                try
                {
                    var remainingIssueFieldId = await FindIssueFieldIdAsync(
                        client,
                        fieldName,
                        CancellationToken.None);
                    if (remainingIssueFieldId is not null)
                    {
                        await DeleteIssueFieldAsync(client, remainingIssueFieldId);
                    }
                }
                finally
                {
                    DeleteDirectoryIfPresent(createLogDirectory);
                    DeleteDirectoryIfPresent(updateLogDirectory);
                }
            }
        }
    }

    private static ProjectSnapshot Snapshot(string title, FieldSnapshot issueField) => new()
    {
        SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
        Project = new ProjectInfoSnapshot
        {
            Title = title,
            Public = false,
            Closed = false,
        },
        Fields = [issueField],
        Views = [],
        Workflows = [],
        Items = [],
    };

    private static FieldSnapshot IssueField(
        string name,
        string description,
        string visibility,
        params SingleSelectOptionSnapshot[] options) => new()
    {
        Name = name,
        DataType = "MULTI_SELECT",
        Options = options,
        IssueField = new IssueFieldConfigurationSnapshot
        {
            Description = description,
            Visibility = visibility,
        },
    };

    private static SingleSelectOptionSnapshot Option(
        string name,
        string color,
        string description) => new()
    {
        Id = name,
        Name = name,
        Color = color,
        Description = description,
    };

    private static async Task<FieldSnapshot> ExportUntilIssueFieldMatchesAsync(
        GitHubGraphQLClient client,
        int projectNumber,
        FieldSnapshot expected,
        CancellationToken cancellationToken)
    {
        var apiExporter = new ProjectExporter(client);
        FieldSnapshot? field = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }

            var apiSnapshot = await apiExporter.ExportAsync(
                TargetOrg,
                projectNumber,
                cancellationToken);
            if (!apiSnapshot.Fields.Any(candidate => candidate.Name == expected.Name))
            {
                continue;
            }

            var catalog = new ProjectFieldCatalog
            {
                Entries =
                [
                    .. apiSnapshot.Fields.Select(candidate =>
                        new ProjectFieldCatalogEntry(
                            candidate.Name == expected.Name ? expected : candidate,
                            IsIssueField: candidate.Name == expected.Name)),
                ],
            };
            var completeExporter = new ProjectExporter(client)
            {
                CompleteFieldCatalogProviderAsync = (_, _) => Task.FromResult(catalog),
            };
            var completeSnapshot = await completeExporter.ExportAsync(
                TargetOrg,
                projectNumber,
                cancellationToken);
            field = completeSnapshot.Fields.SingleOrDefault(candidate => candidate.Name == expected.Name);
            if (field is not null
                && field.DataType == expected.DataType
                && field.IssueField == expected.IssueField
                && OptionsMatch(expected.Options, field.Options))
            {
                return field;
            }
        }

        return field ?? throw new InvalidOperationException(
            $"Issue Field '{expected.Name}' was not linked to project #{projectNumber}.");
    }

    private static void AssertIssueField(FieldSnapshot expected, FieldSnapshot actual)
    {
        Assert.Equal(expected.DataType, actual.DataType);
        Assert.Equal(expected.IssueField, actual.IssueField);
        Assert.Equal(
            expected.Options!.Select(option => (option.Name, option.Color, option.Description)),
            actual.Options!.Select(option => (option.Name, option.Color, option.Description)));
    }

    private static bool OptionsMatch(
        IReadOnlyList<SingleSelectOptionSnapshot>? expected,
        IReadOnlyList<SingleSelectOptionSnapshot>? actual)
        => expected is not null
            && actual is not null
            && expected.Select(option => (option.Name, option.Color, option.Description))
                .SequenceEqual(actual.Select(option => (option.Name, option.Color, option.Description)));

    private static async Task<(string Id, int Number)> CreateProjectAsync(
        GitHubGraphQLClient client,
        string title,
        CancellationToken cancellationToken)
    {
        var ownerData = await client.QueryAsync(
            "query($login: String!) { organization(login: $login) { id } }",
            new { login = TargetOrg },
            cancellationToken);
        var projectData = await client.QueryAsync(
            """
            mutation($ownerId: ID!, $title: String!) {
              createProjectV2(input: { ownerId: $ownerId, title: $title }) {
                projectV2 { id number }
              }
            }
            """,
            new { ownerId = ownerData.GetProperty("organization").GetProperty("id").GetString(), title },
            cancellationToken);
        var project = projectData.GetProperty("createProjectV2").GetProperty("projectV2");
        return (project.GetProperty("id").GetString()!, project.GetProperty("number").GetInt32());
    }

    private static async Task DeleteProjectAsync(GitHubGraphQLClient client, string projectId)
    {
        await client.QueryAsync(
            "mutation($projectId: ID!) { deleteProjectV2(input: { projectId: $projectId }) { projectV2 { id } } }",
            new { projectId },
            CancellationToken.None);
    }

    private static async Task DeleteIssueFieldAsync(GitHubGraphQLClient client, string issueFieldId)
    {
        var clientMutationId = Guid.NewGuid().ToString("N");
        var data = await client.QueryAsync(
            """
            mutation($fieldId: ID!, $clientMutationId: String!) {
              deleteIssueField(input: { fieldId: $fieldId, clientMutationId: $clientMutationId }) {
                clientMutationId
              }
            }
            """,
            new { fieldId = issueFieldId, clientMutationId },
            CancellationToken.None);
        Assert.Equal(
            clientMutationId,
            data.GetProperty("deleteIssueField").GetProperty("clientMutationId").GetString());
    }

    private static async Task WaitUntilIssueFieldIsDeletedAsync(
        GitHubGraphQLClient client,
        string fieldName,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (await FindIssueFieldIdAsync(client, fieldName, cancellationToken) is null)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        Assert.Fail($"Issue Field '{fieldName}' was still present after deletion.");
    }

    private static async Task<string?> FindIssueFieldIdAsync(
        GitHubGraphQLClient client,
        string fieldName,
        CancellationToken cancellationToken)
    {
        await foreach (var node in client.QueryPaginatedAsync(
            """
            query($login: String!, $first: Int!, $after: String) {
              organization(login: $login) {
                issueFields(first: $first, after: $after) {
                  nodes {
                    ... on IssueFieldCommon { name }
                    ... on IssueFieldText { id }
                    ... on IssueFieldNumber { id }
                    ... on IssueFieldDate { id }
                    ... on IssueFieldSingleSelect { id }
                    ... on IssueFieldMultiSelect { id }
                  }
                  pageInfo { hasNextPage endCursor }
                }
              }
            }
            """,
            new { login = TargetOrg, first = 100 },
            "organization.issueFields",
            cancellationToken: cancellationToken))
        {
            if (string.Equals(node.GetProperty("name").GetString(), fieldName, StringComparison.Ordinal))
            {
                return node.GetProperty("id").GetString();
            }
        }

        return null;
    }

    private static void DeleteDirectoryIfPresent(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
