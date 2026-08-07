using Ghpmv.Core.Export;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Integration.Tests;

/// <summary>
/// Verifies the GraphQL-readable Project View import contract against the real API.
/// Browser-only grouping, sorting, and UI settings are intentionally out of scope.
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
        using var client = new GitHubGraphQLClient(Token);
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

    private static ViewSnapshot View(
        int number,
        string name,
        string layout,
        string? filter,
        IReadOnlyList<string> visibleFields) => new()
    {
        Number = number,
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

}
