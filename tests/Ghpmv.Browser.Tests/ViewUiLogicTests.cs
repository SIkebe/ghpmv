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
    [InlineData("Sort by: Fixture Number (ascending)", "Fixture Number", "ASC", true)]
    [InlineData("Sort by: Fixture Number, ascending", "Fixture Number", "ASC", true)]
    [InlineData("Sort by:\nFixture Number\nDescending", "Fixture Number", "DESC", true)]
    [InlineData("Sort by: Fixture Number 2, ascending", "Fixture Number", "ASC", false)]
    [InlineData("Sort by: Fixture Number, descending", "Fixture Number", "ASC", false)]
    [InlineData("Sort by: none", "Fixture Number", "ASC", false)]
    public void SortMenuMatches_requires_the_exact_field_and_direction(
        string menuText,
        string field,
        string direction,
        bool expected)
        => Assert.Equal(
            expected,
            ViewUiImporter.SortMenuMatches(
                menuText,
                new SortByFieldSnapshot { Field = field, Direction = direction }));

    [Theory]
    [InlineData("  Fixture   Sprint\nend ", "Fixture Sprint end")]
    [InlineData("   ", null)]
    public void NormalizeUiText_collapses_whitespace(string text, string? expected)
        => Assert.Equal(expected, ViewUiExporter.NormalizeUiText(text));

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("1", 1)]
    [InlineData("25", 25)]
    public void Parse_Board_column_limit_distinguishes_unlimited_and_numeric_values(
        string? value,
        int? expected)
        => Assert.Equal(expected, BoardColumnLimitUi.ParseLimit(value));

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("many")]
    public void Parse_Board_column_limit_rejects_invalid_values(string value)
        => Assert.Throws<InvalidOperationException>(() => BoardColumnLimitUi.ParseLimit(value));

    [Fact]
    public void Board_limit_capture_is_skipped_without_discarding_other_UI_for_unsupported_columns()
    {
        var view = View("Board", "BOARD_LAYOUT") with
        {
            VerticalGroupByFields = ["Assignees"],
            Ui = new ViewUiSnapshot { FieldSum = ["Count"] },
        };
        var fields = new[]
        {
            new FieldSnapshot { Name = "Assignees", DataType = "ASSIGNEES" },
        };

        Assert.False(BoardColumnLimitUi.CanCapture(view, fields, out var reason));
        Assert.Contains("unsupported type 'ASSIGNEES'", reason, StringComparison.Ordinal);
        Assert.Equal(["Count"], view.Ui.FieldSum);
    }

    [Theory]
    [InlineData("2 / 1", 2, 1)]
    [InlineData(" 12/3 ", 12, 3)]
    public void Parse_Board_column_counter_reads_count_and_limit(
        string text,
        int expectedCount,
        int expectedLimit)
        => Assert.Equal(
            (expectedCount, expectedLimit),
            BoardColumnLimitObserver.ParseCounter(text));

    [Fact]
    public void Board_column_observer_compares_logical_identities_without_node_ids()
    {
        var expected = View("Board", "BOARD_LAYOUT") with
        {
            Ui = new ViewUiSnapshot
            {
                BoardColumnLimits =
                [
                    BoardLimit("Fixture Sprint", iteration: "Sprint 1", limit: 2),
                ],
            },
        };

        BoardColumnLimitObserver.ValidateLimits(
            expected,
            [BoardLimit("Fixture Sprint", iteration: "Sprint 1", limit: 2)]);
        Assert.Throws<InvalidOperationException>(() =>
            BoardColumnLimitObserver.ValidateLimits(
                expected,
                [BoardLimit("Fixture Sprint", iteration: "Sprint 1", limit: 3)]));
    }

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
    public void CollectPreflightWarnings_accepts_logical_single_select_and_iteration_columns()
    {
        var snapshot = Snapshot(
            fields: ["Fixture Select", "Fixture Sprint"],
            View("Select Board", "BOARD_LAYOUT") with
            {
                VerticalGroupByFields = ["Fixture Select"],
                Ui = new ViewUiSnapshot
                {
                    BoardColumnLimits =
                    [
                        new BoardColumnLimitSnapshot
                        {
                            FieldName = "Fixture Select",
                            SingleSelectOptionName = "Alpha",
                            Limit = 1,
                        },
                    ],
                },
            },
            View("Iteration Board", "BOARD_LAYOUT") with
            {
                VerticalGroupByFields = ["Fixture Sprint"],
                Ui = new ViewUiSnapshot
                {
                    BoardColumnLimits =
                    [
                        new BoardColumnLimitSnapshot
                        {
                            FieldName = "Fixture Sprint",
                            IterationTitle = "Sprint 1",
                            Limit = 2,
                        },
                    ],
                },
            }) with
        {
            Fields =
            [
                new FieldSnapshot
                {
                    Name = "Fixture Select",
                    DataType = "SINGLE_SELECT",
                    Options =
                    [
                        new SingleSelectOptionSnapshot
                        {
                            Id = "source-option-id",
                            Name = "Alpha",
                            Color = "RED",
                        },
                    ],
                },
                new FieldSnapshot
                {
                    Name = "Fixture Sprint",
                    DataType = "ITERATION",
                    IterationConfiguration = new IterationConfigurationSnapshot
                    {
                        Duration = 14,
                        StartDay = 1,
                        Iterations =
                        [
                            new IterationSnapshot
                            {
                                Id = "source-iteration-id",
                                Title = "Sprint 1",
                                StartDate = "2026-08-24",
                                Duration = 14,
                            },
                        ],
                        CompletedIterations = [],
                    },
                },
            ],
        };

        Assert.Empty(ViewUiImporter.CollectPreflightWarnings(snapshot));
    }

    [Fact]
    public void CollectPreflightWarnings_reports_malformed_and_missing_Board_columns()
    {
        var snapshot = Snapshot(
            fields: ["Fixture Select"],
            View("Board", "BOARD_LAYOUT") with
            {
                VerticalGroupByFields = ["Fixture Select"],
                Ui = new ViewUiSnapshot
                {
                    BoardColumnLimits =
                    [
                        new BoardColumnLimitSnapshot
                        {
                            FieldName = "Fixture Select",
                            SingleSelectOptionName = "Missing",
                            Limit = 0,
                        },
                        new BoardColumnLimitSnapshot
                        {
                            FieldName = "Wrong Field",
                            IterationTitle = "Sprint 1",
                            Limit = 2,
                        },
                        new BoardColumnLimitSnapshot
                        {
                            FieldName = "Fixture Select",
                            SingleSelectOptionName = "Alpha",
                            IterationTitle = "Sprint 1",
                            Limit = 3,
                        },
                    ],
                },
            }) with
        {
            Fields =
            [
                new FieldSnapshot
                {
                    Name = "Fixture Select",
                    DataType = "SINGLE_SELECT",
                    Options =
                    [
                        new SingleSelectOptionSnapshot { Id = "alpha", Name = "Alpha", Color = "RED" },
                    ],
                },
            ],
        };

        var warnings = ViewUiImporter.CollectPreflightWarnings(snapshot);

        Assert.Contains(warnings, warning => warning.Contains("must be positive", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Contains("option 'Missing' does not exist", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Contains("does not use column-by field", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Contains("exactly one Single-select option or Iteration", StringComparison.Ordinal));
        Assert.Equal(4, warnings.Count);
    }

    [Fact]
    public void Board_limit_reconciliation_skips_all_writes_when_a_target_column_is_missing()
    {
        var field = new FieldSnapshot
        {
            Name = "Fixture Select",
            DataType = "SINGLE_SELECT",
            Options =
            [
                new SingleSelectOptionSnapshot { Id = "alpha", Name = "Alpha", Color = "RED" },
                new SingleSelectOptionSnapshot { Id = "beta", Name = "Beta", Color = "BLUE" },
            ],
        };
        var view = View("Board", "BOARD_LAYOUT") with
        {
            VerticalGroupByFields = [field.Name],
        };
        var desiredLimits = new[]
        {
            BoardLimit(field.Name, option: "Alpha", limit: 1),
        };

        var plan = BoardColumnLimitUi.BuildReconciliationPlan(
            view,
            field,
            desiredLimits,
            ["Beta"]);

        Assert.Contains(plan.Warnings, warning =>
            warning.Contains("Single-select column 'Fixture Select' / 'Alpha'", StringComparison.Ordinal)
            && warning.Contains("was not found", StringComparison.Ordinal));
        Assert.Empty(plan.Targets);
    }

    [Fact]
    public void Board_limit_reconciliation_skips_all_writes_for_malformed_identities()
    {
        var field = new FieldSnapshot
        {
            Name = "Fixture Select",
            DataType = "SINGLE_SELECT",
            Options =
            [
                new SingleSelectOptionSnapshot { Id = "alpha", Name = "Alpha", Color = "RED" },
            ],
        };
        var view = View("Board", "BOARD_LAYOUT") with
        {
            VerticalGroupByFields = [field.Name],
        };
        var desiredLimits = new[]
        {
            BoardLimit("Wrong Field", option: "Alpha", limit: 1),
            BoardLimit(field.Name, iteration: "Alpha", limit: 2),
            new BoardColumnLimitSnapshot
            {
                FieldName = field.Name,
                SingleSelectOptionName = "Alpha",
                IterationTitle = "Alpha",
                Limit = 3,
            },
            BoardLimit(field.Name, option: "Alpha", limit: 0),
        };

        var plan = BoardColumnLimitUi.BuildReconciliationPlan(
            view,
            field,
            desiredLimits,
            ["Alpha"]);

        Assert.Equal(4, plan.Warnings.Count);
        Assert.Empty(plan.Targets);
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
    public void Field_sum_persistence_match_is_order_independent()
        => Assert.True(ViewUiImporter.FieldSumMatches(
            ["Count", "Fixture Number"],
            ["Fixture Number", "Count"]));

    [Fact]
    public void Field_sum_persistence_match_rejects_unavailable_or_incomplete_state()
    {
        Assert.False(ViewUiImporter.FieldSumMatches(["Count"], null));
        Assert.False(ViewUiImporter.FieldSumMatches([], null));
        Assert.False(ViewUiImporter.FieldSumMatches(
            ["Count", "Fixture Number"],
            ["Count"]));
    }

    [Fact]
    public void Checkbox_selection_match_supports_roadmap_markers()
    {
        Assert.True(ViewUiImporter.CheckboxSelectionMatches(
            ["Fixture Date", "Fixture Sprint"],
            ["Fixture Sprint", "Fixture Date"]));
        Assert.False(ViewUiImporter.CheckboxSelectionMatches(["Fixture Date"], []));
        Assert.False(ViewUiImporter.CheckboxSelectionMatches([], null));
    }

    [Fact]
    public void Rendered_field_sum_observation_accepts_count_and_numeric_labels()
    {
        var view = FixtureUiSnapshotFactory.Create().Views.Single(candidate => candidate.Name == "View 1");

        FieldSumRenderingObserver.ValidateObservation(
            view,
            ["Todo 2 (2) Fixture Number: 3.14 Fixture Number 2: 0"],
            ["Fixture Number: 3.14", "Fixture Number 2: 0"]);
    }

    [Fact]
    public void Rendered_field_sum_observation_rejects_missing_numeric_label()
    {
        var view = FixtureUiSnapshotFactory.Create().Views.Single(candidate => candidate.Name == "Fixture Roadmap");

        var exception = Assert.Throws<InvalidOperationException>(
            () => FieldSumRenderingObserver.ValidateObservation(
                view,
                ["Todo 2"],
                []));

        Assert.Contains("Fixture Number 2", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public void Rendered_roadmap_observation_requires_configured_truncation_and_date_visibility(
        bool titleTruncated,
        bool datesRendered,
        bool expected)
    {
        var view = FixtureUiSnapshotFactory.Create().Views.Single(candidate => candidate.Name == "Fixture Roadmap");

        if (expected)
        {
            FieldSumRenderingObserver.ValidateRoadmapDisplayObservation(view, titleTruncated, datesRendered);
            return;
        }

        Assert.Throws<InvalidOperationException>(
            () => FieldSumRenderingObserver.ValidateRoadmapDisplayObservation(view, titleTruncated, datesRendered));
    }

    [Fact]
    public void Rendered_roadmap_observation_rejects_dates_when_the_view_hides_them()
    {
        var view = FixtureUiSnapshotFactory.Create().Views.Single(
            candidate => candidate.Name == "Fixture Roadmap Dates Hidden");

        FieldSumRenderingObserver.ValidateRoadmapDisplayObservation(
            view,
            titleTruncated: true,
            datesRendered: false);
        Assert.Throws<InvalidOperationException>(
            () => FieldSumRenderingObserver.ValidateRoadmapDisplayObservation(
                view,
                titleTruncated: true,
                datesRendered: true));
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

    [Fact]
    public void Persistence_check_reports_changed_cleared_and_unexpected_Board_limits()
    {
        var view = View("Board", "BOARD_LAYOUT") with
        {
            VerticalGroupByFields = ["Fixture Select"],
            Ui = new ViewUiSnapshot
            {
                BoardColumnLimits =
                [
                    BoardLimit("Fixture Select", option: "Alpha", limit: 1),
                    BoardLimit("Fixture Select", option: "Beta", limit: 2),
                ],
            },
        };
        var persisted = new ViewUiImporter.PersistedViewSettings(
            GroupBy: null,
            ColumnBy: "Fixture Select",
            SliceBy: null,
            FieldSumAvailable: true,
            FieldSum: [],
            BoardColumnLimits:
            [
                BoardLimit("Fixture Select", option: "Alpha", limit: 3),
                BoardLimit("Fixture Select", option: "Gamma", limit: 4),
            ]);

        var differences = ViewUiImporter.CollectPersistenceDifferences(view, persisted);

        Assert.Contains(differences, difference =>
            difference.Contains("'Alpha' expected '1', actual '3'", StringComparison.Ordinal));
        Assert.Contains(differences, difference =>
            difference.Contains("'Beta' expected '2', actual 'unlimited'", StringComparison.Ordinal));
        Assert.Contains(differences, difference =>
            difference.Contains("'Gamma' expected 'unlimited', actual '4'", StringComparison.Ordinal));
        Assert.Equal(3, differences.Count);
    }

    [Fact]
    public void Persistence_check_reports_roadmap_display_option_loss_independently()
    {
        var view = View("Roadmap", "ROADMAP_LAYOUT") with
        {
            Ui = new ViewUiSnapshot
            {
                Roadmap = new RoadmapSettingsSnapshot
                {
                    TruncateTitles = true,
                    ShowDateFields = false,
                },
            },
        };
        var persisted = new ViewUiImporter.PersistedViewSettings(
            GroupBy: null,
            ColumnBy: null,
            SliceBy: null,
            FieldSumAvailable: false,
            FieldSum: [],
            TruncateTitles: false,
            ShowDateFields: null);

        var differences = ViewUiImporter.CollectPersistenceDifferences(view, persisted);

        Assert.Contains("truncate-titles expected 'true', actual 'false'", differences);
        Assert.Contains("show-date-fields expected 'false', actual 'unavailable'", differences);
        Assert.Equal(2, differences.Count);
    }

    [Fact]
    public void Persistence_check_skips_only_uncaptured_roadmap_display_options()
    {
        var view = View("Roadmap", "ROADMAP_LAYOUT") with
        {
            Ui = new ViewUiSnapshot
            {
                Roadmap = new RoadmapSettingsSnapshot
                {
                    TruncateTitles = null,
                    ShowDateFields = null,
                },
            },
        };
        var persisted = new ViewUiImporter.PersistedViewSettings(
            GroupBy: null,
            ColumnBy: null,
            SliceBy: null,
            FieldSumAvailable: false,
            FieldSum: [],
            TruncateTitles: false,
            ShowDateFields: true);

        Assert.Empty(ViewUiImporter.CollectPersistenceDifferences(view, persisted));
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
            ["View 1", "Fixture Board", "Fixture Roadmap", "Fixture Empty Sums", "Fixture Roadmap Dates Hidden", "Fixture Iteration Board"],
            snapshot.Views.Select(v => v.Name));
        Assert.Equal(
            ["Fixture Roadmap", "View 1", "Fixture Board", "Fixture Iteration Board", "Fixture Empty Sums", "Fixture Roadmap Dates Hidden"],
            snapshot.Views.OrderBy(view => view.TabPosition).Select(view => view.Name));
        var roadmap = Assert.Single(snapshot.Views, view => view.Name == "Fixture Roadmap").Ui!.Roadmap!;
        Assert.True(roadmap.TruncateTitles);
        Assert.False(roadmap.ShowDateFields);
        var datesHidden = Assert.Single(snapshot.Views, view => view.Name == "Fixture Roadmap Dates Hidden").Ui!.Roadmap!;
        Assert.True(datesHidden.TruncateTitles);
        Assert.False(datesHidden.ShowDateFields);
        Assert.Equal(
            ["Count", "Fixture Number", "Fixture Number 2"],
            snapshot.Views.Single(view => view.Name == "View 1").Ui!.FieldSum);
        Assert.Equal(
            ["Fixture Number 2"],
            snapshot.Views.Single(view => view.Name == "Fixture Roadmap").Ui!.FieldSum);
        Assert.Empty(snapshot.Views.Single(view => view.Name == "Fixture Empty Sums").Ui!.FieldSum!);
        Assert.Equal(
            [("Alpha", 1), ("Beta", 2)],
            snapshot.Views.Single(view => view.Name == "Fixture Board").Ui!.BoardColumnLimits!
                .Select(limit => (limit.SingleSelectOptionName, limit.Limit)));
        Assert.Equal(
            [("Sprint 0", 1), ("Sprint 1", 3)],
            snapshot.Views.Single(view => view.Name == "Fixture Iteration Board").Ui!.BoardColumnLimits!
                .Select(limit => (limit.IterationTitle, limit.Limit)));
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
    public void FixtureUiSnapshotFactory_creates_project_shared_roadmap_display_drift()
    {
        var standard = Assert.Single(
            FixtureUiSnapshotFactory.Create().Views,
            view => view.Name == "Fixture Roadmap").Ui!.Roadmap!;
        var drift = Assert.Single(
            FixtureUiSnapshotFactory.CreateRoadmapDisplayDrift().Views,
            view => view.Name == "Fixture Roadmap").Ui!.Roadmap!;
        var dateDrift = Assert.Single(
            FixtureUiSnapshotFactory.CreateRoadmapDateDisplayDrift().Views,
            view => view.Name == "Fixture Roadmap").Ui!.Roadmap!;

        Assert.True(standard.TruncateTitles);
        Assert.False(standard.ShowDateFields);
        Assert.False(drift.TruncateTitles);
        Assert.False(drift.ShowDateFields);
        Assert.True(dateDrift.TruncateTitles);
        Assert.True(dateDrift.ShowDateFields);
    }

    [Fact]
    public void Shared_roadmap_display_settings_reject_conflicting_view_values()
    {
        var snapshot = FixtureUiSnapshotFactory.Create();
        ViewUiImporter.ValidateSharedRoadmapDisplaySettings(snapshot.Views);
        var conflicting = snapshot.Views.Select(view =>
            view.Name == "Fixture Roadmap Dates Hidden"
                ? view with
                {
                    Ui = view.Ui! with
                    {
                        Roadmap = view.Ui.Roadmap! with { ShowDateFields = true },
                    },
                }
                : view).ToList();

        Assert.Throws<InvalidOperationException>(
            () => ViewUiImporter.ValidateSharedRoadmapDisplaySettings(conflicting));
    }

    [Fact]
    public void FixtureUiSnapshotFactory_combined_drift_changes_field_sum_and_Board_limits()
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

            if (view.Name == "Fixture Board")
            {
                var limit = Assert.Single(actual.Ui.BoardColumnLimits!);
                Assert.Equal("Alpha", limit.SingleSelectOptionName);
                Assert.Equal(5, limit.Limit);
            }
            else
            {
                Assert.Equal(view.Ui.BoardColumnLimits, actual.Ui.BoardColumnLimits);
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
    public void FixtureUiSnapshotFactory_creates_typed_defaults_and_all_type_drift()
    {
        var expected = FixtureUiSnapshotFactory.Create("fixture-repo");
        var drifted = FixtureUiSnapshotFactory.CreateFieldDefaultDrift("fixture-repo");

        Assert.Equal("既定値 🌏", expected.Fields.Single(field => field.Name == "Fixture Text").DefaultValue!.Text);
        Assert.Equal(-7, expected.Fields.Single(field => field.Name == "Fixture Number").DefaultValue!.Number);
        Assert.Equal(0, expected.Fields.Single(field => field.Name == "Fixture Number 2").DefaultValue!.Number);
        Assert.Equal(
            "Beta",
            expected.Fields.Single(field => field.Name == "Fixture Select").DefaultValue!.SingleSelectOptionName);

        Assert.Equal("drifted text", drifted.Fields.Single(field => field.Name == "Fixture Text").DefaultValue!.Text);
        Assert.Null(drifted.Fields.Single(field => field.Name == "Fixture Number").DefaultValue!.Number);
        Assert.Equal(99, drifted.Fields.Single(field => field.Name == "Fixture Number 2").DefaultValue!.Number);
        Assert.Equal(
            "Gamma",
            drifted.Fields.Single(field => field.Name == "Fixture Select").DefaultValue!.SingleSelectOptionName);
    }

    [Fact]
    public void Tab_move_plan_is_empty_when_order_already_matches()
        => Assert.Empty(ViewUiImporter.BuildTabMovePlan([1, 2, 3], [1, 2, 3]));

    [Fact]
    public void Sort_field_visibility_is_restored_only_when_import_temporarily_showed_a_hidden_field()
    {
        var view = FixtureUiSnapshotFactory.Create("fixture-repo").Views.Single(view => view.Name == "View 1");

        Assert.True(ViewUiImporter.ShouldRestoreSortFieldVisibility(view, "Fixture Number", true));
        Assert.False(ViewUiImporter.ShouldRestoreSortFieldVisibility(view, "Fixture Number", false));
        Assert.False(ViewUiImporter.ShouldRestoreSortFieldVisibility(view, "Title", true));
    }

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
    public async Task Roadmap_recoverable_failure_saves_partial_browser_storage_state()
    {
        var warnings = new List<string>();
        var partialWriteApplied = false;
        var savedPartialWrite = false;

        await ViewUiImporter.ApplyRoadmapDisplayWriteRecoverablyAsync(
            () =>
            {
                partialWriteApplied = true;
                throw new InvalidOperationException("forced read-back failure");
            },
            () =>
            {
                savedPartialWrite = partialWriteApplied;
                return Task.CompletedTask;
            },
            warnings,
            "Fixture Roadmap");

        Assert.True(savedPartialWrite);
        var warning = Assert.Single(warnings);
        Assert.Contains("Fixture Roadmap", warning, StringComparison.Ordinal);
        Assert.Contains("forced read-back failure", warning, StringComparison.Ordinal);
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

    [Theory]
    [InlineData(true, true, false, true, "truncate titles mismatch")]
    [InlineData(false, true, false, false, "show date fields mismatch")]
    public void Verifier_reports_each_roadmap_display_option_difference(
        bool sourceTruncateTitles,
        bool sourceShowDateFields,
        bool targetTruncateTitles,
        bool targetShowDateFields,
        string expectedMessage)
    {
        var source = View("Roadmap", "ROADMAP_LAYOUT") with
        {
            Ui = new ViewUiSnapshot
            {
                Roadmap = new RoadmapSettingsSnapshot
                {
                    TruncateTitles = sourceTruncateTitles,
                    ShowDateFields = sourceShowDateFields,
                },
            },
        };
        var target = source with
        {
            Ui = new ViewUiSnapshot
            {
                Roadmap = new RoadmapSettingsSnapshot
                {
                    TruncateTitles = targetTruncateTitles,
                    ShowDateFields = targetShowDateFields,
                },
            },
        };

        var difference = Assert.Single(
            ProjectVerifier.Compare(Snapshot([], source), Snapshot([], target)).Differences,
            difference => difference.Category == "View");

        Assert.Contains(expectedMessage, difference.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verifier_skips_only_uncaptured_roadmap_display_options()
    {
        var source = View("Roadmap", "ROADMAP_LAYOUT") with
        {
            Ui = new ViewUiSnapshot { Roadmap = new RoadmapSettingsSnapshot() },
        };
        var target = source with
        {
            Ui = new ViewUiSnapshot
            {
                Roadmap = new RoadmapSettingsSnapshot
                {
                    TruncateTitles = true,
                    ShowDateFields = false,
                },
            },
        };

        var report = ProjectVerifier.Compare(Snapshot([], source), Snapshot([], target));

        Assert.DoesNotContain(report.Differences, difference => difference.Category == "View");
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
            TruncateTitles = true,
            ShowDateFields = false,
        },
        ScrapedAt = new DateTimeOffset(2026, 7, 5, 0, 0, 0, TimeSpan.Zero),
    };

    private static BoardColumnLimitSnapshot BoardLimit(
        string fieldName,
        int limit,
        string? option = null,
        string? iteration = null)
        => new()
        {
            FieldName = fieldName,
            SingleSelectOptionName = option,
            IterationTitle = iteration,
            Limit = limit,
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
        StatusUpdates = [],
        LinkedRepositories = [],
        LinkedTeams = [],
        Project = new ProjectInfoSnapshot { Title = "t", Public = false, Closed = false, Template = false },
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
