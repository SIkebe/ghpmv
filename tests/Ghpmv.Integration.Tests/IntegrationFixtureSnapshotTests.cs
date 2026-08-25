using Ghpmv.Core.Fixtures;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Integration.Tests;

public class IntegrationFixtureSnapshotTests
{
    [Fact]
    public void SelectCanonicalItems_excludes_unrelated_shared_fixture_items()
    {
        var snapshot = new ProjectSnapshot
        {
            SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
            Project = new ProjectInfoSnapshot { Title = "fixture", Public = false, Closed = false },
            Fields = [],
            Views = [],
            Workflows = [],
            Items =
            [
                Draft("Fixture draft 1", 0),
                Draft("Fixture draft 2", 1),
                Draft("Fixture draft 3", 2),
                Content("ISSUE", 1, 3),
                Content("PULL_REQUEST", 3, 4),
                Draft("Fixture archived draft", 5),
                Draft("Fixture assigned draft", 6),
                Content("ISSUE", 4, 7),
            ],
        };

        var result = IntegrationFixtureSnapshot.SelectCanonicalItems(snapshot);

        Assert.Equal(7, result.Items.Count);
        Assert.Equal(Enumerable.Range(0, 7), result.Items.Select(item => item.Position));
        Assert.DoesNotContain(result.Items, item => item.Type == "ISSUE" && item.Number == 4);
    }

    [Fact]
    public void NormalizeKnownSnapshot_matches_exported_field_value_shape()
    {
        var snapshot = FixtureProjectBuilder.CreateSnapshot(
            "fixture",
            IntegrationTestSettings.FixtureRepositoryFullName,
            "viewer",
            IntegrationTestSettings.FixturePullRequestNumber);

        var result = IntegrationFixtureSnapshot.NormalizeKnownSnapshot(snapshot, "viewer");

        Assert.All(result.Items.SelectMany(item => item.FieldValues), value =>
            Assert.NotNull(value.IsIssueField));
        Assert.All(result.Items.Where(item => item.Draft is not null), item =>
        {
            var title = Assert.Single(item.FieldValues, value => value.FieldName == "Title");
            Assert.False(title.IsIssueField);
            Assert.Equal(item.Draft!.Title, title.Text);
            Assert.Equal("viewer", item.Draft.Creator);
        });
        Assert.Equal(
            "Fixture issue 1",
            result.Items.Single(item => item.Type == "ISSUE").FieldValues
                .Single(value => value.FieldName == "Title").Text);
        Assert.Equal(
            "Fixture pull request",
            result.Items.Single(item => item.Type == "PULL_REQUEST").FieldValues
                .Single(value => value.FieldName == "Title").Text);
        Assert.Contains(result.Fields, field => field.Name == "Title" && field.DataType == "TITLE");
        Assert.Contains(result.Fields, field => field.Name == "Assignees" && field.DataType == "ASSIGNEES");
        Assert.Contains(result.Fields, field => field.Name == "Linked pull requests" && field.DataType == "LINKED_PULL_REQUESTS");
        Assert.Contains(result.Fields, field => field.Name == "Sub-issues progress" && field.DataType == "SUB_ISSUES_PROGRESS");
    }

    [Fact]
    public void SelectExpectedStatusUpdates_allows_unrelated_history_around_and_between_fixture_entries()
    {
        var expected = FixtureStatusUpdates();
        StatusUpdateSnapshot[] actual =
        [
            Unrelated("newer"),
            expected[0] with { Creator = "server-user", CreatedAt = "2026-08-16T00:05:00Z" },
            Unrelated("between"),
            expected[1] with { Creator = "server-user", CreatedAt = "2026-08-16T00:04:00Z" },
            Unrelated("older"),
        ];

        var result = IntegrationFixtureSnapshot.SelectExpectedStatusUpdates(actual, expected);

        Assert.Equal(expected.Select(update => update.Body), result.Select(update => update.Body));
    }

    [Fact]
    public void SelectExpectedStatusUpdates_selects_one_canonical_legacy_duplicate()
    {
        var expected = FixtureStatusUpdates();
        StatusUpdateSnapshot[] actual =
        [
            expected[0],
            expected[0] with { CreatedAt = "2026-08-16T00:06:00Z" },
            expected[1],
        ];

        var result = IntegrationFixtureSnapshot.SelectExpectedStatusUpdates(actual, expected);

        Assert.Equal(expected.Length, result.Count);
        Assert.Same(actual[0], result[0]);
        Assert.Same(actual[2], result[1]);
    }

    [Fact]
    public void SelectExpectedStatusUpdates_fails_when_a_fixture_entry_is_missing()
    {
        var expected = FixtureStatusUpdates();

        var exception = Assert.Throws<InvalidOperationException>(
            () => IntegrationFixtureSnapshot.SelectExpectedStatusUpdates([expected[0]], expected));

        Assert.Contains("was not found", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectExpectedStatusUpdates_fails_when_fixture_entries_are_out_of_order()
    {
        var expected = FixtureStatusUpdates();

        var exception = Assert.Throws<InvalidOperationException>(
            () => IntegrationFixtureSnapshot.SelectExpectedStatusUpdates(
                [expected[1], Unrelated("between"), expected[0]],
                expected));

        Assert.Contains("not in reverse chronological order", exception.Message, StringComparison.Ordinal);
    }

    private static StatusUpdateSnapshot[] FixtureStatusUpdates()
    {
        var updates = FixtureProjectBuilder.CreateSnapshot(
            "fixture",
            IntegrationTestSettings.FixtureRepositoryFullName,
            "viewer",
            IntegrationTestSettings.FixturePullRequestNumber).StatusUpdates;
        Assert.NotNull(updates);
        return updates.Take(2).ToArray();
    }

    private static StatusUpdateSnapshot Unrelated(string suffix) => new()
    {
        Body = $"Unrelated {suffix}",
        CreatedAt = "2026-08-16T00:00:00Z",
        UpdatedAt = "2026-08-16T00:00:00Z",
    };

    private static ItemSnapshot Draft(string title, int position) => new()
    {
        Type = "DRAFT_ISSUE",
        Position = position,
        IsArchived = title == "Fixture archived draft",
        Draft = new DraftIssueSnapshot { Title = title, Assignees = [] },
        FieldValues = [],
    };

    private static ItemSnapshot Content(string type, int number, int position) => new()
    {
        Type = type,
        Position = position,
        IsArchived = false,
        Repository = IntegrationTestSettings.FixtureRepositoryFullName,
        Number = number,
        FieldValues = [],
    };
}
