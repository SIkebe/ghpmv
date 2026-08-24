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

    [Theory]
    [InlineData("", null)]
    [InlineData("Select a default value", null)]
    [InlineData("Default value: Beta", "Beta")]
    [InlineData("日本語 🚀", "日本語 🚀")]
    public void NormalizeSingleSelectValue_reads_cleared_and_named_values(string value, string? expected)
        => Assert.Equal(
            expected,
            FieldDefaultUiExporter.NormalizeSingleSelectValue(
                value,
                new HashSet<string>(["Beta", "日本語 🚀"], StringComparer.Ordinal)));

    [Theory]
    [InlineData("None")]
    [InlineData("No default value")]
    [InlineData("Default value: Beta")]
    public void NormalizeSingleSelectValue_preserves_option_names_that_look_like_ui_labels(string optionName)
        => Assert.Equal(
            optionName,
            FieldDefaultUiExporter.NormalizeSingleSelectValue(
                optionName,
                new HashSet<string>([optionName], StringComparer.Ordinal)));

    [Fact]
    public void NormalizeSingleSelectValue_resolves_a_prefixed_arbitrary_option_name()
        => Assert.Equal(
            "Default value: Beta",
            FieldDefaultUiExporter.NormalizeSingleSelectValue(
                "Default value: Default value: Beta",
                new HashSet<string>(["Default value: Beta"], StringComparer.Ordinal)));

    [Fact]
    public void NormalizeSingleSelectValue_rejects_unknown_control_text()
    {
        var exception = Assert.Throws<FormatException>(() =>
            FieldDefaultUiExporter.NormalizeSingleSelectValue(
                "Loading options...",
                new HashSet<string>(["Beta"], StringComparer.Ordinal)));

        Assert.Contains("did not match", exception.Message, StringComparison.Ordinal);
    }

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
}
