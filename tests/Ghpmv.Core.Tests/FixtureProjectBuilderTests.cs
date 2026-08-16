using System.Globalization;
using Ghpmv.Core.Fixtures;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Tests;

public class FixtureProjectBuilderTests
{
    [Fact]
    public void Demo_fixture_exercises_every_snapshot_field_pattern()
    {
        var snapshot = FixtureProjectBuilder.CreateSnapshot(
            "Fixture",
            "example/fixture",
            "octocat",
            pullRequestNumber: 2);
        var values = snapshot.Items.SelectMany(item => item.FieldValues).ToList();

        foreach (var property in typeof(FieldValueSnapshot).GetProperties()
                     .Where(property => property.Name != nameof(FieldValueSnapshot.FieldName)))
        {
            Assert.Contains(values, value => property.GetValue(value) is not null);
        }

        foreach (var property in typeof(FieldSnapshot).GetProperties()
                     .Where(property => property.Name is not nameof(FieldSnapshot.Name) and not nameof(FieldSnapshot.DataType)))
        {
            Assert.Contains(snapshot.Fields, field => property.GetValue(field) is not null);
        }
    }

    [Fact]
    public void Demo_fixture_puts_multi_select_values_on_a_real_issue()
    {
        var snapshot = FixtureProjectBuilder.CreateSnapshot(
            "Fixture",
            "example/fixture",
            "octocat",
            pullRequestNumber: 2);

        var field = Assert.Single(snapshot.Fields, field => field.Name == "Fixture Teams");
        Assert.Equal("MULTI_SELECT", field.DataType);
        Assert.NotNull(field.IssueField);
        Assert.Equal("ALL", field.IssueField.Visibility);
        Assert.Equal(["Platform", "SDK", "Docs"], field.Options!.Select(option => option.Name));

        var issue = Assert.Single(snapshot.Items, item => item.Type == "ISSUE");
        var value = Assert.Single(issue.FieldValues, value => value.FieldName == field.Name);
        Assert.Equal(["Platform", "SDK"], value.MultiSelectOptionNames);
    }

    [Fact]
    public void Demo_fixture_exercises_ordinary_project_multi_select_fields()
    {
        var snapshot = FixtureProjectBuilder.CreateSnapshot(
            "Fixture",
            "example/fixture",
            "octocat",
            pullRequestNumber: 2);

        var field = Assert.Single(snapshot.Fields, field => field.Name == "Fixture Areas");
        Assert.Equal("MULTI_SELECT", field.DataType);
        Assert.Null(field.IssueField);
        Assert.Equal(["Backend", "Frontend", "Operations"], field.Options!.Select(option => option.Name));

        var draft = Assert.Single(snapshot.Items, item => item.Draft?.Title == "Fixture draft 1");
        var value = Assert.Single(draft.FieldValues, value => value.FieldName == field.Name);
        Assert.Equal(false, value.IsIssueField);
        Assert.Equal(["Backend", "Frontend"], value.MultiSelectOptionNames);
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, false, false)]
    public void Item_stage_runs_only_for_new_or_resumable_fixture(
        bool projectAlreadyExists,
        bool hasItemWork,
        bool projectImportWasPending,
        bool expected)
    {
        Assert.Equal(
            expected,
            FixtureProjectBuilder.ShouldImportItems(
                projectAlreadyExists,
                hasItemWork,
                projectImportWasPending));
    }

    [Fact]
    public void Status_only_import_log_does_not_resume_the_fixture_item_stage()
    {
        var log = new ImportLog
        {
            ProjectId = "PVT_fixture",
            SourceSnapshotFingerprint = "fingerprint",
        };
        log.StatusUpdates["0"] = "PVTSU_fixture";

        Assert.False(FixtureProjectBuilder.HasItemWork(log));
        Assert.False(FixtureProjectBuilder.ShouldImportItems(
            projectAlreadyExists: true,
            hasItemWork: FixtureProjectBuilder.HasItemWork(log),
            projectImportWasPending: false));
    }

    [Fact]
    public void Legacy_fixture_log_rebinds_to_the_status_update_snapshot_without_losing_item_state()
    {
        var snapshot = FixtureProjectBuilder.CreateSnapshot(
            "Fixture",
            "example/fixture",
            "octocat",
            pullRequestNumber: 2);
        var legacySnapshot = snapshot with { StatusUpdates = null };
        var legacyLog = new ImportLog
        {
            ProjectId = "PVT_fixture",
            SourceSnapshotFingerprint = ImportLog.ComputeSnapshotFingerprint(legacySnapshot),
        };
        legacyLog.Items["0"] = "PVTI_existing";
        legacyLog.ItemStates["draft:0"] = new ImportItemState
        {
            TargetItemId = "PVTI_existing",
            TargetContentIdentity = "draft",
        };

        var upgraded = FixtureProjectBuilder.UpgradeLegacyFixtureLog(legacyLog, snapshot);

        Assert.NotNull(upgraded);
        Assert.Equal(ImportLog.ComputeSnapshotFingerprint(snapshot), upgraded.SourceSnapshotFingerprint);
        Assert.Equal("PVTI_existing", upgraded.Items["0"]);
        Assert.Equal("PVTI_existing", upgraded.ItemStates["draft:0"].TargetItemId);
        Assert.Empty(upgraded.StatusUpdates);
        Assert.Empty(upgraded.PendingStatusUpdates);
    }

    [Fact]
    public void Fixture_status_history_match_is_exact_but_ignores_server_generated_metadata()
    {
        var expected = FixtureStatusUpdates();
        var actual = expected.Select(update => update with
        {
            Creator = "server-user",
            CreatedAt = "2026-08-16T00:00:00Z",
            UpdatedAt = "2026-08-16T00:01:00Z",
        }).ToList();

        Assert.True(FixtureProjectBuilder.FixtureStatusUpdatesMatch(expected, actual));
        Assert.False(FixtureProjectBuilder.FixtureStatusUpdatesMatch(
            expected,
            [.. actual, actual[0] with { Body = "extra" }]));
        Assert.False(FixtureProjectBuilder.FixtureStatusUpdatesMatch(
            expected,
            [actual[0] with { TargetDate = null }, .. actual.Skip(1)]));
    }

    private static IReadOnlyList<StatusUpdateSnapshot> FixtureStatusUpdates()
    {
        var snapshot = FixtureProjectBuilder.CreateSnapshot(
            "Fixture",
            "example/fixture",
            "octocat",
            pullRequestNumber: 2);

        Assert.NotNull(snapshot.StatusUpdates);
        return snapshot.StatusUpdates;
    }

    [Fact]
    public void Demo_fixture_exercises_every_status_update_status()
    {
        var updates = FixtureStatusUpdates();

        Assert.Equal(5, updates.Count);
        var statuses = updates.Select(update => update.Status).ToList();
        Assert.Equal(
            ["COMPLETE", "OFF_TRACK", "AT_RISK", "ON_TRACK", "INACTIVE"],
            statuses);
        Assert.Equal(statuses.Count, statuses.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Demo_fixture_status_updates_are_in_strictly_descending_created_at_order()
    {
        var updates = FixtureStatusUpdates();

        var timestamps = updates
            .Select(update => DateTimeOffset.Parse(update.CreatedAt, CultureInfo.InvariantCulture))
            .ToList();

        // Export order is reverse chronological (newest first), which the importer relies
        // on when it re-orders to oldest-first for creation.
        for (var index = 1; index < timestamps.Count; index++)
        {
            Assert.True(
                timestamps[index] < timestamps[index - 1],
                $"status update {index} ({updates[index].CreatedAt}) is not older than {updates[index - 1].CreatedAt}");
        }

        Assert.Equal(
            DateTimeOffset.Parse("2026-01-05T09:00:00Z", CultureInfo.InvariantCulture),
            timestamps[0]);
        Assert.Equal(
            DateTimeOffset.Parse("2026-01-01T09:00:00Z", CultureInfo.InvariantCulture),
            timestamps[^1]);
    }

    [Fact]
    public void Demo_fixture_status_updates_mix_null_and_populated_dates()
    {
        var updates = FixtureStatusUpdates();

        Assert.Contains(updates, update => update.StartDate is null);
        Assert.Contains(updates, update => update.TargetDate is null);

        var inactive = Assert.Single(updates, update => update.Status == "INACTIVE");
        Assert.Null(inactive.StartDate);
        Assert.Null(inactive.TargetDate);

        var complete = Assert.Single(updates, update => update.Status == "COMPLETE");
        Assert.Equal("2026-01-01", complete.StartDate);
        Assert.Equal("2026-04-15", complete.TargetDate);
    }

    [Fact]
    public void Demo_fixture_status_update_bodies_include_multi_line_and_markdown_content()
    {
        var updates = FixtureStatusUpdates();

        var multiLine = Assert.Single(updates, update => update.Status == "ON_TRACK");
        Assert.Contains("\n", multiLine.Body, StringComparison.Ordinal);

        var markdown = Assert.Single(updates, update => update.Status == "INACTIVE");
        Assert.Contains("**", markdown.Body, StringComparison.Ordinal);

        Assert.All(updates, update => Assert.False(string.IsNullOrWhiteSpace(update.Body)));
    }

    [Fact]
    public void Demo_fixture_status_updates_populate_every_snapshot_property_somewhere()
    {
        var updates = FixtureStatusUpdates();

        foreach (var property in typeof(StatusUpdateSnapshot).GetProperties())
        {
            Assert.Contains(updates, update => property.GetValue(update) is not null);
        }

        Assert.All(updates, update => Assert.Equal("octocat", update.Creator));
        Assert.Contains(
            updates,
            update => !string.Equals(update.UpdatedAt, update.CreatedAt, StringComparison.Ordinal));
    }
}
