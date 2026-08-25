using Ghpmv.Core.Export;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;
using Ghpmv.Core.Verify;

namespace Ghpmv.Integration.Tests;

public class ProjectTemplateIntegrationTests
{
    private static string SourceOrg => IntegrationTestSettings.SourceOrg;

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Template_state_round_trips_through_export_import_and_verify(bool template)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = IntegrationTestSettings.CreateClient(Token);
        var sourceDirectory = IntegrationTestSettings.CreateOperationLogDirectory();
        var targetDirectory = IntegrationTestSettings.CreateOperationLogDirectory();
        ImportResult? source = null;
        ImportResult? target = null;

        try
        {
            source = await ImportCompleteSnapshotAsync(
                client,
                SourceOrg,
                Snapshot(NewTitle("source"), template, includeStatusUpdate: true),
                sourceDirectory,
                addStatusAttribution: false,
                registerProject: result => source = result,
                cancellationToken);
            var exported = await new ProjectExporter(client).ExportAsync(
                SourceOrg,
                source.ProjectNumber,
                cancellationToken);
            Assert.Equal(template, exported.Project.Template);

            var targetSnapshot = exported with
            {
                Project = exported.Project with { Title = NewTitle("target") },
            };
            target = await ImportCompleteSnapshotAsync(
                client,
                TargetOrg,
                targetSnapshot,
                targetDirectory,
                addStatusAttribution: true,
                registerProject: result => target = result,
                cancellationToken);

            var report = await new ProjectVerifier(client).VerifyAsync(
                targetSnapshot,
                TargetOrg,
                target.ProjectNumber,
                cancellationToken);
            Assert.Contains(report.Categories, category =>
                category.Category == "Project" && category.Status == VerifyStatus.Match);
            Assert.Contains(report.Categories, category =>
                category.Category == "StatusUpdate" && category.Status == VerifyStatus.Match);
            Assert.DoesNotContain(report.Differences, difference =>
                difference.Severity == VerifySeverity.Error
                && difference.Category is not "View" and not "Workflow");

            var reloaded = await new ProjectExporter(client).ExportAsync(
                TargetOrg,
                target.ProjectNumber,
                cancellationToken);
            Assert.Equal(template, reloaded.Project.Template);
            Assert.Single(reloaded.StatusUpdates!);
        }
        finally
        {
            try
            {
                if (target is not null)
                {
                    await DeleteProjectAsync(client, target.ProjectId);
                }
            }
            finally
            {
                try
                {
                    if (source is not null)
                    {
                        await DeleteProjectAsync(client, source.ProjectId);
                    }
                }
                finally
                {
                    TryDeleteDirectory(sourceDirectory);
                    TryDeleteDirectory(targetDirectory);
                }
             }
        }
    }

    private static async Task<ImportResult> ImportCompleteSnapshotAsync(
        GitHubGraphQLClient client,
        string organization,
        ProjectSnapshot snapshot,
        string directory,
        bool addStatusAttribution,
        Action<ImportResult> registerProject,
        CancellationToken cancellationToken)
    {
        var result = await new ProjectImporter(client)
        {
            OperationLogDirectory = directory,
        }.ImportAsync(snapshot, organization, cancellationToken);
        registerProject(result);
        await new ItemImporter(client).ImportAsync(snapshot, result, directory, cancellationToken);

        ProjectTemplateWriteSession? templateSession = null;
        if (snapshot.StatusUpdates is { Count: > 0 })
        {
            var log = await ImportLog.LoadAsync(directory, cancellationToken)
                ?? new ImportLog
                {
                    ProjectId = result.ProjectId,
                    SourceSnapshotFingerprint = ImportLog.ComputeSnapshotFingerprint(snapshot),
                };
            async Task PersistAsync(bool required, CancellationToken token)
            {
                log.TemplateRestorationRequired = required;
                await log.SaveAsync(directory, token);
            }

            templateSession = await ProjectTemplateWriteSession.PrepareAsync(
                client,
                result.ProjectId,
                log.TemplateRestorationRequired,
                PersistAsync,
                cancellationToken: cancellationToken);
            await new StatusUpdateImporter(client)
            {
                AddAttributionNote = addStatusAttribution,
            }.ImportAsync(snapshot, result, directory, cancellationToken);
        }

        if (templateSession is not null)
        {
            await templateSession.CompleteAsync(snapshot.Project.Template, cancellationToken);
        }
        else
        {
            await ProjectTemplateWriteSession.SetFinalStateAsync(
                client,
                result.ProjectId,
                snapshot.Project.Template,
                cancellationToken: cancellationToken);
        }

        return result;
    }

    private static Task<ProjectSnapshot> ExportAsync(
        GitHubGraphQLClient client,
        int projectNumber,
        CancellationToken cancellationToken)
        => new ProjectExporter(client).ExportAsync(TargetOrg, projectNumber, cancellationToken);

    private static ProjectSnapshot Snapshot(string title, bool template, bool includeStatusUpdate) => new()
    {
        SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
        LinkedRepositories = [],
        LinkedTeams = [],
        Project = new ProjectInfoSnapshot
        {
            Title = title,
            Public = false,
            Closed = false,
            Template = template,
        },
        Fields = [],
        Views = [],
        Workflows = [],
        Items = [],
        StatusUpdates = includeStatusUpdate
            ?
            [
                new StatusUpdateSnapshot
                {
                    Body = "Template migration E2E",
                    Status = "ON_TRACK",
                    CreatedAt = "2026-01-01T00:00:00Z",
                    UpdatedAt = "2026-01-01T00:00:00Z",
                },
            ]
            : [],
    };

    private static string NewTitle(string kind)
        => $"ghpmv-template-{kind}-{Guid.NewGuid():N}";

    private static async Task DeleteProjectAsync(GitHubGraphQLClient client, string projectId)
    {
        await client.QueryAsync(
            "mutation($projectId: ID!) { deleteProjectV2(input: { projectId: $projectId }) { projectV2 { id } } }",
            new { projectId },
            CancellationToken.None);
    }

    private static void TryDeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
