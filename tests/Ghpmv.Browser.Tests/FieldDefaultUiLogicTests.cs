using Ghpmv.Core.Browser;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Browser.Tests;

public class FieldDefaultUiLogicTests
{
    [Theory]
    [InlineData("", null)]
    [InlineData("0", 0d)]
    [InlineData("-42.5", -42.5)]
    public void ParseNumber_preserves_empty_zero_and_negative_values(string value, double? expected)
        => Assert.Equal(expected, FieldDefaultUiExporter.ParseNumber(value));

    [Fact]
    public void Validate_accepts_single_select_default_by_option_name_regardless_of_id()
    {
        var field = new FieldSnapshot
        {
            Name = "Fixture Select",
            DataType = "SINGLE_SELECT",
            Options =
            [
                new SingleSelectOptionSnapshot { Id = "source-beta", Name = "Beta", Color = "BLUE" },
            ],
            DefaultValue = new FieldDefaultValueSnapshot { SingleSelectOptionName = "Beta" },
        };

        Assert.Null(FieldDefaultUiImporter.Validate(field));
        Assert.Null(FieldDefaultUiImporter.Validate(field with
        {
            Options =
            [
                new SingleSelectOptionSnapshot { Id = "target-renamed-id", Name = "Beta", Color = "BLUE" },
            ],
        }));
    }

    [Fact]
    public void Validate_rejects_missing_single_select_option()
    {
        var warning = FieldDefaultUiImporter.Validate(new FieldSnapshot
        {
            Name = "Fixture Select",
            DataType = "SINGLE_SELECT",
            Options = [],
            DefaultValue = new FieldDefaultValueSnapshot { SingleSelectOptionName = "Missing" },
        });

        Assert.Contains("does not exist", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_accepts_captured_clear_for_each_supported_type()
    {
        foreach (var dataType in new[] { "TEXT", "NUMBER", "SINGLE_SELECT" })
        {
            Assert.Null(FieldDefaultUiImporter.Validate(new FieldSnapshot
            {
                Name = dataType,
                DataType = dataType,
                DefaultValue = new FieldDefaultValueSnapshot(),
            }));
        }
    }

    [Fact]
    public void Validate_rejects_a_member_for_the_wrong_field_type()
    {
        var warning = FieldDefaultUiImporter.Validate(new FieldSnapshot
        {
            Name = "Fixture Number",
            DataType = "NUMBER",
            DefaultValue = new FieldDefaultValueSnapshot { Text = "wrong" },
        });

        Assert.Contains("does not match", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Built_in_status_is_not_treated_as_a_custom_single_select_default()
        => Assert.False(FieldDefaultUiExporter.Supports(new FieldSnapshot
        {
            Name = "Status",
            DataType = "SINGLE_SELECT",
        }));

    [Fact]
    public void Field_defaults_are_deferred_when_any_source_item_was_skipped()
    {
        var snapshot = FixtureUiSnapshotFactory.Create();

        Assert.True(FieldDefaultUiImporter.ShouldDefer(snapshot, skippedItemCount: 1));
        Assert.False(FieldDefaultUiImporter.ShouldDefer(snapshot, skippedItemCount: 0));
        Assert.False(FieldDefaultUiImporter.ShouldDefer(
            snapshot with
            {
                Fields = snapshot.Fields.Select(field => field with { DefaultValue = null }).ToList(),
            },
            skippedItemCount: 1));
    }

    [Theory]
    [InlineData(4, 0, "field-defaults: imported=4 warnings=0")]
    [InlineData(0, 1, "field-defaults: imported=0 warnings=1")]
    public void FormatSummary_preserves_machine_readable_counts(
        int importedCount,
        int warningCount,
        string expected)
        => Assert.Equal(
            expected,
            FieldDefaultUiImporter.FormatSummary(importedCount, warningCount));

    [Fact]
    public void Cleared_default_snapshot_only_neutralizes_captured_defaults()
    {
        var snapshot = FixtureUiSnapshotFactory.Create();

        var cleared = FieldDefaultUiImporter.CreateClearedDefaultsSnapshot(snapshot);

        Assert.All(
            cleared.Fields.Where(field => field.DefaultValue is not null),
            field => Assert.Equal(new FieldDefaultValueSnapshot(), field.DefaultValue));
        Assert.Equal(
            snapshot.Fields.Where(field => field.DefaultValue is null),
            cleared.Fields.Where(field => field.DefaultValue is null));
    }

    [Fact]
    public async Task Import_sequence_neutralizes_before_items_and_restores_only_after_skip_free_resume()
    {
        var snapshot = FixtureUiSnapshotFactory.Create();
        var events = new List<string>();

        var skipped = await FieldDefaultUiImporter.RunImportSequenceAsync(
            snapshot,
            (phase, desiredSnapshot, _) =>
            {
                events.Add(phase.ToString());
                if (phase == FieldDefaultUiImporter.FieldDefaultImportPhase.NeutralizeBeforeItems)
                {
                    Assert.All(
                        desiredSnapshot.Fields.Where(field => field.DefaultValue is not null),
                        field => Assert.Equal(new FieldDefaultValueSnapshot(), field.DefaultValue));
                }
                return Task.FromResult(new FieldDefaultUiImporter.FieldDefaultImportStepResult
                {
                    AppliedCount = 0,
                    Warnings = [],
                });
            },
            _ =>
            {
                events.Add("ImportItemsSkipped");
                return Task.FromResult(new SequenceItemResult(1));
            },
            result => result.Skipped,
            TestContext.Current.CancellationToken);

        var resumed = await FieldDefaultUiImporter.RunImportSequenceAsync(
            snapshot,
            (phase, _, _) =>
            {
                events.Add(phase.ToString());
                return Task.FromResult(new FieldDefaultUiImporter.FieldDefaultImportStepResult
                {
                    AppliedCount = phase == FieldDefaultUiImporter.FieldDefaultImportPhase.ApplyAfterItems ? 4 : 0,
                    Warnings = [],
                });
            },
            _ =>
            {
                events.Add("ImportItemsComplete");
                return Task.FromResult(new SequenceItemResult(0));
            },
            result => result.Skipped,
            TestContext.Current.CancellationToken);

        Assert.True(skipped.DefaultsDeferred);
        Assert.False(resumed.DefaultsDeferred);
        Assert.Equal("field-defaults: imported=0 warnings=1", skipped.Summary);
        Assert.Equal(
            "field defaults were deferred because 1 source item(s) were skipped; fix mappings and rerun import before defaults are applied",
            Assert.Single(skipped.Warnings));
        Assert.Equal("field-defaults: imported=4 warnings=0", resumed.Summary);
        Assert.Empty(resumed.Warnings);
        Assert.Equal(
            [
                "NeutralizeBeforeItems",
                "ImportItemsSkipped",
                "NeutralizeBeforeItems",
                "ImportItemsComplete",
                "ApplyAfterItems",
            ],
            events);
    }

    [Theory]
    [InlineData("TEXT")]
    [InlineData("NUMBER")]
    [InlineData("SINGLE_SELECT")]
    public void ValuesEqual_treats_cleared_values_as_equal(string dataType)
        => Assert.True(FieldDefaultUiImporter.ValuesEqual(
            dataType,
            new FieldDefaultValueSnapshot(),
            new FieldDefaultValueSnapshot()));

    [Fact]
    public void ValidateDraftDefaults_accepts_all_standard_typed_values()
    {
        var fields = FixtureUiSnapshotFactory.Create().Fields;
        var draft = new ItemSnapshot
        {
            Type = "DRAFT_ISSUE",
            Position = 0,
            IsArchived = false,
            Draft = new DraftIssueSnapshot { Title = "check", Assignees = [] },
            FieldValues =
            [
                new FieldValueSnapshot { FieldName = "Fixture Text", Text = "既定値 🌏" },
                new FieldValueSnapshot { FieldName = "Fixture Number", Number = -7 },
                new FieldValueSnapshot { FieldName = "Fixture Number 2", Number = 0 },
                new FieldValueSnapshot { FieldName = "Fixture Select", SingleSelectOptionName = "Beta" },
            ],
        };

        FieldDefaultFixtureObserver.ValidateDraftDefaults(fields, draft);
    }

    [Fact]
    public void ValidateDraftDefaults_rejects_a_missing_default()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            FieldDefaultFixtureObserver.ValidateDraftDefaults(
                FixtureUiSnapshotFactory.Create().Fields,
                new ItemSnapshot
                {
                    Type = "DRAFT_ISSUE",
                    Position = 0,
                    IsArchived = false,
                    Draft = new DraftIssueSnapshot { Title = "check", Assignees = [] },
                    FieldValues = [],
                }));

        Assert.Contains("Fixture Text", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PollForMatchesAsync_retries_until_an_ambiguous_draft_is_visible()
    {
        var attempts = 0;

        var matches = await FieldDefaultFixtureObserver.PollForMatchesAsync(
            _ => Task.FromResult<IReadOnlyList<string>>(
                ++attempts < 3 ? [] : ["PVTI_test"]),
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, attempts);
        Assert.Equal(["PVTI_test"], matches);
    }

    [Fact]
    public void FormatDraftInventory_includes_every_duplicate_item_id()
        => Assert.Equal(
            "inventory title 'duplicate' and matching item IDs [PVTI_first,PVTI_second] before cleanup",
            FieldDefaultFixtureObserver.FormatDraftInventory(
                "duplicate",
                ["PVTI_first", "PVTI_second"]));

    private sealed record SequenceItemResult(int Skipped);
}
