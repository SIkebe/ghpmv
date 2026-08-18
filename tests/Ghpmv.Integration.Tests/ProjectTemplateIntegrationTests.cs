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
                cancellationToken);

            var report = await new ProjectVerifier(client).VerifyAsync(
                targetSnapshot,
                TargetOrg,
                target.ProjectNumber,
                cancellationToken);
            Assert.True(report.IsMatch, string.Join(Environment.NewLine, report.Differences.Select(d => d.Message)));

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
                    Directory.Delete(sourceDirectory, recursive: true);
                    Directory.Delete(targetDirectory, recursive: true);
                }
             }
        }
    }

    [Fact]
    public async Task Existing_template_can_be_unmarked_and_legacy_null_preserves_state()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = IntegrationTestSettings.CreateClient(Token);
        var initialDirectory = IntegrationTestSettings.CreateOperationLogDirectory();
        var ordinaryDirectory = IntegrationTestSettings.CreateOperationLogDirectory();
        var legacyDirectory = IntegrationTestSettings.CreateOperationLogDirectory();
        ImportResult? target = null;

        try
        {
            var initial = Snapshot(NewTitle("existing"), template: true, includeStatusUpdate: false);
            target = await ImportCompleteSnapshotAsync(
                client,
                TargetOrg,
                initial,
                initialDirectory,
                addStatusAttribution: true,
                cancellationToken);

            var ordinary = initial with { Project = initial.Project with { Template = false } };
            await new ProjectImporter(client)
            {
                OnConflict = ConflictAction.Update,
                OperationLogDirectory = ordinaryDirectory,
            }.ImportIntoAsync(ordinary, TargetOrg, target.ProjectNumber, cancellationToken);
            await ProjectTemplateWriteSession.SetFinalStateAsync(
                client,
                target.ProjectId,
                desiredTemplate: false,
                cancellationToken: cancellationToken);
            Assert.False((await ExportAsync(client, target.ProjectNumber, cancellationToken)).Project.Template);

            await ProjectTemplateWriteSession.SetFinalStateAsync(
                client,
                target.ProjectId,
                desiredTemplate: true,
                cancellationToken: cancellationToken);
            var legacy = ordinary with { Project = ordinary.Project with { Template = null } };
            await new ProjectImporter(client)
            {
                OnConflict = ConflictAction.Update,
                OperationLogDirectory = legacyDirectory,
            }.ImportIntoAsync(legacy, TargetOrg, target.ProjectNumber, cancellationToken);

            Assert.True((await ExportAsync(client, target.ProjectNumber, cancellationToken)).Project.Template);
        }
        finally
        {
            if (target is not null)
            {
                await DeleteProjectAsync(client, target.ProjectId);
            }

            Directory.Delete(initialDirectory, recursive: true);
            Directory.Delete(ordinaryDirectory, recursive: true);
            Directory.Delete(legacyDirectory, recursive: true);
        }
    }

    private static async Task<ImportResult> ImportCompleteSnapshotAsync(
        GitHubGraphQLClient client,
        string organization,
        ProjectSnapshot snapshot,
        string directory,
        bool addStatusAttribution,
        CancellationToken cancellationToken)
    {
        var result = await new ProjectImporter(client)
        {
            OperationLogDirectory = directory,
        }.ImportAsync(snapshot, organization, cancellationToken);
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
                snapshot.Project.Template!.Value,
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
}
