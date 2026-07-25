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
        var fieldCatalog = IntegrationFixtureSnapshot.CreateFieldCatalog(result);
        Assert.Contains(fieldCatalog.Fields, field => field.Name == "Title" && field.DataType == "TITLE");
        Assert.Contains(fieldCatalog.Fields, field => field.Name == "Assignees" && field.DataType == "ASSIGNEES");
        Assert.Contains(fieldCatalog.Fields, field => field.Name == "Linked pull requests" && field.DataType == "LINKED_PULL_REQUESTS");
        Assert.Contains(fieldCatalog.Fields, field => field.Name == "Sub-issues progress" && field.DataType == "SUB_ISSUES_PROGRESS");
    }

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
