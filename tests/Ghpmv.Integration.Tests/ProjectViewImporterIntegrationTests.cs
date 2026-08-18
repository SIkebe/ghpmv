using Ghpmv.Core.Export;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;
using Ghpmv.Core.Verify;

namespace Ghpmv.Integration.Tests;

/// <summary>
/// Verifies the GraphQL-readable Project View import contract against the real API.
/// This includes POSITION-based tab order reads, creation order, API-only warnings,
/// and verification. Browser-only grouping, sorting, UI settings, and drag writes
/// are intentionally out of scope.
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
    public async Task Import_and_verify_use_graphql_tab_positions_while_api_only_update_warns()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = IntegrationTestSettings.CreateClient(Token);
        var title = "ghpmv-view-position-test-" + Guid.NewGuid().ToString("N");
        var initial = PositionedSnapshot(title);
        var createLogDirectory = IntegrationTestSettings.CreateOperationLogDirectory();
        var updateLogDirectory = IntegrationTestSettings.CreateOperationLogDirectory();
        try
        {
            var createImporter = new ProjectImporter(client)
            {
                OperationLogDirectory = createLogDirectory,
            };
            var result = await createImporter.ImportAsync(initial, TargetOrg, cancellationToken);
            Assert.DoesNotContain(createImporter.Warnings, warning =>
                warning.Contains("tab order differs", StringComparison.Ordinal));

            var expectedInitialOrder = initial.Views
                .OrderBy(view => view.TabPosition)
                .Select(view => view.Name)
                .ToArray();
            var imported = await ExportUntilTabOrderMatchesAsync(
                client,
                result.ProjectNumber,
                expectedInitialOrder,
                cancellationToken);
            Assert.Equal(expectedInitialOrder, imported.Views.OrderBy(view => view.TabPosition).Select(view => view.Name));
            Assert.Equal(
                Enumerable.Range(0, expectedInitialOrder.Length).Select(position => (int?)position),
                imported.Views.OrderBy(view => view.TabPosition).Select(view => view.TabPosition));

            var reordered = initial with
            {
                Views = initial.Views.Select(view => view.Name switch
                {
                    "Position B" => view with { TabPosition = 0 },
                    "Position A" => view with { TabPosition = 1 },
                    "Position C" => view with { TabPosition = 2 },
                    _ => throw new InvalidOperationException($"Unexpected View '{view.Name}'."),
                }).ToList(),
            };
            var apiOnlyUpdate = new ProjectImporter(client)
            {
                OperationLogDirectory = updateLogDirectory,
            };
            await apiOnlyUpdate.ImportIntoAsync(
                reordered,
                TargetOrg,
                result.ProjectNumber,
                cancellationToken);

            Assert.Contains(apiOnlyUpdate.Warnings, warning =>
                warning.Contains("tab order differs", StringComparison.Ordinal)
                && warning.Contains("browser automation", StringComparison.Ordinal));

            var unchangedTarget = await ExportUntilTabOrderMatchesAsync(
                client,
                result.ProjectNumber,
                expectedInitialOrder,
                cancellationToken);
            var report = ProjectVerifier.Compare(reordered, unchangedTarget);
            Assert.Contains(report.Differences, difference =>
                difference.Severity == VerifySeverity.Error
                && difference.Category == "View"
                && difference.Message.Contains("tab order mismatch", StringComparison.Ordinal)
                && difference.Message.Contains("Position B, Position A, Position C", StringComparison.Ordinal)
                && difference.Message.Contains("Position C, Position A, Position B", StringComparison.Ordinal));
        }
        finally
        {
            await TemporaryProjectFixture.DeleteAllByTitleAsync(
                client,
                TargetOrg,
                title,
                CancellationToken.None);
            TryDeleteDirectory(createLogDirectory);
            TryDeleteDirectory(updateLogDirectory);
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

    private static async Task<ProjectSnapshot> ExportUntilTabOrderMatchesAsync(
        GitHubGraphQLClient client,
        int projectNumber,
        IReadOnlyList<string> expectedOrder,
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
            if (snapshot.Views
                .OrderBy(view => view.TabPosition)
                .Select(view => view.Name)
                .SequenceEqual(expectedOrder, StringComparer.Ordinal))
            {
                return snapshot;
            }
        }

        return snapshot ?? throw new InvalidOperationException("The imported View order could not be exported.");
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
