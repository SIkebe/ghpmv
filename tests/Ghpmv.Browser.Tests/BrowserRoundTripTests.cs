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
/// Shared browser E2E: exports the configured fixture once, imports it into one target
/// Project, applies View and Workflow UI state, and verifies Views, Workflows, and explicit
/// collaborators together. The same target is drifted once and re-verified so the suite
/// launches only one source and one target browser session. Requires configured browser
/// state and API tokens; skipped otherwise. The target Project is deleted in a finally block.
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
    public async Task Browser_features_round_trip_in_one_shared_scenario()
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
        var (sourceProjectId, sourceUserId) = await ResolveProjectAndUserIdsAsync(
            sourceClient,
            SourceOrg,
            ExplicitCollaboratorLogin,
            cancellationToken);
        var userMapping = E2eTestEnvironment.Current.Users.ToMappingDictionary();
        var targetCollaboratorLogin = userMapping.TryGetValue(ExplicitCollaboratorLogin, out var mappedLogin)
            ? mappedLogin
            : ExplicitCollaboratorLogin;
        var targetUserId = await ResolveUserIdAsync(targetClient, targetCollaboratorLogin, cancellationToken);

        await using var sourceSession = CreateSession(sourceStatePath!, E2eTestEnvironment.Current.Source);
        await using var targetSession = CreateSession(targetStatePath!, E2eTestEnvironment.Current.Target);
        var validateTargetAuthentication = await CreateTargetAuthenticationGuardAsync(
            targetClient,
            targetSession,
            cancellationToken);

        await SetCollaboratorAsync(sourceClient, sourceProjectId, sourceUserId, "WRITER", cancellationToken);
        try
        {
            var sourceViewExporter = new ViewUiExporter(sourceSession);
            var sourceWorkflowExporter = new WorkflowUiExporter(sourceSession);
            var sourceCollaboratorExporter = new CollaboratorUiExporter(sourceSession);
            var sourceFieldDefaultExporter = new FieldDefaultUiExporter(sourceSession);
            var source = await new ProjectExporter(sourceClient)
                .ExportAsync(SourceOrg, FixtureProjectNumber, cancellationToken);
            source = await sourceFieldDefaultExporter.EnrichAsync(
                source,
                SourceOrg,
                ProjectOwnerType.Organization,
                FixtureProjectNumber,
                cancellationToken);
            source = await sourceViewExporter.EnrichAsync(source, SourceOrg, FixtureProjectNumber, cancellationToken);
            source = await sourceWorkflowExporter.EnrichAsync(source, SourceOrg, FixtureProjectNumber, cancellationToken);
            source = await sourceCollaboratorExporter.EnrichAsync(
                source,
                SourceOrg,
                ProjectOwnerType.Organization,
                FixtureProjectNumber,
                cancellationToken);

            Assert.Empty(sourceViewExporter.Warnings);
            Assert.Empty(sourceWorkflowExporter.Warnings);
            Assert.Empty(sourceCollaboratorExporter.Warnings);
            Assert.Empty(sourceFieldDefaultExporter.Warnings);
            Assert.False(
                sourceViewExporter.GraphQlPositionMatchesDomOrder,
                "GraphQL POSITION now matches the saved-tab DOM order. Re-evaluate replacing the browser read path with the public API.");
            AssertSourceViews(source);
            AssertSourceWorkflows(source);
            AssertSourceFieldDefaults(source);
            var collaborator = Assert.Single(source.Collaborators!, candidate =>
                string.Equals(candidate.Login, ExplicitCollaboratorLogin, StringComparison.OrdinalIgnoreCase));
            Assert.Equal("USER", collaborator.Type);
            Assert.Equal("WRITER", collaborator.Role);

            var snapshot = BuildRoundTripSnapshot(source, collaborator);
            var apiPositions = snapshot.Views
                .OrderBy(view => view.Number)
                .Select((view, position) => (view.Number, position))
                .ToDictionary(pair => pair.Number, pair => pair.position);
            var apiImportSnapshot = snapshot with
            {
                Views = snapshot.Views.Select(view => view with
                {
                    TabPosition = apiPositions[view.Number],
                }).ToList(),
            };

            var operationLogDirectory = CreateOperationLogDirectory();
            var importer = new ProjectImporter(targetClient)
            {
                OperationLogDirectory = operationLogDirectory,
                BeforeWriteAsync = validateTargetAuthentication,
                BrowserViewEnrichmentPlanned = true,
                BrowserFieldDefaultEnrichmentPlanned = true,
                OrganizationMapping = OrganizationMapping,
                RepositoryMapping = RepositoryMapping,
                UserMapping = userMapping,
            };
            var result = await importer.ImportAsync(apiImportSnapshot, TargetOrg, cancellationToken);
            try
            {
                var initialItemResult = await new ItemImporter(targetClient)
                {
                    RepositoryMapping = RepositoryMapping,
                    UserMapping = userMapping,
                }.ImportAsync(snapshot, result, operationLogDirectory, cancellationToken);
                Assert.Empty(initialItemResult.Warnings);

                var fieldDefaultImporter = new FieldDefaultUiImporter(targetSession);
                await fieldDefaultImporter.ImportAsync(
                    snapshot,
                    TargetOrg,
                    ProjectOwnerType.Organization,
                    result.ProjectNumber,
                    cancellationToken);
                Assert.Empty(fieldDefaultImporter.Warnings);

                var initialViewReport = await new ProjectVerifier(targetClient)
                {
                    OrganizationMapping = OrganizationMapping,
                    RepositoryMapping = RepositoryMapping,
                    UserMapping = userMapping,
                    IncludedCategories = new HashSet<string>(StringComparer.Ordinal) { VerifyCategories.View },
                }.VerifyAsync(snapshot, TargetOrg, result.ProjectNumber, cancellationToken);
                Assert.Contains(initialViewReport.Categories, category =>
                    category.Category == VerifyCategories.View && category.Status == VerifyStatus.NotVerified);

                var targetPage = await targetSession.GetPageAsync(cancellationToken);
                await targetPage.SetViewportSizeAsync(480, 1000);
                var expectedRoadmapDisplay = Assert.Single(
                    snapshot.Views
                        .Where(view => view.Layout == "ROADMAP_LAYOUT")
                        .Select(view => (
                            view.Ui!.Roadmap!.TruncateTitles,
                            view.Ui.Roadmap.ShowDateFields))
                        .Distinct());
                await targetPage.EvaluateAsync(
                    """
                    values => {
                      localStorage.setItem("projects.roadmapTruncateTitles", values.truncateTitles);
                      localStorage.setItem("projects.roadmapShowDateFields", values.showDateFields);
                    }
                    """,
                    new
                    {
                        truncateTitles = (!expectedRoadmapDisplay.TruncateTitles!.Value).ToString().ToLowerInvariant(),
                        showDateFields = (!expectedRoadmapDisplay.ShowDateFields!.Value).ToString().ToLowerInvariant(),
                    });
                var viewImporter = new ViewUiImporter(targetSession);
                await viewImporter.EnrichAsync(
                    snapshot,
                    TargetOrg,
                    ProjectOwnerType.Organization,
                    result.ProjectNumber,
                    result.ViewNumbers,
                    cancellationToken);
                Assert.Empty(viewImporter.Warnings);

                await AssertRoadmapDisplayProfileStateAsync(
                    targetStatePath!,
                    expectedRoadmapDisplay.TruncateTitles.Value,
                    expectedRoadmapDisplay.ShowDateFields.Value,
                    cancellationToken);

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

                ProjectSnapshot? reExported = null;
                var targetViewExporter = new ViewUiExporter(targetSession);
                var targetWorkflowExporter = new WorkflowUiExporter(targetSession);
                var targetCollaboratorExporter = new CollaboratorUiExporter(targetSession);
                var targetFieldDefaultExporter = new FieldDefaultUiExporter(targetSession);
                var browserVerificationCount = 0;
                var verifier = new ProjectVerifier(targetClient)
                {
                    OrganizationMapping = OrganizationMapping,
                    RepositoryMapping = RepositoryMapping,
                    UserMapping = userMapping,
                    PostExportAsync = async (target, ct) =>
                    {
                        browserVerificationCount++;
                        target = await targetFieldDefaultExporter.EnrichAsync(
                            target,
                            TargetOrg,
                            ProjectOwnerType.Organization,
                            result.ProjectNumber,
                            ct);
                        target = await targetViewExporter.EnrichAsync(target, TargetOrg, result.ProjectNumber, ct);
                        target = await targetWorkflowExporter.EnrichAsync(target, TargetOrg, result.ProjectNumber, ct);
                        reExported = await targetCollaboratorExporter.EnrichAsync(
                            target,
                            TargetOrg,
                            ProjectOwnerType.Organization,
                            result.ProjectNumber,
                            ct);
                        return reExported;
                    },
                };

                var matchReport = await verifier.VerifyAsync(snapshot, TargetOrg, result.ProjectNumber, cancellationToken);
                Assert.Empty(targetViewExporter.Warnings);
                Assert.Empty(targetWorkflowExporter.Warnings);
                Assert.Empty(targetCollaboratorExporter.Warnings);
                Assert.Empty(targetFieldDefaultExporter.Warnings);
                Assert.Equal(
                    VerifyCategories.All,
                    matchReport.Categories.Select(category => category.Category));
                Assert.All(
                    matchReport.Categories,
                    category => Assert.Equal(VerifyStatus.Match, category.Status));
                var target = Assert.IsType<ProjectSnapshot>(reExported);
                AssertRoundTrippedViews(snapshot, target);
                AssertRoundTrippedWorkflows(snapshot, target);
                AssertRoundTrippedFieldDefaults(snapshot, target);
                await new FieldDefaultFixtureObserver(targetClient, targetSession).ValidateStandardFixtureAsync(
                    TargetOrg,
                    result.ProjectNumber,
                    cancellationToken);
                await new FieldSumRenderingObserver(targetSession).ValidateStandardFixtureAsync(
                    TargetOrg,
                    ProjectOwnerType.Organization,
                    result.ProjectNumber,
                    snapshot.Views.ToDictionary(
                        view => view.Name,
                        view => result.ViewNumbers[view.Number],
                        StringComparer.Ordinal),
                    cancellationToken);

                var sourceRoadmap = Assert.Single(snapshot.Views, view => view.Name == "Fixture Roadmap");
                await viewImporter.ApplyRoadmapDisplayOptionsAsync(
                    TargetOrg,
                    ProjectOwnerType.Organization,
                    result.ProjectNumber,
                    result.ViewNumbers[sourceRoadmap.Number],
                    sourceRoadmap.Name,
                    truncateTitles: false,
                    showDateFields: false,
                    cancellationToken);
                Assert.Empty(viewImporter.Warnings);

                var titleDriftReport = await verifier.VerifyAsync(
                    snapshot,
                    TargetOrg,
                    result.ProjectNumber,
                    cancellationToken);
                Assert.Equal(2, titleDriftReport.Differences.Count(difference =>
                    difference.Category == VerifyCategories.View
                    && difference.Message.Contains("truncate titles mismatch", StringComparison.Ordinal)));
                Assert.Equal(2, titleDriftReport.Differences.Count(difference =>
                    difference.Severity != VerifySeverity.Info));
                Assert.Equal(
                    VerifyStatus.Mismatch,
                    Assert.Single(titleDriftReport.Categories, category =>
                        category.Category == VerifyCategories.View).Status);
                Assert.DoesNotContain(titleDriftReport.Differences, difference =>
                    difference.Category == VerifyCategories.View
                    && difference.Message.Contains("show date fields mismatch", StringComparison.Ordinal));
                await new FieldSumRenderingObserver(targetSession).ValidateFixtureAsync(
                    FixtureUiSnapshotFactory.CreateRoadmapDisplayDrift(),
                    TargetOrg,
                    ProjectOwnerType.Organization,
                    result.ProjectNumber,
                    snapshot.Views.ToDictionary(
                        view => view.Name,
                        view => result.ViewNumbers[view.Number],
                        StringComparer.Ordinal),
                    cancellationToken);

                await viewImporter.ApplyRoadmapDisplayOptionsAsync(
                    TargetOrg,
                    ProjectOwnerType.Organization,
                    result.ProjectNumber,
                    result.ViewNumbers[sourceRoadmap.Number],
                    sourceRoadmap.Name,
                    truncateTitles: true,
                    showDateFields: true,
                    cancellationToken);
                Assert.Empty(viewImporter.Warnings);
                var dateDriftReport = await verifier.VerifyAsync(
                    snapshot,
                    TargetOrg,
                    result.ProjectNumber,
                    cancellationToken);
                Assert.DoesNotContain(dateDriftReport.Differences, difference =>
                    difference.Category == VerifyCategories.View
                    && difference.Message.Contains("truncate titles mismatch", StringComparison.Ordinal));
                Assert.Equal(2, dateDriftReport.Differences.Count(difference =>
                    difference.Category == VerifyCategories.View
                    && difference.Message.Contains("show date fields mismatch", StringComparison.Ordinal)));
                Assert.Equal(2, dateDriftReport.Differences.Count(difference =>
                    difference.Severity != VerifySeverity.Info));
                Assert.Equal(
                    VerifyStatus.Mismatch,
                    Assert.Single(dateDriftReport.Categories, category =>
                        category.Category == VerifyCategories.View).Status);
                await new FieldSumRenderingObserver(targetSession).ValidateFixtureAsync(
                    FixtureUiSnapshotFactory.CreateRoadmapDateDisplayDrift(),
                    TargetOrg,
                    ProjectOwnerType.Organization,
                    result.ProjectNumber,
                    snapshot.Views.ToDictionary(
                        view => view.Name,
                        view => result.ViewNumbers[view.Number],
                        StringComparer.Ordinal),
                    cancellationToken);

                var sourceTable = Assert.Single(snapshot.Views, view => view.Name == "View 1");
                await viewImporter.ApplyFieldSumAsync(
                    TargetOrg,
                    ProjectOwnerType.Organization,
                    result.ProjectNumber,
                    result.ViewNumbers[sourceTable.Number],
                    sourceTable.Name,
                    ["Fixture Number"],
                    cancellationToken);
                Assert.Empty(viewImporter.Warnings);

                var targetWorkflow = Assert.Single(target.Workflows, workflow => workflow.Name == "Auto-add secondary");
                await workflowImporter.UpdateExistingFilterAsync(
                    TargetOrg,
                    ProjectOwnerType.Organization,
                    result.ProjectNumber,
                    targetWorkflow,
                    "is:issue label:documentation",
                    cancellationToken);
                await SetCollaboratorAsync(targetClient, result.ProjectId, targetUserId, "READER", cancellationToken);
                var driftedDefaults = ApplyFieldDefaultDrift(snapshot);
                await fieldDefaultImporter.ImportAsync(
                    driftedDefaults,
                    TargetOrg,
                    ProjectOwnerType.Organization,
                    result.ProjectNumber,
                    cancellationToken);
                Assert.Empty(fieldDefaultImporter.Warnings);

                var driftReport = await verifier.VerifyAsync(snapshot, TargetOrg, result.ProjectNumber, cancellationToken);
                Assert.Contains(driftReport.Differences, difference =>
                    difference.Severity == VerifySeverity.Error
                    && difference.Category == VerifyCategories.Field
                    && difference.Message.Contains("default value mismatch", StringComparison.Ordinal));
                Assert.Contains(driftReport.Differences, difference =>
                    difference.Severity == VerifySeverity.Error
                    && difference.Category == VerifyCategories.View
                    && difference.Message.Contains("field sum mismatch", StringComparison.Ordinal));
                Assert.DoesNotContain(driftReport.Differences, difference =>
                    difference.Category == VerifyCategories.View
                    && difference.Message.Contains("truncate titles mismatch", StringComparison.Ordinal));
                Assert.Equal(2, driftReport.Differences.Count(difference =>
                    difference.Category == VerifyCategories.View
                    && difference.Message.Contains("show date fields mismatch", StringComparison.Ordinal)));
                Assert.Contains(driftReport.Differences, difference =>
                    difference.Severity == VerifySeverity.Error
                    && difference.Category == VerifyCategories.Workflow
                    && difference.Message.Contains("filter mismatch", StringComparison.Ordinal));
                Assert.Contains(driftReport.Differences, difference =>
                    difference.Severity == VerifySeverity.Error
                    && difference.Category == VerifyCategories.Collaborator
                    && difference.Message.Contains("role mismatch", StringComparison.Ordinal));

                var repairResult = await new ProjectImporter(targetClient)
                {
                    OnConflict = ConflictAction.Update,
                    OperationLogDirectory = operationLogDirectory,
                    BeforeWriteAsync = validateTargetAuthentication,
                    BrowserViewEnrichmentPlanned = true,
                    BrowserFieldDefaultEnrichmentPlanned = true,
                    OrganizationMapping = OrganizationMapping,
                    RepositoryMapping = RepositoryMapping,
                    UserMapping = userMapping,
                }.ImportIntoAsync(snapshot, TargetOrg, result.ProjectNumber, cancellationToken);
                await fieldDefaultImporter.ImportAsync(
                    FieldDefaultUiImporter.CreateClearedDefaultsSnapshot(snapshot),
                    TargetOrg,
                    ProjectOwnerType.Organization,
                    repairResult.ProjectNumber,
                    cancellationToken);
                Assert.Empty(fieldDefaultImporter.Warnings);

                var repairItemResult = await new ItemImporter(targetClient)
                {
                    RepositoryMapping = RepositoryMapping,
                    UserMapping = userMapping,
                    ReapplyCompletedFieldValues = true,
                }.ImportAsync(snapshot, repairResult, operationLogDirectory, cancellationToken);
                Assert.Empty(repairItemResult.Warnings);

                await fieldDefaultImporter.ImportAsync(
                    snapshot,
                    TargetOrg,
                    ProjectOwnerType.Organization,
                    repairResult.ProjectNumber,
                    cancellationToken);
                await viewImporter.EnrichAsync(
                    snapshot,
                    TargetOrg,
                    ProjectOwnerType.Organization,
                    repairResult.ProjectNumber,
                    repairResult.ViewNumbers,
                    cancellationToken);
                await workflowImporter.ImportAsync(
                    snapshot,
                    TargetOrg,
                    repairResult.ProjectNumber,
                    cancellationToken);
                Assert.Empty(fieldDefaultImporter.Warnings);
                Assert.Empty(viewImporter.Warnings);
                Assert.Empty(workflowImporter.Warnings);

                var repairReport = await verifier.VerifyAsync(
                    snapshot,
                    TargetOrg,
                    result.ProjectNumber,
                    cancellationToken);
                Assert.All(
                    repairReport.Categories,
                    category => Assert.Equal(VerifyStatus.Match, category.Status));
                await new FieldDefaultFixtureObserver(targetClient, targetSession).ValidateStandardFixtureAsync(
                    TargetOrg,
                    result.ProjectNumber,
                    cancellationToken);

                Assert.Equal(5, browserVerificationCount);
            }
            finally
            {
                await DeleteProjectAsync(targetClient, result.ProjectId);
            }
        }
        finally
        {
            await SetCollaboratorAsync(sourceClient, sourceProjectId, sourceUserId, "NONE", CancellationToken.None);
        }
    }

    private static void AssertSourceViews(ProjectSnapshot source)
    {
        Assert.All(source.Views, view => Assert.NotNull(view.Ui));
        Assert.Contains(source.Fields, field => field.Name == "Labels" && field.DataType == "LABELS");
        Assert.Contains(source.Fields, field => field.Name == "Fixture Teams" && field.IssueField is not null);

        var sourceTable = Assert.Single(source.Views, view => view.Name == "View 1");
        Assert.Equal("status:Todo", sourceTable.Filter);
        Assert.Equal(["Status"], sourceTable.GroupByFields);
        Assert.Equal("Fixture Number", Assert.Single(sourceTable.SortByFields).Field);
        Assert.Equal("Fixture Select", sourceTable.Ui!.SliceBy);
        Assert.Equal(["Count", "Fixture Number", "Fixture Number 2"], sourceTable.Ui.FieldSum);

        var sourceBoard = Assert.Single(source.Views, view => view.Name == "Fixture Board");
        Assert.Equal("Fixture Select", Assert.Single(sourceBoard.VerticalGroupByFields));
        Assert.Equal(["Fixture Number"], sourceBoard.Ui!.FieldSum);

        var sourceRoadmap = Assert.Single(source.Views, view => view.Name == "Fixture Roadmap");
        Assert.Equal(["Status"], sourceRoadmap.GroupByFields);
        Assert.Equal(["Fixture Number 2"], sourceRoadmap.Ui!.FieldSum);
        Assert.Equal("Quarter", sourceRoadmap.Ui.Roadmap?.Zoom);
        Assert.Contains("Fixture Date", sourceRoadmap.Ui.Roadmap?.Markers ?? []);
        Assert.True(sourceRoadmap.Ui.Roadmap?.TruncateTitles);
        Assert.False(sourceRoadmap.Ui.Roadmap?.ShowDateFields);
        var sourceDatesHidden = Assert.Single(source.Views, view => view.Name == "Fixture Roadmap Dates Hidden");
        Assert.True(sourceDatesHidden.Ui!.Roadmap?.TruncateTitles);
        Assert.False(sourceDatesHidden.Ui.Roadmap?.ShowDateFields);

        var sourceEmptySums = Assert.Single(source.Views, view => view.Name == "Fixture Empty Sums");
        Assert.Equal(["Status"], sourceEmptySums.GroupByFields);
        Assert.Empty(sourceEmptySums.Ui!.FieldSum ?? []);

        var sourceTabOrder = source.Views.OrderBy(view => view.TabPosition).Select(view => view.Name).ToList();
        Assert.False(sourceTabOrder.SequenceEqual(
            source.Views.OrderBy(view => view.Number).Select(view => view.Name),
            StringComparer.Ordinal));
    }

    private static void AssertSourceWorkflows(ProjectSnapshot source)
    {
        Assert.All(source.Workflows, workflow => Assert.NotNull(workflow.Ui));
        Assert.Equal(2, source.Workflows.Count(workflow => workflow.Ui!.Repository is not null));
        var sourceSecondary = Assert.Single(source.Workflows, workflow => workflow.Name == "Auto-add secondary");
        Assert.True(sourceSecondary.Enabled);
        Assert.Equal(SourceFixtureRepository, sourceSecondary.Ui!.Repository);
        Assert.Equal("is:issue label:bug", sourceSecondary.Ui.Filter);
        var sourceDisabled = Assert.Single(source.Workflows, workflow => workflow.Name == "Code changes requested");
        Assert.False(sourceDisabled.Enabled);
        Assert.Equal("In Progress", sourceDisabled.Ui!.StatusValue);
    }

    private static void AssertSourceFieldDefaults(ProjectSnapshot source)
    {
        var expected = FixtureUiSnapshotFactory.Create();
        foreach (var expectedField in expected.Fields.Where(field => field.DefaultValue is not null))
        {
            var actual = Assert.Single(source.Fields, field => field.Name == expectedField.Name);
            Assert.NotNull(actual.DefaultValue);
            Assert.True(FieldDefaultUiImporter.ValuesEqual(
                expectedField.DataType,
                expectedField.DefaultValue!,
                actual.DefaultValue!));
        }
    }

    private static ProjectSnapshot ApplyFieldDefaultDrift(ProjectSnapshot snapshot)
    {
        var drift = FixtureUiSnapshotFactory.CreateFieldDefaultDrift().Fields
            .Where(field => field.DefaultValue is not null)
            .ToDictionary(field => field.Name, field => field.DefaultValue, StringComparer.Ordinal);
        return snapshot with
        {
            Fields = snapshot.Fields.Select(field => drift.TryGetValue(field.Name, out var defaultValue)
                ? field with { DefaultValue = defaultValue }
                : field).ToList(),
        };
    }

    private static ProjectSnapshot BuildRoundTripSnapshot(
        ProjectSnapshot source,
        CollaboratorSnapshot collaborator)
    {
        var shiftedSourceViews = source.Views.Select(view => view with
        {
            TabPosition = view.TabPosition + 1,
        }).ToList();
        var overflowViews = Enumerable.Range(1, 6).Select(index => new ViewSnapshot
        {
            Number = 100 + index,
            TabPosition = index == 1 ? 0 : source.Views.Count + index - 1,
            Name = $"Overflow {index}",
            Layout = "TABLE_LAYOUT",
            GroupByFields = [],
            SortByFields = [],
            VerticalGroupByFields = [],
            VisibleFields = [],
            Ui = new ViewUiSnapshot(),
        }).ToList();
        return source with
        {
            Project = source.Project with { Title = "ghpmv-browser-test-" + Guid.NewGuid().ToString("N") },
            Views = [.. shiftedSourceViews, .. overflowViews],
            Collaborators = [collaborator],
        };
    }

    private static void AssertRoundTrippedViews(ProjectSnapshot snapshot, ProjectSnapshot target)
    {
        Assert.Equal(snapshot.Views.Count, target.Views.Count);
        Assert.True(snapshot.Views.Count > 3);
        Assert.Equal(
            snapshot.Views.OrderBy(view => view.TabPosition).Select(view => view.Name),
            target.Views.OrderBy(view => view.TabPosition).Select(view => view.Name));
        foreach (var expected in snapshot.Views)
        {
            var actual = Assert.Single(target.Views, view => string.Equals(view.Name, expected.Name, StringComparison.Ordinal));
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
                Assert.Equal(roadmap.TruncateTitles, actual.Ui.Roadmap.TruncateTitles);
                Assert.Equal(roadmap.ShowDateFields, actual.Ui.Roadmap.ShowDateFields);
            }
        }
    }

    private static void AssertRoundTrippedWorkflows(ProjectSnapshot snapshot, ProjectSnapshot target)
    {
        Assert.Equal(
            snapshot.Workflows.Select(workflow => workflow.Name).Order(StringComparer.Ordinal),
            target.Workflows.Select(workflow => workflow.Name).Order(StringComparer.Ordinal));
        foreach (var expected in snapshot.Workflows)
        {
            var actual = Assert.Single(target.Workflows, workflow =>
                string.Equals(workflow.Name, expected.Name, StringComparison.Ordinal));
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

    }

    private static void AssertRoundTrippedFieldDefaults(ProjectSnapshot snapshot, ProjectSnapshot target)
    {
        foreach (var expected in snapshot.Fields.Where(field => field.DefaultValue is not null))
        {
            var actual = Assert.Single(target.Fields, field =>
                field.Name == expected.Name && field.DataType == expected.DataType);
            Assert.NotNull(actual.DefaultValue);
            Assert.True(FieldDefaultUiImporter.ValuesEqual(
                expected.DataType,
                expected.DefaultValue!,
                actual.DefaultValue!));
        }
    }

    private static async Task AssertRoadmapDisplayProfileStateAsync(
        string statePath,
        bool truncateTitles,
        bool showDateFields,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(statePath);
        using var state = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var storage = state.RootElement.GetProperty("origins")
            .EnumerateArray()
            .SelectMany(origin => origin.GetProperty("localStorage").EnumerateArray())
            .ToDictionary(
                entry => entry.GetProperty("name").GetString()!,
                entry => entry.GetProperty("value").GetString(),
                StringComparer.Ordinal);

        Assert.Equal(
            truncateTitles.ToString().ToLowerInvariant(),
            storage["projects.roadmapTruncateTitles"]);
        Assert.Equal(
            showDateFields.ToString().ToLowerInvariant(),
            storage["projects.roadmapShowDateFields"]);
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

    private static async Task<Func<CancellationToken, Task>> CreateTargetAuthenticationGuardAsync(
        GitHubGraphQLClient targetClient,
        BrowserSession targetSession,
        CancellationToken cancellationToken)
    {
        var apiLogin = await targetClient.GetViewerLoginAsync(cancellationToken);
        return ct => targetSession.ValidateAuthenticationAsync(apiLogin, ct);
    }

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
