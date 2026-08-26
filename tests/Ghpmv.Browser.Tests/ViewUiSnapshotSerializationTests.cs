using Ghpmv.Core.Snapshot;

namespace Ghpmv.Browser.Tests;

/// <summary>
/// Serialization round-trip for the UI-only view settings added in M6
/// (<see cref="ViewUiSnapshot"/> incl. slicing, field sums, and roadmap settings).
/// No Playwright required.
/// </summary>
public class ViewUiSnapshotSerializationTests
{
    [Fact]
    public async Task Ui_settings_round_trip_through_snapshot_file()
    {
        var scrapedAt = new DateTimeOffset(2026, 7, 5, 1, 2, 3, TimeSpan.Zero);
        var snapshot = new ProjectSnapshot
        {
            SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
            StatusUpdates = [],
            LinkedRepositories = [],
            LinkedTeams = [],
            Project = new ProjectInfoSnapshot { Title = "t", Public = false, Closed = false, Template = false },
            Fields = [],
            Views =
            [
                new ViewSnapshot
                {
                    Number = 3,
                    Name = "Fixture Roadmap",
                    Layout = "ROADMAP_LAYOUT",
                    Filter = "",
                    GroupByFields = [],
                    SortByFields = [],
                    VerticalGroupByFields = [],
                    VisibleFields = ["Title"],
                    Ui = new ViewUiSnapshot
                    {
                        SliceBy = "Assignees",
                        FieldSum = ["Fixture Number"],
                        Roadmap = new RoadmapSettingsSnapshot
                        {
                            StartField = "Fixture Date",
                            TargetField = "Fixture Sprint end",
                            Zoom = "Month",
                            Markers = ["Fixture Sprint"],
                            TruncateTitles = true,
                            ShowDateFields = false,
                        },
                        ScrapedAt = scrapedAt,
                    },
                },
                new ViewSnapshot
                {
                    Number = 4,
                    Name = "No UI",
                    Layout = "TABLE_LAYOUT",
                    GroupByFields = [],
                    SortByFields = [],
                    VerticalGroupByFields = [],
                    VisibleFields = [],
                },
                new ViewSnapshot
                {
                    Number = 5,
                    Name = "Empty sums",
                    Layout = "TABLE_LAYOUT",
                    GroupByFields = ["Status"],
                    SortByFields = [],
                    VerticalGroupByFields = [],
                    VisibleFields = [],
                    Ui = new ViewUiSnapshot
                    {
                        FieldSum = [],
                    },
                },
            ],
            Workflows = [],
            Items = [],
        };

        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-browser-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await SnapshotFile.SaveAsync(snapshot, directory, cancellationToken);
            var loaded = await SnapshotFile.LoadAsync(directory, cancellationToken);

            Assert.Equal(3, loaded.Views.Count);

            var roadmap = loaded.Views[0];
            Assert.NotNull(roadmap.Ui);
            Assert.Equal("Assignees", roadmap.Ui!.SliceBy);
            Assert.Equal(["Fixture Number"], roadmap.Ui.FieldSum);
            Assert.Equal(scrapedAt, roadmap.Ui.ScrapedAt);
            Assert.NotNull(roadmap.Ui.Roadmap);
            Assert.Equal("Fixture Date", roadmap.Ui.Roadmap!.StartField);
            Assert.Equal("Fixture Sprint end", roadmap.Ui.Roadmap.TargetField);
            Assert.Equal("Month", roadmap.Ui.Roadmap.Zoom);
            Assert.Equal(["Fixture Sprint"], roadmap.Ui.Roadmap.Markers);
            Assert.True(roadmap.Ui.Roadmap.TruncateTitles);
            Assert.False(roadmap.Ui.Roadmap.ShowDateFields);

            Assert.Null(loaded.Views[1].Ui);
            Assert.Empty(loaded.Views[2].Ui!.FieldSum!);
        }

        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Roadmap_display_options_round_trip_all_boolean_combinations(
        bool truncateTitles,
        bool showDateFields)
    {
        var snapshot = new ProjectSnapshot
        {
            SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
            Project = new ProjectInfoSnapshot { Title = "t", Public = false, Closed = false, Template = false },
            Fields = [],
            Views =
            [
                new ViewSnapshot
                {
                    Number = 1,
                    Name = "Roadmap",
                    Layout = "ROADMAP_LAYOUT",
                    GroupByFields = [],
                    SortByFields = [],
                    VerticalGroupByFields = [],
                    VisibleFields = [],
                    Ui = new ViewUiSnapshot
                    {
                        Roadmap = new RoadmapSettingsSnapshot
                        {
                            TruncateTitles = truncateTitles,
                            ShowDateFields = showDateFields,
                        },
                    },
                },
            ],
            Workflows = [],
            Items = [],
            StatusUpdates = [],
            LinkedRepositories = [],
            LinkedTeams = [],
        };
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-browser-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await SnapshotFile.SaveAsync(snapshot, directory, TestContext.Current.CancellationToken);
            var loaded = await SnapshotFile.LoadAsync(directory, TestContext.Current.CancellationToken);
            var roadmap = Assert.Single(loaded.Views).Ui!.Roadmap!;

            Assert.Equal(truncateTitles, roadmap.TruncateTitles);
            Assert.Equal(showDateFields, roadmap.ShowDateFields);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Workflow_ui_settings_round_trip_through_snapshot_file()
    {
        var scrapedAt = new DateTimeOffset(2026, 7, 5, 4, 5, 6, TimeSpan.Zero);
        var snapshot = new ProjectSnapshot
        {
            SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
            StatusUpdates = [],
            LinkedRepositories = [],
            LinkedTeams = [],
            Project = new ProjectInfoSnapshot { Title = "t", Public = false, Closed = false, Template = false },
            Fields = [],
            Views = [],
            Workflows =
            [
                new WorkflowSnapshot
                {
                    Number = 6,
                    Name = "Item added to project",
                    Enabled = true,
                    Ui = new WorkflowUiSnapshot
                    {
                        ContentTypes = ["ISSUE", "PULL_REQUEST"],
                        StatusValue = "Todo",
                        ScrapedAt = scrapedAt,
                    },
                },
                new WorkflowSnapshot
                {
                    Number = 7,
                    Name = "Auto-add to project",
                    Enabled = true,
                    Ui = new WorkflowUiSnapshot
                    {
                        Filter = "is:issue is:open",
                        Repository = "fixture-repo",
                        ScrapedAt = scrapedAt,
                    },
                },
                new WorkflowSnapshot { Number = 1, Name = "Item closed", Enabled = false },
            ],
            Items = [],
        };

        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-browser-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await SnapshotFile.SaveAsync(snapshot, directory, cancellationToken);
            var loaded = await SnapshotFile.LoadAsync(directory, cancellationToken);

            Assert.Equal(3, loaded.Workflows.Count);

            var itemAdded = loaded.Workflows[0];
            Assert.NotNull(itemAdded.Ui);
            Assert.Equal(["ISSUE", "PULL_REQUEST"], itemAdded.Ui!.ContentTypes);
            Assert.Equal("Todo", itemAdded.Ui.StatusValue);
            Assert.Null(itemAdded.Ui.Filter);
            Assert.Null(itemAdded.Ui.Repository);
            Assert.Equal(scrapedAt, itemAdded.Ui.ScrapedAt);

            var autoAdd = loaded.Workflows[1];
            Assert.NotNull(autoAdd.Ui);
            Assert.Equal("is:issue is:open", autoAdd.Ui!.Filter);
            Assert.Equal("fixture-repo", autoAdd.Ui.Repository);
            Assert.Null(autoAdd.Ui.ContentTypes);
            Assert.Null(autoAdd.Ui.StatusValue);

            Assert.Null(loaded.Workflows[2].Ui);
            Assert.False(loaded.Workflows[2].Enabled);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
