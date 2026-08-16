using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Integration.Tests;

/// <summary>
/// Live tests for the narrow template write seam: GitHub refuses status update writes on a
/// template project, so <see cref="ProjectTemplateWriteSession"/> temporarily unmarks the
/// target and restores the flag as the final stage. Every test uses a throwaway project
/// created for this run only and deletes it in a finally block.
/// Requires the GHPMV_TEST_TOKEN environment variable (SSO-authorized for the test orgs).
/// </summary>
public class ProjectTemplateWriteSessionTests
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

    private static string NewTestTitle() => "ghpmv-import-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Template_state_is_restored_after_status_update_writes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new GitHubGraphQLClient(Token);

        var (projectId, projectNumber) = await TemporaryProjectFixture.CreateAsync(
            client, TargetOrg, NewTestTitle(), cancellationToken);
        var logDirectory = Directory.CreateTempSubdirectory("ghpmv-status-").FullName;
        try
        {
            await SetTemplateAsync(client, projectId, mark: true, cancellationToken);
            Assert.True(await ReadTemplateFlagAsync(client, projectId, cancellationToken));

            var progress = new List<string>();
            var session = await ProjectTemplateWriteSession.PrepareAsync(
                client, projectId, progress.Add, cancellationToken);

            Assert.True(session.RestorationRequired);
            Assert.False(
                await ReadTemplateFlagAsync(client, projectId, cancellationToken),
                "PrepareAsync must unmark the template before status update writes.");
            Assert.Contains(
                "Temporarily unmarking the target project as a template before status update writes...",
                progress);

            // The write only succeeds because the project is no longer a template.
            var snapshot = SnapshotWithStatusUpdates(NewTestTitle());
            var result = await new StatusUpdateImporter(client).ImportAsync(
                snapshot,
                TemporaryTarget(projectId, projectNumber),
                logDirectory,
                cancellationToken);
            Assert.Equal(2, result.Created);
            Assert.Equal(0, result.Resumed);
            Assert.Equal(0, result.AlreadyComplete);
            Assert.False(await ReadTemplateFlagAsync(client, projectId, cancellationToken));

            await session.RestoreAsync(cancellationToken);

            Assert.True(
                await ReadTemplateFlagAsync(client, projectId, cancellationToken),
                "RestoreAsync must re-mark the project as a template.");
            Assert.Contains(
                "Restoring the target project's template state as the final import stage...",
                progress);

            // Restore is idempotent: the CLI calls it on the happy path and again in finally.
            await session.RestoreAsync(cancellationToken);
            Assert.True(await ReadTemplateFlagAsync(client, projectId, cancellationToken));
            Assert.Equal(
                1,
                progress.Count(message => string.Equals(
                    message,
                    "Restoring the target project's template state as the final import stage...",
                    StringComparison.Ordinal)));
        }
        finally
        {
            await DeleteProjectAsync(client, projectId);
            TryDeleteDirectory(logDirectory);
        }
    }

    [Fact]
    public async Task Prepare_on_a_non_template_project_requires_no_restoration()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new GitHubGraphQLClient(Token);

        var (projectId, projectNumber) = await TemporaryProjectFixture.CreateAsync(
            client, TargetOrg, NewTestTitle(), cancellationToken);
        var logDirectory = Directory.CreateTempSubdirectory("ghpmv-status-").FullName;
        try
        {
            Assert.False(await ReadTemplateFlagAsync(client, projectId, cancellationToken));

            var progress = new List<string>();
            var session = await ProjectTemplateWriteSession.PrepareAsync(
                client, projectId, progress.Add, cancellationToken);

            Assert.False(session.RestorationRequired);
            Assert.Empty(progress);

            var snapshot = SnapshotWithStatusUpdates(NewTestTitle());
            var result = await new StatusUpdateImporter(client).ImportAsync(
                snapshot,
                TemporaryTarget(projectId, projectNumber),
                logDirectory,
                cancellationToken);
            Assert.Equal(2, result.Created);

            await session.RestoreAsync(cancellationToken);

            // A project that was never a template must not become one.
            Assert.False(await ReadTemplateFlagAsync(client, projectId, cancellationToken));
            Assert.Empty(progress);
        }
        finally
        {
            await DeleteProjectAsync(client, projectId);
            TryDeleteDirectory(logDirectory);
        }
    }

    private static async Task<bool> ReadTemplateFlagAsync(
        GitHubGraphQLClient client,
        string projectId,
        CancellationToken cancellationToken)
    {
        var data = await client.QueryAsync(
            """
            query($projectId: ID!) {
              node(id: $projectId) {
                ... on ProjectV2 { id template }
              }
            }
            """,
            new { projectId },
            cancellationToken);
        return data.GetProperty("node").GetProperty("template").GetBoolean();
    }

    private static async Task SetTemplateAsync(
        GitHubGraphQLClient client,
        string projectId,
        bool mark,
        CancellationToken cancellationToken)
    {
        var mutation = mark
            ? """
              mutation($projectId: ID!, $clientMutationId: String!) {
                markProjectV2AsTemplate(input: { projectId: $projectId, clientMutationId: $clientMutationId }) {
                  projectV2 { id template }
                }
              }
              """
            : """
              mutation($projectId: ID!, $clientMutationId: String!) {
                unmarkProjectV2AsTemplate(input: { projectId: $projectId, clientMutationId: $clientMutationId }) {
                  projectV2 { id template }
                }
              }
              """;
        await client.MutationAsync(
            mark ? "markProjectV2AsTemplate" : "unmarkProjectV2AsTemplate",
            mutation,
            new { projectId },
            MutationRetryPolicy.Idempotent,
            target: projectId,
            requiredResultPath: "projectV2.id",
            cancellationToken: cancellationToken);
    }

    private static ImportResult TemporaryTarget(string projectId, int projectNumber) => new()
    {
        ProjectId = projectId,
        ProjectNumber = projectNumber,
        Url = "https://github.com/orgs/" + TargetOrg + "/projects/"
            + projectNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Outcome = ProjectImportOutcome.Created,
        FieldIds = new Dictionary<string, string>(StringComparer.Ordinal),
        OptionIds = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal),
        IterationIds = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal),
    };

    private static ProjectSnapshot SnapshotWithStatusUpdates(string title) => new()
    {
        SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
        Project = new ProjectInfoSnapshot
        {
            Title = title,
            Public = false,
            Closed = false,
        },
        Fields = [],
        Views = [],
        Workflows = [],
        Items = [],
        StatusUpdates =
        [
            new StatusUpdateSnapshot
            {
                Body = "Template session kickoff.",
                Status = "ON_TRACK",
                StartDate = "2026-01-01",
                TargetDate = "2026-03-31",
                Creator = null,
                CreatedAt = "2026-01-01T09:00:00Z",
                UpdatedAt = "2026-01-01T09:00:00Z",
            },
            new StatusUpdateSnapshot
            {
                Body = "Template session complete.",
                Status = "COMPLETE",
                StartDate = null,
                TargetDate = null,
                Creator = null,
                CreatedAt = "2026-01-02T09:00:00Z",
                UpdatedAt = "2026-01-02T09:00:00Z",
            },
        ],
    };

    private static async Task DeleteProjectAsync(GitHubGraphQLClient client, string projectId)
    {
        await client.QueryAsync(
            "mutation($projectId: ID!) { deleteProjectV2(input: { projectId: $projectId }) { projectV2 { id } } }",
            new { projectId },
            CancellationToken.None);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp log directory.
        }
    }
}
