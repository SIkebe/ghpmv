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

    [Fact]
    public void FixtureUiSnapshotFactory_creates_importable_standard_views_and_workflows()
    {
        var snapshot = FixtureUiSnapshotFactory.Create("fixture-repo");

        Assert.Equal(["View 1", "Fixture Board", "Fixture Roadmap"], snapshot.Views.Select(v => v.Name));
        Assert.Equal(
            ["Fixture Roadmap", "View 1", "Fixture Board"],
            snapshot.Views.OrderBy(view => view.TabPosition).Select(view => view.Name));
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
