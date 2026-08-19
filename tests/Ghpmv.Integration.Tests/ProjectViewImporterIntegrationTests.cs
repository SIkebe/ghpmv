using Ghpmv.Core.Export;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;
using Ghpmv.Core.Verify;

namespace Ghpmv.Integration.Tests;

/// <summary>
/// Verifies the GraphQL-readable Project View import contract against the real API.
/// API-only coverage intentionally treats saved tab order as browser-only state.
/// Browser-only grouping, sorting, UI settings, tab order, and drag writes are
/// outside this project's scope.
/// </summary>
public class ProjectViewImporterIntegrationTests
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
    public async Task Import_round_trips_graphql_readable_view_settings()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = IntegrationTestSettings.CreateClient(Token);
        var title = "ghpmv-view-api-test-" + Guid.NewGuid().ToString("N");
        var snapshot = Snapshot(title);
        try
        {
            var (projectId, projectNumber) = await TemporaryProjectFixture.CreateAsync(
                client,
                TargetOrg,
                title,
                cancellationToken);
            var result = await new ProjectImporter(client)
            {
                OperationLogDirectory = IntegrationTestSettings.CreateOperationLogDirectory(),
            }.ImportIntoAsync(snapshot, TargetOrg, projectNumber, cancellationToken);

            Assert.Equal(ProjectImportOutcome.Updated, result.Outcome);
            Assert.Equal(projectId, result.ProjectId);
            Assert.Equal(snapshot.Views.Count, result.ViewNumbers.Count);
            Assert.Equal(snapshot.Views.Select(view => view.Number).Order(), result.ViewNumbers.Keys.Order());
            Assert.Equal(snapshot.Views.Count, result.ViewNumbers.Values.Distinct().Count());
            Assert.All(result.ViewNumbers.Values, number => Assert.True(number > 0));

            var imported = await ExportUntilViewsMatchAsync(
                client,
                projectNumber,
                snapshot.Views,
                cancellationToken);

            foreach (var expected in snapshot.Views)
            {
                var actual = Assert.Single(imported.Views, view => view.Name == expected.Name);
                Assert.Equal(result.ViewNumbers[expected.Number], actual.Number);
                Assert.Equal(expected.Layout, actual.Layout);
                Assert.Equal(expected.Filter, actual.Filter);
                Assert.Equal(expected.VisibleFields, actual.VisibleFields);
            }
        }
        finally
        {
            await TemporaryProjectFixture.DeleteAllByTitleAsync(
                client,
                TargetOrg,
                title,
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task Api_only_import_warns_for_browser_tab_order_and_export_leaves_it_uncaptured()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = IntegrationTestSettings.CreateClient(Token);
        var title = "ghpmv-view-position-test-" + Guid.NewGuid().ToString("N");
        var initial = PositionedSnapshot(title);
        var createLogDirectory = IntegrationTestSettings.CreateOperationLogDirectory();
        try
        {
            var createImporter = new ProjectImporter(client)
            {
                OperationLogDirectory = createLogDirectory,
            };
            var result = await createImporter.ImportAsync(initial, TargetOrg, cancellationToken);
            Assert.Contains(createImporter.Warnings, warning =>
                warning.Contains("tab order requires browser automation", StringComparison.Ordinal));

            var imported = await ExportUntilViewsMatchAsync(
                client,
                result.ProjectNumber,
                initial.Views,
                cancellationToken);
            Assert.All(imported.Views, view => Assert.Null(view.TabPosition));

            var report = ProjectVerifier.Compare(initial, imported);
            Assert.Contains(report.Differences, difference =>
                difference.Severity == VerifySeverity.Warning
                && difference.Category == "View"
                && difference.Message.Contains("tab order was captured in the source", StringComparison.Ordinal));
            Assert.Contains(report.Categories, category =>
                category.Category == "View" && category.Status == VerifyStatus.NotVerified);
        }
        finally
        {
            await TemporaryProjectFixture.DeleteAllByTitleAsync(
                client,
                TargetOrg,
                title,
                CancellationToken.None);
            TryDeleteDirectory(createLogDirectory);
        }
    }

    private static ProjectSnapshot Snapshot(string title) => new()
    {
        SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
        Project = new ProjectInfoSnapshot
        {
            Title = title,
            Public = false,
            Closed = false,
        },
        Fields =
        [
            new FieldSnapshot { Name = "API Probe Text", DataType = "TEXT" },
        ],
        Views =
        [
            View(
                number: 7,
                name: "View 1",
                layout: "TABLE_LAYOUT",
                filter: "status:Todo",
                visibleFields: ["Title", "Status", "API Probe Text"]),
            View(
                number: 11,
                name: "API Roadmap",
                layout: "ROADMAP_LAYOUT",
                filter: null,
                visibleFields: []),
        ],
        Workflows = [],
        Items = [],
    };

    private static ProjectSnapshot PositionedSnapshot(string title) => Snapshot(title) with
    {
        Views =
        [
            View(7, "Position A", "TABLE_LAYOUT", filter: null, visibleFields: [], tabPosition: 1),
            View(11, "Position B", "TABLE_LAYOUT", filter: null, visibleFields: [], tabPosition: 2),
            View(13, "Position C", "TABLE_LAYOUT", filter: null, visibleFields: [], tabPosition: 0),
        ],
    };

    private static ViewSnapshot View(
        int number,
        string name,
        string layout,
        string? filter,
        IReadOnlyList<string> visibleFields,
        int? tabPosition = null) => new()
    {
        Number = number,
        TabPosition = tabPosition,
        Name = name,
        Layout = layout,
        Filter = filter,
        GroupByFields = [],
        SortByFields = [],
        VerticalGroupByFields = [],
        VisibleFields = visibleFields,
    };

    private static async Task<ProjectSnapshot> ExportUntilViewsMatchAsync(
        GitHubGraphQLClient client,
        int projectNumber,
        IReadOnlyList<ViewSnapshot> expectedViews,
        CancellationToken cancellationToken)
    {
        var exporter = new ProjectExporter(client);
        ProjectSnapshot? snapshot = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }

            snapshot = await exporter.ExportAsync(TargetOrg, projectNumber, cancellationToken);
            if (expectedViews.All(expected => snapshot.Views.Any(actual =>
                    actual.Name == expected.Name
                    && actual.Layout == expected.Layout
                    && actual.Filter == expected.Filter
                    && actual.VisibleFields.SequenceEqual(expected.VisibleFields))))
            {
                return snapshot;
            }
        }

        return snapshot ?? throw new InvalidOperationException("The imported project could not be exported.");
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

}
