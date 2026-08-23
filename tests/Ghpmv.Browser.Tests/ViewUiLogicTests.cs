using Ghpmv.Core.Browser;
using Ghpmv.Core.Snapshot;
using Ghpmv.Core.Verify;
using Microsoft.Playwright;

namespace Ghpmv.Browser.Tests;

/// <summary>
/// Pure-logic unit tests for the browser module (no Playwright required):
/// menu-value parsing, pre-flight warning collection and the verifier's
/// UI-settings comparison.
/// </summary>
public class ViewUiLogicTests
{
    // ----- ViewUiExporter.ParseMenuValue / ParseListValue -----

    [Theory]
    [InlineData("Group by: Status", "Status")]
    [InlineData("Group by:\nStatus", "Status")]
    [InlineData("Zoom level: Month", "Month")]
    [InlineData("Group by: none", null)]
    [InlineData("Group by: None", null)]
    [InlineData("Group by:", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseMenuValue_extracts_the_value_and_normalizes_none(string? text, string? expected)
        => Assert.Equal(expected, ViewUiExporter.ParseMenuValue(text));

    [Fact]
    public void ParseMenuValue_returns_the_whole_text_when_there_is_no_label()
        => Assert.Equal("Status", ViewUiExporter.ParseMenuValue("Status"));

    [Fact]
    public void ParseListValue_splits_on_commas()
        => Assert.Equal(["Milestone", "Fixture Sprint"], ViewUiExporter.ParseListValue("Markers: Milestone, Fixture Sprint"));

    [Fact]
    public void ParseListValue_splits_prose_conjunctions()
    {
        // The UI renders lists in prose form (E2E discovery, 2026-07-06).
        Assert.Equal(["Count", "Fixture Number"], ViewUiExporter.ParseListValue("Field sum: Count and Fixture Number"));
        Assert.Equal(["A", "B", "C"], ViewUiExporter.ParseListValue("Fields: A, B, and C"));
    }

    [Fact]
    public void ParseListValue_returns_null_for_none()
        => Assert.Null(ViewUiExporter.ParseListValue("Markers: none"));

    [Theory]
    [InlineData("Sort by: Fixture Number, ascending", "Ascending", true)]
    [InlineData("Sort by:\nFixture Number\nDescending", "Descending", true)]
    [InlineData("Sort by: Fixture Number, ascending", "Descending", false)]
    [InlineData(null, "Ascending", false)]
    public void HasSortDirection_detects_the_current_direction(
        string? menuText,
        string directionName,
        bool expected)
        => Assert.Equal(expected, ViewUiImporter.HasSortDirection(menuText, directionName));

    [Theory]
    [InlineData("  Fixture   Sprint\nend ", "Fixture Sprint end")]
    [InlineData("   ", null)]
    public void NormalizeUiText_collapses_whitespace(string text, string? expected)
        => Assert.Equal(expected, ViewUiExporter.NormalizeUiText(text));

    // ----- pre-flight warning collection -----

    [Fact]
    public void CollectPreflightWarnings_is_empty_when_all_settings_are_applicable()
    {
        var snapshot = Snapshot(
            fields: ["Status", "Fixture Date", "Fixture Sprint"],
            View("Board", "BOARD_LAYOUT", groupBy: ["Status"], sortBy: [Sort("Status", "ASC")]),
            View("Roadmap", "ROADMAP_LAYOUT") with
            {
                Ui = new ViewUiSnapshot
                {
                    SliceBy = "Status",
                    Roadmap = new RoadmapSettingsSnapshot { StartField = "Fixture Date", TargetField = "Fixture Sprint end" },
                },
            });

        Assert.Empty(ViewUiImporter.CollectPreflightWarnings(snapshot));
    }

    [Fact]
    public void CollectPreflightWarnings_reports_missing_fields_and_extra_sort_keys()
    {
        var snapshot = Snapshot(
            fields: ["Status"],
            View("V", "TABLE_LAYOUT",
                groupBy: ["Missing group"],
                sortBy: [Sort("Status", "ASC"), Sort("Missing sort", "DESC")]) with
            {
                Ui = new ViewUiSnapshot
                {
                    SliceBy = "Missing slice",
                    Roadmap = new RoadmapSettingsSnapshot { StartField = "Missing start", TargetField = "Status end" },
                },
            });

        var warnings = ViewUiImporter.CollectPreflightWarnings(snapshot);

        Assert.Contains(warnings, w => w.Contains("group-by field 'Missing group'", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("sort-by field 'Missing sort'", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("only the first of 2 sort keys", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("slice-by field 'Missing slice'", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("start date field 'Missing start'", StringComparison.Ordinal));
        // "Status end" resolves to the iteration-style suffix of the existing "Status" field.
        Assert.DoesNotContain(warnings, w => w.Contains("'Status end'", StringComparison.Ordinal));
        Assert.Equal(5, warnings.Count);
    }

    [Fact]
    public void CollectPreflightWarnings_reports_missing_field_sum_fields()
    {
        var snapshot = Snapshot(
            fields: ["Status"],
            View("Board", "BOARD_LAYOUT") with
            {
                Ui = new ViewUiSnapshot
                {
                    FieldSum = ["Count", "Missing number"],
                },
            });

        var warnings = ViewUiImporter.CollectPreflightWarnings(snapshot);

        // "Count" is a built-in Field sum entry, not a field.
        Assert.Contains(warnings, w => w.Contains("field-sum field 'Missing number'", StringComparison.Ordinal));
        Assert.Single(warnings);
    }

    [Theory]
    [InlineData("TABLE_LAYOUT")]
    [InlineData("BOARD_LAYOUT")]
    [InlineData("ROADMAP_LAYOUT")]
    public void Field_sum_plan_supports_every_layout(string layout)
    {
        var view = View("Grouped", layout) with
        {
            GroupByFields = ["Status"],
            Ui = new ViewUiSnapshot
            {
                FieldSum = ["Count", "Fixture Number", "Fixture Number 2"],
            },
        };

        Assert.Equal(
            ["Count", "Fixture Number", "Fixture Number 2"],
            ViewUiImporter.FieldSumValuesToApply(view));
    }

    [Theory]
    [InlineData("TABLE_LAYOUT")]
    [InlineData("ROADMAP_LAYOUT")]
    public void Field_sum_plan_skips_ungrouped_layouts_without_the_control(string layout)
    {
        var view = View("Ungrouped", layout) with
        {
            Ui = new ViewUiSnapshot(),
        };

        Assert.Null(ViewUiImporter.FieldSumValuesToApply(view));
    }

    [Fact]
    public void Field_sum_plan_does_not_write_uncaptured_ui_state()
    {
        var view = View("Grouped", "TABLE_LAYOUT");

        Assert.Null(ViewUiImporter.FieldSumValuesToApply(view));
    }

    [Fact]
    public void Field_sum_plan_clears_when_captured_selection_is_empty()
    {
        var view = View("Grouped", "ROADMAP_LAYOUT") with
        {
            GroupByFields = ["Status"],
            Ui = new ViewUiSnapshot(),
        };

        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(
            ViewUiImporter.FieldSumValuesToApply(view)));
    }

    [Fact]
    public void Field_sum_plan_ignores_unknown_layouts()
    {
        var view = View("Unknown", "UNKNOWN_LAYOUT") with
        {
            Ui = new ViewUiSnapshot { FieldSum = ["Count"] },
        };

        Assert.Null(ViewUiImporter.FieldSumValuesToApply(view));
    }

    [Theory]
    [InlineData("TABLE_LAYOUT", true, true)]
    [InlineData("ROADMAP_LAYOUT", true, true)]
    [InlineData("BOARD_LAYOUT", false, true)]
    [InlineData("TABLE_LAYOUT", false, false)]
    [InlineData("ROADMAP_LAYOUT", false, false)]
    [InlineData("UNKNOWN_LAYOUT", true, false)]
    public void Field_sum_control_availability_depends_on_layout_and_grouping(
        string layout,
        bool grouped,
        bool expected)
    {
        var view = View("View", layout) with
        {
            GroupByFields = grouped ? ["Status"] : [],
        };

        Assert.Equal(expected, ViewUiImporter.FieldSumControlExpected(view));
    }

    [Fact]
    public void Persistence_check_accepts_saved_grouping_slice_and_unordered_field_sums()
    {
        var view = View("Table", "TABLE_LAYOUT", groupBy: ["Status"]) with
        {
            Ui = new ViewUiSnapshot
            {
                SliceBy = "Fixture Select",
                FieldSum = ["Count", "Fixture Number"],
            },
        };
        var persisted = new ViewUiImporter.PersistedViewSettings(
            GroupBy: "Status",
            ColumnBy: null,
            SliceBy: "Fixture Select",
            FieldSumAvailable: true,
            FieldSum: ["Fixture Number", "Count"]);

        Assert.Empty(ViewUiImporter.CollectPersistenceDifferences(view, persisted));
    }

    [Fact]
    public void Persistence_check_reports_grouping_slice_and_field_sum_loss()
    {
        var view = View("Table", "TABLE_LAYOUT", groupBy: ["Status"]) with
        {
            Ui = new ViewUiSnapshot
            {
                SliceBy = "Fixture Select",
                FieldSum = ["Count", "Fixture Number"],
            },
        };
        var persisted = new ViewUiImporter.PersistedViewSettings(
            GroupBy: null,
            ColumnBy: null,
            SliceBy: null,
            FieldSumAvailable: false,
            FieldSum: []);

        var differences = ViewUiImporter.CollectPersistenceDifferences(view, persisted);

        Assert.Contains(differences, difference => difference.StartsWith("grouping expected", StringComparison.Ordinal));
        Assert.Contains(differences, difference => difference.StartsWith("slice-by expected", StringComparison.Ordinal));
        Assert.Contains(differences, difference => difference == "field-sum control is unavailable");
        Assert.Equal(3, differences.Count);
    }

    [Fact]
    public void Persistence_check_reports_board_column_loss()
    {
        var view = View("Board", "BOARD_LAYOUT", groupBy: ["Status"]) with
        {
            VerticalGroupByFields = ["Fixture Select"],
            Ui = new ViewUiSnapshot { FieldSum = ["Fixture Number"] },
        };
        var persisted = new ViewUiImporter.PersistedViewSettings(
            GroupBy: "Status",
            ColumnBy: null,
            SliceBy: null,
            FieldSumAvailable: true,
            FieldSum: ["Fixture Number"]);

        var difference = Assert.Single(ViewUiImporter.CollectPersistenceDifferences(view, persisted));

        Assert.StartsWith("column-by expected", difference, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(false, true, true, true)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, false, false)]
    public void Disabled_checkbox_change_is_reported_only_when_state_cannot_match(
        bool shouldBeChecked,
        bool isChecked,
        bool isDisabled,
        bool expected)
        => Assert.Equal(
            expected,
            ViewUiImporter.DisabledCheckboxChangeRequired(shouldBeChecked, isChecked, isDisabled));

    [Fact]
    public void FixtureUiSnapshotFactory_creates_importable_standard_views_and_workflows()
    {
        var snapshot = FixtureUiSnapshotFactory.Create("fixture-repo");

        Assert.Equal(
            ["View 1", "Fixture Board", "Fixture Roadmap", "Fixture Empty Sums"],
            snapshot.Views.Select(v => v.Name));
        Assert.Equal(
            ["Fixture Roadmap", "View 1", "Fixture Board", "Fixture Empty Sums"],
            snapshot.Views.OrderBy(view => view.TabPosition).Select(view => view.Name));
        Assert.Equal(
            ["Count", "Fixture Number", "Fixture Number 2"],
            snapshot.Views.Single(view => view.Name == "View 1").Ui!.FieldSum);
        Assert.Equal(
            ["Fixture Number 2"],
            snapshot.Views.Single(view => view.Name == "Fixture Roadmap").Ui!.FieldSum);
        Assert.Empty(snapshot.Views.Single(view => view.Name == "Fixture Empty Sums").Ui!.FieldSum!);
        Assert.Contains(snapshot.Fields, field =>
            field.Name == "Fixture Teams"
            && field.DataType == "MULTI_SELECT"
            && field.IssueField is not null);
        Assert.Contains(snapshot.Workflows, w => w.Name == "Auto-add to project" && w.Ui?.Repository == "fixture-repo");
        Assert.Contains(snapshot.Workflows, w => w.Name == "Auto-add secondary" && w.Ui?.Filter == "is:issue label:bug");
        Assert.Empty(ViewUiImporter.CollectPreflightWarnings(snapshot));
        Assert.Empty(WorkflowUiImporter.CollectPreflightWarnings(snapshot, WorkflowUiImporter.DefaultMaxAutoAddWorkflows));
    }

    [Fact]
    public void FixtureUiSnapshotFactory_field_sum_drift_only_changes_View_1_field_sum()
    {
        var expected = FixtureUiSnapshotFactory.Create("fixture-repo");
        var drifted = FixtureUiSnapshotFactory.CreateFieldSumDrift("fixture-repo");

        Assert.Equal(expected.Views.Count, drifted.Views.Count);
        foreach (var view in expected.Views)
        {
            var actual = Assert.Single(drifted.Views, candidate => candidate.Name == view.Name);
            Assert.Equal(view.Number, actual.Number);
            Assert.Equal(view.TabPosition, actual.TabPosition);
            Assert.Equal(view.Layout, actual.Layout);
            Assert.Equal(view.Filter, actual.Filter);
            Assert.Equal(view.GroupByFields, actual.GroupByFields);
            Assert.Equal(view.SortByFields, actual.SortByFields);
            Assert.Equal(view.VerticalGroupByFields, actual.VerticalGroupByFields);
            Assert.Equal(view.VisibleFields, actual.VisibleFields);
            Assert.Equal(view.Ui!.SliceBy, actual.Ui!.SliceBy);
            if (view.Name == "View 1")
            {
                Assert.Equal(["Count", "Fixture Number"], actual.Ui.FieldSum);
            }
            else
            {
                Assert.Equal(view.Ui.FieldSum, actual.Ui.FieldSum);
            }

            Assert.Equal(view.Ui.Roadmap?.StartField, actual.Ui.Roadmap?.StartField);
            Assert.Equal(view.Ui.Roadmap?.TargetField, actual.Ui.Roadmap?.TargetField);
            Assert.Equal(view.Ui.Roadmap?.Zoom, actual.Ui.Roadmap?.Zoom);
            if (view.Ui.Roadmap is not null)
            {
                Assert.Equal(view.Ui.Roadmap.Markers, actual.Ui.Roadmap!.Markers);
            }
        }
    }

    [Fact]
    public void Tab_move_plan_is_empty_when_order_already_matches()
        => Assert.Empty(ViewUiImporter.BuildTabMovePlan([1, 2, 3], [1, 2, 3]));

    [Fact]
    public void Tab_move_plan_uses_one_drag_for_a_rotation()
    {
        var moves = ViewUiImporter.BuildTabMovePlan([4, 1, 2, 3], [1, 2, 3, 4]);

        var move = Assert.Single(moves);
        Assert.Equal(new ViewUiImporter.TabMove(4, 3, PlaceBefore: false), move);
        Assert.Equal([1, 2, 3, 4], ApplyMoves([4, 1, 2, 3], moves));
    }

    [Fact]
    public void Tab_move_plan_uses_the_minimum_for_reverse_order()
    {
        var moves = ViewUiImporter.BuildTabMovePlan([8, 7, 6, 5, 4, 3, 2, 1], [1, 2, 3, 4, 5, 6, 7, 8]);

        Assert.Equal(7, moves.Count);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], ApplyMoves([8, 7, 6, 5, 4, 3, 2, 1], moves));
    }

    [Fact]
    public void Tab_move_plan_handles_more_tabs_than_fit_in_the_viewport()
    {
        int[] current = [12, 1, 3, 2, 5, 4, 7, 6, 9, 8, 11, 10];
        int[] desired = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

        var moves = ViewUiImporter.BuildTabMovePlan(current, desired);

        Assert.Equal(desired, ApplyMoves(current, moves));
        Assert.Equal(6, moves.Count);
    }

    [Fact]
    public void Tab_move_plan_keeps_nonfixed_anchors_in_final_relative_order()
    {
        int[] current = [4, 3, 1, 2];
        int[] desired = [1, 2, 3, 4];

        var moves = ViewUiImporter.BuildTabMovePlan(current, desired);

        Assert.Equal(2, moves.Count);
        Assert.Equal(desired, ApplyMoves(current, moves));
    }

    [Fact]
    public void Tab_move_plan_reaches_the_target_for_every_five_tab_permutation()
    {
        int[] desired = [1, 2, 3, 4, 5];
        foreach (var current in Permutations(desired))
        {
            var moves = ViewUiImporter.BuildTabMovePlan(current, desired);

            Assert.Equal(desired, ApplyMoves(current, moves));
            Assert.Equal(desired.Length - LongestIncreasingSubsequenceLength(current), moves.Count);
        }
    }

    [Fact]
    public async Task Tab_move_execution_reports_drag_and_final_readback_failures()
    {
        var moves = new List<ViewUiImporter.TabMove>
        {
            new(2, 1, PlaceBefore: true),
        };
        var desired = new List<int> { 2, 1 };
        var names = new Dictionary<int, string> { [1] = "First", [2] = "Second" };

        var warnings = await ViewUiImporter.ApplyTabMovesAsync(
            moves,
            desired,
            names,
            (_, _) => throw new PlaywrightException("forced drag failure"),
            _ => Task.FromResult<IReadOnlyList<int>>([1, 2]),
            TestContext.Current.CancellationToken);

        Assert.Contains(warnings, warning =>
            warning.Contains("view tab 'Second' could not be reordered", StringComparison.Ordinal)
            && warning.Contains("forced drag failure", StringComparison.Ordinal));
        Assert.Contains(warnings, warning =>
            warning.Contains("could not be fully applied", StringComparison.Ordinal)
            && warning.Contains("expected [Second, First]", StringComparison.Ordinal)
            && warning.Contains("actual [First, Second]", StringComparison.Ordinal));
        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public async Task Tab_reorder_read_failure_is_recoverable()
    {
        var warnings = new List<string>();

        await ViewUiImporter.ApplyTabOrderRecoverablyAsync(
            () => throw new PlaywrightException("forced DOM read failure"),
            warnings);

        var warning = Assert.Single(warnings);
        Assert.Contains("view tab order could not be applied", warning, StringComparison.Ordinal);
        Assert.Contains("forced DOM read failure", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tab_order_poll_retries_an_incomplete_connection_until_all_mapped_views_are_visible()
    {
        var reads = new Queue<IReadOnlyList<int>>(
        [
            [1],
            [1, 8],
        ]);
        var delays = 0;

        var result = await ViewUiImporter.PollTabOrderAsync(
            _ => Task.FromResult(reads.Dequeue()),
            order => order.Count == 2,
            _ =>
            {
                delays++;
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal([1, 8], result);
        Assert.Empty(reads);
        Assert.Equal(1, delays);
    }

    [Theory]
    [InlineData("/orgs/octo/projects/7/views/42", 42)]
    [InlineData("/orgs/octo/projects/7/views/42?pane=info", 42)]
    [InlineData("https://github.com/orgs/octo/projects/7/views/42", 42)]
    public void ParseViewNumber_reads_saved_tab_href(string href, int expected)
        => Assert.Equal(expected, ViewTabOrder.ParseViewNumber(href));

    [Fact]
    public void ApplyViewTabOrder_overrides_graphql_enumeration_order()
    {
        var table = View("Table", "TABLE_LAYOUT") with { Number = 1 };
        var board = View("Board", "BOARD_LAYOUT") with { Number = 2 };
        var roadmap = View("Roadmap", "ROADMAP_LAYOUT") with { Number = 3 };

        var result = ViewTabOrder.Apply([table, board, roadmap], [3, 1, 2]);

        Assert.Equal(
            ["Roadmap", "Table", "Board"],
            result.OrderBy(view => view.TabPosition).Select(view => view.Name));
        Assert.Equal(1, result.Single(view => view.Name == "Table").TabPosition);
        Assert.Equal(2, result.Single(view => view.Name == "Board").TabPosition);
        Assert.Equal(0, result.Single(view => view.Name == "Roadmap").TabPosition);
    }

    [Fact]
    public void ApplyViewTabOrder_rejects_an_incomplete_tab_strip()
    {
        var views =
            new[]
            {
                View("Table", "TABLE_LAYOUT") with { Number = 1 },
                View("Board", "BOARD_LAYOUT") with { Number = 2 },
            };

        var exception = Assert.Throws<InvalidOperationException>(
            () => ViewTabOrder.Apply(views, [1]));

        Assert.Contains("exactly the Views returned by the API", exception.Message, StringComparison.Ordinal);
    }

    // ----- verifier: Ui comparison (M6) -----

    [Fact]
    public void Verifier_reports_no_view_differences_when_ui_settings_match()
    {
        var view = View("Roadmap", "ROADMAP_LAYOUT") with { Ui = Ui("Assignees") };
        var report = ProjectVerifier.Compare(Snapshot(["Status"], view), Snapshot(["Status"], view));

        Assert.DoesNotContain(report.Differences, d => d.Category == "View");
    }

    [Fact]
    public void Verifier_warns_when_ui_settings_differ()
    {
        var source = View("Roadmap", "ROADMAP_LAYOUT") with { Ui = Ui("Assignees") };
        var target = View("Roadmap", "ROADMAP_LAYOUT") with { Ui = Ui("Status") };

        var report = ProjectVerifier.Compare(Snapshot(["Status"], source), Snapshot(["Status"], target));

        var difference = Assert.Single(report.Differences, d => d.Category == "View");
        Assert.Equal(VerifySeverity.Error, difference.Severity);
        Assert.Contains("slice by mismatch", difference.Message, StringComparison.Ordinal);
        Assert.Equal(VerifyStatus.Mismatch, report.Status);
    }

    [Theory]
    [InlineData("TABLE_LAYOUT")]
    [InlineData("BOARD_LAYOUT")]
    [InlineData("ROADMAP_LAYOUT")]
    public void Verifier_reports_field_sum_differences_for_every_layout(string layout)
    {
        var source = View("Grouped", layout) with
        {
            Ui = new ViewUiSnapshot { FieldSum = ["Count", "Fixture Number"] },
        };
        var target = source with
        {
            Ui = new ViewUiSnapshot { FieldSum = ["Fixture Number"] },
        };

        var report = ProjectVerifier.Compare(
            Snapshot(["Fixture Number"], source),
            Snapshot(["Fixture Number"], target));

        var difference = Assert.Single(report.Differences, difference =>
            difference.Category == "View"
            && difference.Message.Contains("field sum mismatch", StringComparison.Ordinal));
        Assert.Equal(VerifySeverity.Error, difference.Severity);
        Assert.Equal(VerifyStatus.Mismatch, report.Status);
    }

    [Fact]
    public void Verifier_marks_ui_not_verified_when_one_side_has_no_ui()
    {
        var source = View("Roadmap", "ROADMAP_LAYOUT") with { Ui = Ui("Assignees") };
        var target = View("Roadmap", "ROADMAP_LAYOUT");

        var report = ProjectVerifier.Compare(Snapshot(["Status"], source), Snapshot(["Status"], target));

        Assert.Equal(VerifyStatus.NotVerified, report.Status);
        Assert.Contains(report.Categories, category =>
            category.Category == "View" && category.Status == VerifyStatus.NotVerified);
    }

    // ----- helpers -----

    private static ViewUiSnapshot Ui(string sliceBy) => new()
    {
        SliceBy = sliceBy,
        Roadmap = new RoadmapSettingsSnapshot
        {
            StartField = "Fixture Date",
            TargetField = "Fixture Sprint end",
            Zoom = "Month",
            Markers = ["Fixture Sprint"],
        },
        ScrapedAt = new DateTimeOffset(2026, 7, 5, 0, 0, 0, TimeSpan.Zero),
    };

    private static SortByFieldSnapshot Sort(string field, string direction) => new() { Field = field, Direction = direction };

    private static ViewSnapshot View(
        string name,
        string layout,
        IReadOnlyList<string>? groupBy = null,
        IReadOnlyList<SortByFieldSnapshot>? sortBy = null) => new()
        {
            Number = 1,
            Name = name,
            Layout = layout,
            GroupByFields = groupBy ?? [],
            SortByFields = sortBy ?? [],
            VerticalGroupByFields = [],
            VisibleFields = [],
        };

    private static ProjectSnapshot Snapshot(IReadOnlyList<string> fields, params ViewSnapshot[] views) => new()
    {
        SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
        Project = new ProjectInfoSnapshot { Title = "t", Public = false, Closed = false },
        Fields = [.. fields.Select(f => new FieldSnapshot { Name = f, DataType = "TEXT" })],
        Views = views,
        Workflows = [],
        Items = [],
    };

    private static List<int> ApplyMoves(
        IReadOnlyList<int> current,
        IReadOnlyList<ViewUiImporter.TabMove> moves)
    {
        var result = current.ToList();
        foreach (var move in moves)
        {
            result.Remove(move.ViewNumber);
            var anchorIndex = result.IndexOf(move.AnchorViewNumber);
            result.Insert(move.PlaceBefore ? anchorIndex : anchorIndex + 1, move.ViewNumber);
        }

        return result;
    }

    private static IEnumerable<int[]> Permutations(int[] values)
    {
        if (values.Length == 1)
        {
            yield return [values[0]];
            yield break;
        }

        for (var index = 0; index < values.Length; index++)
        {
            var remaining = values.Where((_, candidate) => candidate != index).ToArray();
            foreach (var permutation in Permutations(remaining))
            {
                yield return [values[index], .. permutation];
            }
        }
    }

    private static int LongestIncreasingSubsequenceLength(int[] values)
    {
        var lengths = Enumerable.Repeat(1, values.Length).ToArray();
        for (var index = 0; index < values.Length; index++)
        {
            for (var candidate = 0; candidate < index; candidate++)
            {
                if (values[candidate] < values[index])
                {
                    lengths[index] = Math.Max(lengths[index], lengths[candidate] + 1);
                }
            }
        }

        return lengths.Max();
    }
}
