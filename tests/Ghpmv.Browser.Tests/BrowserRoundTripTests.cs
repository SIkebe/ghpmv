using Ghpmv.Core.Browser;
using Ghpmv.Core.Export;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;
using Ghpmv.Core.Verify;
using Ghpmv.TestSupport;
using System.Text.Json;

namespace Ghpmv.Browser.Tests;

/// <summary>
/// M6 E2E: exports the configured fixture project including browser-scraped UI
/// settings, imports it into the target org (project, fields, and supported View state
/// via GraphQL; unsupported View state via browser automation), re-exports the target and asserts the views round-trip
/// (name / layout / UI settings). Requires GHPMV_BROWSER_STATE (a storage-state file
/// saved by <c>ghpmv login</c>) and GHPMV_TEST_TOKEN; skipped otherwise.
/// The created project is deleted in a finally block.
/// </summary>
[Trait("Category", "E2E")]
public class BrowserRoundTripTests
{
    private static string SourceOrg => E2eTestEnvironment.SourceOrganization;

    private static string TargetOrg => E2eTestEnvironment.TargetOrganization;

    private static int FixtureProjectNumber => E2eTestEnvironment.BrowserProjectNumber;

    private static string SourceFixtureRepository => E2eTestEnvironment.BrowserSourceRepository;

    private static string TargetFixtureRepository => E2eTestEnvironment.BrowserTargetRepository;

    private static string ExplicitCollaboratorLogin => E2eTestEnvironment.CollaboratorLogin;

    private static IReadOnlyDictionary<string, string> RepositoryMapping =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [$"{SourceOrg}/{SourceFixtureRepository}"] = $"{TargetOrg}/{TargetFixtureRepository}",
        };

    private static IReadOnlyDictionary<string, string> OrganizationMapping =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SourceOrg] = TargetOrg,
        };

    private static string CreateOperationLogDirectory()
        => Path.Combine(Path.GetTempPath(), $"ghpmv-browser-project-import-{Guid.NewGuid():N}");

    [Fact]
    public async Task Explicit_collaborators_are_exported_through_browser_automation()
    {
        var sourceStatePath = E2eTestEnvironment.SourceBrowserStatePath;
        var targetStatePath = E2eTestEnvironment.TargetBrowserStatePath;
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(sourceStatePath) || !File.Exists(sourceStatePath),
            "The configured source browser state file does not exist; skipping browser E2E test.");
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(targetStatePath) || !File.Exists(targetStatePath),
            "The configured target browser state file does not exist; skipping browser E2E test.");
        var sourceToken = E2eTestEnvironment.SourceToken;
        var targetToken = E2eTestEnvironment.TargetToken;
        Assert.SkipWhen(string.IsNullOrWhiteSpace(sourceToken), "The configured source token is not set; skipping browser E2E test.");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(targetToken), "The configured target token is not set; skipping browser E2E test.");

        var cancellationToken = TestContext.Current.CancellationToken;
        using var sourceClient = CreateClient(sourceToken!, E2eTestEnvironment.Current.Source.ApiBaseUrl);
        using var targetClient = CreateClient(targetToken!, E2eTestEnvironment.Current.Target.ApiBaseUrl);
        var (projectId, userId) = await ResolveProjectAndUserIdsAsync(sourceClient, SourceOrg, ExplicitCollaboratorLogin, cancellationToken);
        var userMapping = E2eTestEnvironment.Current.Users.ToMappingDictionary();
        var targetCollaboratorLogin = userMapping.TryGetValue(ExplicitCollaboratorLogin, out var mappedLogin)
            ? mappedLogin
            : ExplicitCollaboratorLogin;
        var targetUserId = await ResolveUserIdAsync(targetClient, targetCollaboratorLogin, cancellationToken);

        await SetCollaboratorAsync(sourceClient, projectId, userId, "WRITER", cancellationToken);
        try
        {
            await using var sourceSession = CreateSession(sourceStatePath!, E2eTestEnvironment.Current.Source);
            await using var targetSession = CreateSession(targetStatePath!, E2eTestEnvironment.Current.Target);
            var exporter = new ProjectExporter(sourceClient);
            var snapshot = await exporter.ExportAsync(SourceOrg, FixtureProjectNumber, cancellationToken);
            var collaboratorExporter = new CollaboratorUiExporter(sourceSession);

            snapshot = await collaboratorExporter.EnrichAsync(snapshot, SourceOrg, ProjectOwnerType.Organization, FixtureProjectNumber, cancellationToken);

            Assert.Empty(collaboratorExporter.Warnings);
            var collaborator = Assert.Single(snapshot.Collaborators!, c =>
                string.Equals(c.Login, ExplicitCollaboratorLogin, StringComparison.OrdinalIgnoreCase));
            Assert.Equal("USER", collaborator.Type);
            Assert.Equal("WRITER", collaborator.Role);

            var verificationSnapshot = snapshot with
            {
                Project = snapshot.Project with { Title = "ghpmv-browser-collaborator-test-" + Guid.NewGuid().ToString("N") },
                Views = [],
                Workflows = [],
                Items = [],
                LinkedRepositories = [],
                Collaborators = [collaborator],
            };
            var result = await new ProjectImporter(targetClient)
            {
                OperationLogDirectory = CreateOperationLogDirectory(),
                OrganizationMapping = OrganizationMapping,
                RepositoryMapping = RepositoryMapping,
                UserMapping = userMapping,
            }.ImportAsync(
                verificationSnapshot,
                TargetOrg,
                cancellationToken);
            try
            {
                var targetViewExporter = new ViewUiExporter(targetSession);
                var targetWorkflowExporter = new WorkflowUiExporter(targetSession);
                var targetCollaboratorExporter = new CollaboratorUiExporter(targetSession);
                var verifier = new ProjectVerifier(targetClient)
                {
                    OrganizationMapping = OrganizationMapping,
                    RepositoryMapping = RepositoryMapping,
                    UserMapping = userMapping,
                    PostExportAsync = async (target, ct) =>
                    {
                        target = await targetViewExporter.EnrichAsync(target, TargetOrg, result.ProjectNumber, ct);
                        target = await targetWorkflowExporter.EnrichAsync(target, TargetOrg, result.ProjectNumber, ct);
                        return await targetCollaboratorExporter.EnrichAsync(
                            target,
                            TargetOrg,
                            ProjectOwnerType.Organization,
                            result.ProjectNumber,
                            ct);
                    },
                };

                var matchReport = await verifier.VerifyAsync(
                    verificationSnapshot,
                    TargetOrg,
                    result.ProjectNumber,
                    cancellationToken);
                Assert.DoesNotContain(matchReport.Differences, difference => difference.Category == "Collaborator");

                await SetCollaboratorAsync(targetClient, result.ProjectId, targetUserId, "READER", cancellationToken);
                var driftReport = await verifier.VerifyAsync(
                    verificationSnapshot,
                    TargetOrg,
                    result.ProjectNumber,
                    cancellationToken);
                Assert.Contains(driftReport.Differences, difference =>
                    difference.Severity == VerifySeverity.Error
                    && difference.Category == "Collaborator"
                    && difference.Message.Contains("role mismatch", StringComparison.Ordinal));
            }
            finally
            {
                await DeleteProjectAsync(targetClient, result.ProjectId);
            }
        }
        finally
        {
            await SetCollaboratorAsync(sourceClient, projectId, userId, "NONE", CancellationToken.None);
        }
    }

    [Fact]
    public async Task Views_round_trip_through_browser_automation()
    {
        var sourceStatePath = E2eTestEnvironment.SourceBrowserStatePath;
        var targetStatePath = E2eTestEnvironment.TargetBrowserStatePath;
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(sourceStatePath) || !File.Exists(sourceStatePath),
            "The configured source browser state file does not exist; skipping browser E2E test.");
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(targetStatePath) || !File.Exists(targetStatePath),
            "The configured target browser state file does not exist; skipping browser E2E test.");
        var sourceToken = E2eTestEnvironment.SourceToken;
        var targetToken = E2eTestEnvironment.TargetToken;
        Assert.SkipWhen(string.IsNullOrWhiteSpace(sourceToken), "The configured source token is not set; skipping browser E2E test.");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(targetToken), "The configured target token is not set; skipping browser E2E test.");

        var cancellationToken = TestContext.Current.CancellationToken;
        using var sourceClient = CreateClient(sourceToken!, E2eTestEnvironment.Current.Source.ApiBaseUrl);
        using var targetClient = CreateClient(targetToken!, E2eTestEnvironment.Current.Target.ApiBaseUrl);
        await using var sourceSession = CreateSession(sourceStatePath!, E2eTestEnvironment.Current.Source);
        await using var targetSession = CreateSession(targetStatePath!, E2eTestEnvironment.Current.Target);

        // Export the fixture with UI settings and retarget it under a unique title.
        var exporter = new ProjectExporter(sourceClient);
        var uiExporter = new ViewUiExporter(sourceSession);
        var source = await exporter.ExportAsync(SourceOrg, FixtureProjectNumber, cancellationToken);
        source = await uiExporter.EnrichAsync(source, SourceOrg, FixtureProjectNumber, cancellationToken);
        Assert.Empty(uiExporter.Warnings);
        Assert.All(source.Views, v => Assert.NotNull(v.Ui));
        Assert.Contains(source.Fields, field => field.Name == "Labels" && field.DataType == "LABELS");
        Assert.Contains(source.Fields, field => field.Name == "Fixture Teams" && field.IssueField is not null);

        // Explicit source expectations (fixture enrichment, 2026-07-06) — guards against
        // silently comparing null-to-null when the scrape misses a setting.
        var sourceTable = Assert.Single(source.Views, v => v.Name == "View 1");
        Assert.Equal("status:Todo", sourceTable.Filter);
        Assert.Equal("Fixture Number", Assert.Single(sourceTable.SortByFields).Field);
        Assert.Equal("Fixture Select", sourceTable.Ui!.SliceBy);

        var sourceBoard = Assert.Single(source.Views, v => v.Name == "Fixture Board");
        Assert.Equal("Fixture Select", Assert.Single(sourceBoard.VerticalGroupByFields));
        Assert.Equal(["Fixture Number"], sourceBoard.Ui!.FieldSum);

        var sourceRoadmap = Assert.Single(source.Views, v => v.Name == "Fixture Roadmap");
        Assert.Equal("Quarter", sourceRoadmap.Ui!.Roadmap?.Zoom);
        Assert.Contains("Fixture Date", sourceRoadmap.Ui.Roadmap?.Markers ?? []);

        var title = "ghpmv-browser-test-" + Guid.NewGuid().ToString("N");
        var snapshot = source with { Project = source.Project with { Title = title } };
        var userMapping = E2eTestEnvironment.Current.Users.ToMappingDictionary();

        var importer = new ProjectImporter(targetClient)
        {
            OperationLogDirectory = CreateOperationLogDirectory(),
            BrowserViewEnrichmentPlanned = true,
            OrganizationMapping = OrganizationMapping,
            RepositoryMapping = RepositoryMapping,
            UserMapping = userMapping,
        };
        var result = await importer.ImportAsync(snapshot, TargetOrg, cancellationToken);
        try
        {
            var viewImporter = new ViewUiImporter(targetSession);
            await viewImporter.EnrichAsync(
                snapshot,
                TargetOrg,
                ProjectOwnerType.Organization,
                result.ProjectNumber,
                result.ViewNumbers,
                cancellationToken);
            Assert.Empty(viewImporter.Warnings);

            // Verify re-exports the target through GraphQL and its browser post-export hook.
            ProjectSnapshot? reExported = null;
            var reExportUi = new ViewUiExporter(targetSession);
            var verifier = new ProjectVerifier(targetClient)
            {
                OrganizationMapping = OrganizationMapping,
                RepositoryMapping = RepositoryMapping,
                UserMapping = userMapping,
                PostExportAsync = async (target, ct) =>
                {
                    reExported = await reExportUi.EnrichAsync(target, TargetOrg, result.ProjectNumber, ct);
                    return reExported;
                },
            };
            var report = await verifier.VerifyAsync(snapshot, TargetOrg, result.ProjectNumber, cancellationToken);
            Assert.Empty(reExportUi.Warnings);
            var target = Assert.IsType<ProjectSnapshot>(reExported);
            Assert.DoesNotContain(report.Differences, difference => difference.Category == "Field");
            Assert.DoesNotContain(report.Differences, difference => difference.Category == "View");

            Assert.Equal(snapshot.Views.Count, target.Views.Count);
            // Tab order re-creation is out of scope for v1 (PLAN §8.1) and target view
            // numbers are re-assigned, so views are matched by name instead of position.
            foreach (var expected in snapshot.Views)
            {
                var actual = Assert.Single(target.Views, v => string.Equals(v.Name, expected.Name, StringComparison.Ordinal));
                Assert.Equal(expected.Layout, actual.Layout);

                Assert.NotNull(expected.Ui);
                Assert.NotNull(actual.Ui);
                Assert.Equal(expected.Ui!.SliceBy, actual.Ui!.SliceBy);
                Assert.Equal(expected.Ui.FieldSum ?? [], actual.Ui.FieldSum ?? []);
                Assert.Equal(expected.Ui.Roadmap is null, actual.Ui.Roadmap is null);
                if (expected.Ui.Roadmap is { } roadmap)
                {
                    Assert.Equal(roadmap.StartField, actual.Ui.Roadmap!.StartField);
                    Assert.Equal(roadmap.TargetField, actual.Ui.Roadmap.TargetField);
                    Assert.Equal(roadmap.Zoom, actual.Ui.Roadmap.Zoom);
                    Assert.Equal(roadmap.Markers ?? [], actual.Ui.Roadmap.Markers ?? []);
                }
            }

            var driftedSnapshot = snapshot with
            {
                Views = snapshot.Views.Select(view =>
                    view.Name == "View 1"
                        ? view with { Ui = view.Ui! with { SliceBy = "Status" } }
                        : view).ToList(),
            };
            await viewImporter.EnrichAsync(
                driftedSnapshot,
                TargetOrg,
                ProjectOwnerType.Organization,
                result.ProjectNumber,
                result.ViewNumbers,
                cancellationToken);
            var driftReport = await verifier.VerifyAsync(snapshot, TargetOrg, result.ProjectNumber, cancellationToken);
            Assert.Contains(driftReport.Differences, difference =>
                difference.Severity == VerifySeverity.Error
                && difference.Category == "View"
                && difference.Message.Contains("slice by mismatch", StringComparison.Ordinal));
        }
        finally
        {
            await DeleteProjectAsync(targetClient, result.ProjectId);
        }
    }

    /// <summary>
    /// M7 E2E: exports the fixture workflows (GraphQL + UI scrape), imports them into a
    /// fresh target project (workflows via browser automation, Auto-add repository
    /// resolved through the repo mapping), re-exports the target and asserts the
    /// enabled state / content types / status values / filter / repository round-trip.
    /// Kept independent of the views E2E so each run stays focused (and faster).
    /// </summary>
    [Fact]
    public async Task Workflows_round_trip_through_browser_automation()
    {
        var sourceStatePath = E2eTestEnvironment.SourceBrowserStatePath;
        var targetStatePath = E2eTestEnvironment.TargetBrowserStatePath;
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(sourceStatePath) || !File.Exists(sourceStatePath),
            "The configured source browser state file does not exist; skipping browser E2E test.");
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(targetStatePath) || !File.Exists(targetStatePath),
            "The configured target browser state file does not exist; skipping browser E2E test.");
        var sourceToken = E2eTestEnvironment.SourceToken;
        var targetToken = E2eTestEnvironment.TargetToken;
        Assert.SkipWhen(string.IsNullOrWhiteSpace(sourceToken), "The configured source token is not set; skipping browser E2E test.");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(targetToken), "The configured target token is not set; skipping browser E2E test.");

        var cancellationToken = TestContext.Current.CancellationToken;
        using var sourceClient = CreateClient(sourceToken!, E2eTestEnvironment.Current.Source.ApiBaseUrl);
        using var targetClient = CreateClient(targetToken!, E2eTestEnvironment.Current.Target.ApiBaseUrl);
        await using var sourceSession = CreateSession(sourceStatePath!, E2eTestEnvironment.Current.Source);
        await using var targetSession = CreateSession(targetStatePath!, E2eTestEnvironment.Current.Target);

        // Export the fixture with workflow UI settings and retarget it under a unique title.
        var exporter = new ProjectExporter(sourceClient);
        var workflowExporter = new WorkflowUiExporter(sourceSession);
        var source = await exporter.ExportAsync(SourceOrg, FixtureProjectNumber, cancellationToken);
        source = await workflowExporter.EnrichAsync(source, SourceOrg, FixtureProjectNumber, cancellationToken);
        Assert.Empty(workflowExporter.Warnings);
        Assert.All(source.Workflows, w => Assert.NotNull(w.Ui));

        // Explicit source expectations (fixture enrichment, 2026-07-06): two Auto-add
        // instances (exercising the Duplicate path) and a saved-but-disabled workflow
        // (exercising the disable mirroring incl. the save-once path on the target).
        Assert.Equal(2, source.Workflows.Count(w => w.Ui!.Repository is not null));
        var sourceSecondary = Assert.Single(source.Workflows, w => w.Name == "Auto-add secondary");
        Assert.True(sourceSecondary.Enabled);
        Assert.Equal(SourceFixtureRepository, sourceSecondary.Ui!.Repository);
        Assert.Equal("is:issue label:bug", sourceSecondary.Ui.Filter);
        var sourceDisabled = Assert.Single(source.Workflows, w => w.Name == "Code changes requested");
        Assert.False(sourceDisabled.Enabled);
        Assert.Equal("Code changes requested", sourceDisabled.Name);
        Assert.Equal("In Progress", sourceDisabled.Ui!.StatusValue);

        var title = "ghpmv-browser-wf-test-" + Guid.NewGuid().ToString("N");
        var snapshot = source with { Project = source.Project with { Title = title } };
        var userMapping = E2eTestEnvironment.Current.Users.ToMappingDictionary();

        var importer = new ProjectImporter(targetClient)
        {
            OperationLogDirectory = CreateOperationLogDirectory(),
            OrganizationMapping = OrganizationMapping,
            RepositoryMapping = RepositoryMapping,
            UserMapping = userMapping,
        };
        var result = await importer.ImportAsync(snapshot, TargetOrg, cancellationToken);
        try
        {
            var workflowImporter = new WorkflowUiImporter(targetSession)
            {
                OrganizationMapping = OrganizationMapping,
                RepositoryMapping = RepositoryMapping,
                UserMapping = userMapping,
            };
            await workflowImporter.ImportAsync(snapshot, TargetOrg, result.ProjectNumber, cancellationToken);
            Assert.True(
                workflowImporter.Warnings.Count == 0,
                string.Join(Environment.NewLine, workflowImporter.Warnings));
            Assert.Equal(snapshot.Workflows.Count, workflowImporter.ImportedCount);

            var reExportUi = new WorkflowUiExporter(targetSession);
            ProjectSnapshot? reExported = null;
            var verifier = new ProjectVerifier(targetClient)
            {
                OrganizationMapping = OrganizationMapping,
                RepositoryMapping = RepositoryMapping,
                UserMapping = userMapping,
                PostExportAsync = async (target, ct) =>
                {
                    reExported = await reExportUi.EnrichAsync(target, TargetOrg, result.ProjectNumber, ct);
                    return reExported;
                },
            };
            var matchReport = await verifier.VerifyAsync(snapshot, TargetOrg, result.ProjectNumber, cancellationToken);
            Assert.Empty(reExportUi.Warnings);
            Assert.DoesNotContain(matchReport.Differences, difference => difference.Category == "Workflow");
            var target = Assert.IsType<ProjectSnapshot>(reExported);

            Assert.Equal(
                snapshot.Workflows.Select(w => w.Name).Order(StringComparer.Ordinal),
                target.Workflows.Select(w => w.Name).Order(StringComparer.Ordinal));
            foreach (var expected in snapshot.Workflows)
            {
                var actual = Assert.Single(target.Workflows, w => string.Equals(w.Name, expected.Name, StringComparison.Ordinal));
                Assert.Equal(expected.Enabled, actual.Enabled);

                Assert.NotNull(expected.Ui);
                Assert.NotNull(actual.Ui);
                Assert.Equal(expected.Ui!.ContentTypes ?? [], actual.Ui!.ContentTypes ?? []);
                Assert.Equal(expected.Ui.StatusValue, actual.Ui.StatusValue);
                Assert.Equal(expected.Ui.Filter, actual.Ui.Filter);
                Assert.Equal(
                    expected.Ui.Repository == SourceFixtureRepository
                        ? TargetFixtureRepository
                        : expected.Ui.Repository,
                    actual.Ui.Repository);
            }

            var targetWorkflow = Assert.Single(target.Workflows, workflow => workflow.Name == "Auto-add secondary");
            await workflowImporter.UpdateExistingFilterAsync(
                TargetOrg,
                ProjectOwnerType.Organization,
                result.ProjectNumber,
                targetWorkflow,
                "is:issue label:documentation",
                cancellationToken);
            var driftReport = await verifier.VerifyAsync(snapshot, TargetOrg, result.ProjectNumber, cancellationToken);
            Assert.Contains(driftReport.Differences, difference =>
                difference.Severity == VerifySeverity.Error
                && difference.Category == "Workflow"
                && difference.Message.Contains("filter mismatch", StringComparison.Ordinal));
        }
        finally
        {
            await DeleteProjectAsync(targetClient, result.ProjectId);
        }
    }

    private static async Task DeleteProjectAsync(GitHubGraphQLClient client, string projectId)
    {
        _ = await client.QueryAsync(
            "mutation($projectId: ID!) { deleteProjectV2(input: { projectId: $projectId }) { projectV2 { id } } }",
            new { projectId },
            CancellationToken.None);
    }

    private static GitHubGraphQLClient CreateClient(string token, string apiBaseUrl)
        => new(token, GitHubGraphQLClient.NormalizeBaseUrl(apiBaseUrl));

    private static BrowserSession CreateSession(string statePath, E2eEndpointSettings endpoint)
        => new(new BrowserSessionOptions
        {
            BaseUrl = BrowserBaseUrl.Resolve(
                GitHubGraphQLClient.NormalizeBaseUrl(endpoint.ApiBaseUrl),
                endpoint.WebBaseUrl),
            Profile = endpoint.BrowserProfile,
            StatePath = statePath,
        });

    private static async Task<string> ResolveUserIdAsync(
        GitHubGraphQLClient client,
        string login,
        CancellationToken cancellationToken)
    {
        var data = await client.QueryAsync(
            "query($login: String!) { user(login: $login) { id } }",
            new { login },
            cancellationToken);
        return data.GetProperty("user").GetProperty("id").GetString()!;
    }

        private static async Task<(string ProjectId, string UserId)> ResolveProjectAndUserIdsAsync(
                GitHubGraphQLClient client,
                string org,
                string login,
                CancellationToken cancellationToken)
        {
                var data = await client.QueryAsync(
                        """
                        query($org: String!, $number: Int!, $login: String!) {
                            organization(login: $org) { projectV2(number: $number) { id } }
                            user(login: $login) { id }
                        }
                        """,
                        new { org, number = FixtureProjectNumber, login },
                        cancellationToken);
                return (
                        data.GetProperty("organization").GetProperty("projectV2").GetProperty("id").GetString()!,
                        data.GetProperty("user").GetProperty("id").GetString()!);
        }

        private static Task<JsonElement> SetCollaboratorAsync(
                GitHubGraphQLClient client,
                string projectId,
                string userId,
                string role,
                CancellationToken cancellationToken)
                => client.QueryAsync(
                        """
                        mutation($projectId: ID!, $userId: ID!, $role: ProjectV2Roles!) {
                            updateProjectV2Collaborators(input: { projectId: $projectId, collaborators: [{ userId: $userId, role: $role }] }) {
                                clientMutationId
                            }
                        }
                        """,
                        new { projectId, userId, role },
                        cancellationToken);
}
