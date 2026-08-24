using System.Text.Json;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Tests;

/// <summary>M2 unit tests for the snapshot schema (serialization roundtrip, schema version).</summary>
public class SnapshotTests
{
    private static ProjectSnapshot CreateFullSnapshot() => new()
    {
        SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
        Project = new ProjectInfoSnapshot
        {
            Title = "Fixture",
            ShortDescription = "A test project",
            Readme = "# Readme\n\nBody",
            Public = false,
            Closed = false,
            Template = true,
        },
        Fields =
        [
            new FieldSnapshot { Name = "Title", DataType = "TITLE" },
            new FieldSnapshot
            {
                Name = "Fixture Text",
                DataType = "TEXT",
                DefaultValue = new FieldDefaultValueSnapshot { Text = "既定値 🚀" },
            },
            new FieldSnapshot
            {
                Name = "Fixture Number",
                DataType = "NUMBER",
                DefaultValue = new FieldDefaultValueSnapshot { Number = -42.5 },
            },
            new FieldSnapshot
            {
                Name = "Fixture Select",
                DataType = "SINGLE_SELECT",
                Options =
                [
                    new SingleSelectOptionSnapshot { Id = "o1", Name = "Alpha", Color = "RED", Description = "First" },
                    new SingleSelectOptionSnapshot { Id = "o2", Name = "Beta", Color = "BLUE", Description = null },
                ],
                DefaultValue = new FieldDefaultValueSnapshot { SingleSelectOptionName = "Beta" },
            },
            new FieldSnapshot
            {
                Name = "Fixture Teams",
                DataType = "MULTI_SELECT",
                Options =
                [
                    new SingleSelectOptionSnapshot { Id = "m1", Name = "Platform", Color = "PURPLE", Description = "Platform work" },
                    new SingleSelectOptionSnapshot { Id = "m2", Name = "SDK", Color = "GREEN", Description = null },
                ],
                IssueField = new IssueFieldConfigurationSnapshot
                {
                    Description = "Teams involved",
                    Visibility = "ALL",
                },
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
                        new IterationSnapshot { Id = "i1", Title = "Sprint 1", StartDate = "2026-07-06", Duration = 14 },
                    ],
                    CompletedIterations =
                    [
                        new IterationSnapshot { Id = "i0", Title = "Sprint 0", StartDate = "2026-06-22", Duration = 14 },
                    ],
                },
            },
        ],
        Views =
        [
            new ViewSnapshot
            {
                Number = 1,
                TabPosition = 0,
                Name = "View 1",
                Layout = "TABLE_LAYOUT",
                Filter = "is:issue -status:Done",
                GroupByFields = ["Status"],
                SortByFields = [new SortByFieldSnapshot { Field = "Fixture Number", Direction = "DESC" }],
                VerticalGroupByFields = [],
                VisibleFields = ["Title", "Status", "Fixture Text"],
                Ui = null,
            },
        ],
        Workflows =
        [
            new WorkflowSnapshot { Number = 1, Name = "Item closed", Enabled = true, Ui = null },
        ],
        Items =
        [
            new ItemSnapshot
            {
                Type = "ISSUE",
                Position = 0,
                IsArchived = false,
                Repository = "gpm-source/fixture-repo",
                Number = 1,
                FieldValues =
                [
                    new FieldValueSnapshot { FieldName = "Fixture Text", Text = "hello" },
                    new FieldValueSnapshot { FieldName = "Fixture Number", Number = 42.5 },
                    new FieldValueSnapshot { FieldName = "Fixture Date", Date = "2026-07-05" },
                    new FieldValueSnapshot { FieldName = "Fixture Select", SingleSelectOptionName = "Alpha" },
                    new FieldValueSnapshot { FieldName = "Fixture Teams", IsIssueField = true, MultiSelectOptionNames = ["Platform", "SDK"] },
                    new FieldValueSnapshot { FieldName = "Fixture Sprint", IterationTitle = "Sprint 1" },
                ],
            },
            new ItemSnapshot
            {
                Type = "DRAFT_ISSUE",
                Position = 1,
                IsArchived = true,
                Draft = new DraftIssueSnapshot { Title = "Fixture draft 1", Body = "body", Assignees = ["octocat"] },
                FieldValues = [],
            },
        ],
        StatusUpdates =
        [
            new StatusUpdateSnapshot
            {
                Body = "Fixture migration is complete.",
                Status = "COMPLETE",
                StartDate = "2026-01-01",
                TargetDate = "2026-04-15",
                Creator = "octocat",
                CreatedAt = "2026-01-05T09:00:00Z",
                UpdatedAt = "2026-01-05T10:30:00Z",
            },
            new StatusUpdateSnapshot
            {
                Body = "Kickoff with **Markdown**.\n\n- one\n- two",
                Status = "INACTIVE",
                StartDate = null,
                TargetDate = null,
                Creator = null,
                CreatedAt = "2026-01-01T09:00:00Z",
                UpdatedAt = "2026-01-01T09:00:00Z",
            },
        ],
        Collaborators =
        [
            new CollaboratorSnapshot { Type = "USER", Login = "octocat", Role = "WRITER" },
            new CollaboratorSnapshot { Type = "TEAM", Login = "fixture-team", Role = "READER" },
        ],
        LinkedRepositories = ["gpm-source/fixture-repo"],
        LinkedTeams =
        [
            new LinkedTeamSnapshot { Organization = "gpm-source", Slug = "platform", Name = "Platform" },
        ],
    };

    [Fact]
    public void Roundtrip_preserves_all_values()
    {
        var original = CreateFullSnapshot();

        var json = JsonSerializer.Serialize(original, SnapshotJsonContext.Default.ProjectSnapshot);
        var restored = JsonSerializer.Deserialize(json, SnapshotJsonContext.Default.ProjectSnapshot);

        Assert.NotNull(restored);
        Assert.Equal(original.SchemaVersion, restored.SchemaVersion);
        Assert.Equal(original.Project, restored.Project);

        Assert.Equal(original.Fields.Count, restored.Fields.Count);
        var select = restored.Fields.Single(f => f.Name == "Fixture Select");
        Assert.NotNull(select.Options);
        Assert.Equal(["Alpha", "Beta"], select.Options.Select(o => o.Name));
        Assert.Equal(["RED", "BLUE"], select.Options.Select(o => o.Color));
        Assert.Equal("Beta", select.DefaultValue!.SingleSelectOptionName);
        Assert.Equal("既定値 🚀", restored.Fields.Single(f => f.Name == "Fixture Text").DefaultValue!.Text);
        Assert.Equal(-42.5, restored.Fields.Single(f => f.Name == "Fixture Number").DefaultValue!.Number);

        var multiSelect = restored.Fields.Single(f => f.Name == "Fixture Teams");
        Assert.Equal("MULTI_SELECT", multiSelect.DataType);
        Assert.Equal(["Platform", "SDK"], multiSelect.Options!.Select(o => o.Name));
        Assert.Equal("Teams involved", multiSelect.IssueField!.Description);
        Assert.Equal("ALL", multiSelect.IssueField.Visibility);

        var sprint = restored.Fields.Single(f => f.Name == "Fixture Sprint");
        Assert.NotNull(sprint.IterationConfiguration);
        Assert.Equal(14, sprint.IterationConfiguration.Duration);
        Assert.Equal(1, sprint.IterationConfiguration.StartDay);
        Assert.Equal("Sprint 1", Assert.Single(sprint.IterationConfiguration.Iterations).Title);
        Assert.Equal("Sprint 0", Assert.Single(sprint.IterationConfiguration.CompletedIterations).Title);

        var view = Assert.Single(restored.Views);
        Assert.Equal("TABLE_LAYOUT", view.Layout);
        Assert.Equal("is:issue -status:Done", view.Filter);
        Assert.Equal(["Status"], view.GroupByFields);
        Assert.Equal(new SortByFieldSnapshot { Field = "Fixture Number", Direction = "DESC" }, Assert.Single(view.SortByFields));
        Assert.Equal(["Title", "Status", "Fixture Text"], view.VisibleFields);
        Assert.Null(view.Ui);

        var workflow = Assert.Single(restored.Workflows);
        Assert.Equal(new WorkflowSnapshot { Number = 1, Name = "Item closed", Enabled = true }, workflow);

        Assert.Equal(2, restored.Items.Count);
        var issue = restored.Items[0];
        Assert.Equal("gpm-source/fixture-repo", issue.Repository);
        Assert.Equal(1, issue.Number);
        Assert.Equal(
            original.Items[0].FieldValues.Select(value => value.FieldName),
            issue.FieldValues.Select(value => value.FieldName));
        foreach (var (expected, actual) in original.Items[0].FieldValues.Zip(issue.FieldValues))
        {
            Assert.Equal(expected.Text, actual.Text);
            Assert.Equal(expected.Number, actual.Number);
            Assert.Equal(expected.Date, actual.Date);
            Assert.Equal(expected.SingleSelectOptionName, actual.SingleSelectOptionName);
            Assert.Equal(expected.MultiSelectOptionNames, actual.MultiSelectOptionNames);
            Assert.Equal(expected.IterationTitle, actual.IterationTitle);
            Assert.Equal(expected.IsIssueField, actual.IsIssueField);
        }

        Assert.Equal(
            ["Platform", "SDK"],
            issue.FieldValues.Single(value => value.FieldName == "Fixture Teams").MultiSelectOptionNames);
        Assert.True(issue.FieldValues.Single(value => value.FieldName == "Fixture Teams").IsIssueField);
        var draftItem = restored.Items[1];
        Assert.True(draftItem.IsArchived);
        Assert.NotNull(draftItem.Draft);
        Assert.Equal("Fixture draft 1", draftItem.Draft.Title);
        Assert.Equal(["octocat"], draftItem.Draft.Assignees);

        Assert.NotNull(restored.Collaborators);
        Assert.Equal(2, restored.Collaborators.Count);
        Assert.Equal(new CollaboratorSnapshot { Type = "USER", Login = "octocat", Role = "WRITER" }, restored.Collaborators[0]);
        Assert.Equal(new CollaboratorSnapshot { Type = "TEAM", Login = "fixture-team", Role = "READER" }, restored.Collaborators[1]);
        Assert.Equal(["gpm-source/fixture-repo"], restored.LinkedRepositories);
        Assert.Equal("gpm-source/platform", Assert.Single(restored.LinkedTeams!).Identity);
        Assert.DoesNotContain("\"identity\"", json, StringComparison.Ordinal);

        Assert.NotNull(restored.StatusUpdates);
        Assert.Equal(original.StatusUpdates!.Count, restored.StatusUpdates.Count);
        foreach (var (expected, actual) in original.StatusUpdates.Zip(restored.StatusUpdates))
        {
            Assert.Equal(expected.Body, actual.Body);
            Assert.Equal(expected.Status, actual.Status);
            Assert.Equal(expected.StartDate, actual.StartDate);
            Assert.Equal(expected.TargetDate, actual.TargetDate);
            Assert.Equal(expected.Creator, actual.Creator);
            Assert.Equal(expected.CreatedAt, actual.CreatedAt);
            Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
        }
    }

    [Fact]
    public void Captured_cleared_default_round_trips_as_present_empty_object()
    {
        var original = CreateFullSnapshot() with
        {
            Fields =
            [
                new FieldSnapshot
                {
                    Name = "Fixture Number",
                    DataType = "NUMBER",
                    DefaultValue = new FieldDefaultValueSnapshot(),
                },
            ],
        };

        var json = JsonSerializer.Serialize(original, SnapshotJsonContext.Default.ProjectSnapshot);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            JsonValueKind.Object,
            document.RootElement.GetProperty("fields")[0].GetProperty("defaultValue").ValueKind);

        var restored = JsonSerializer.Deserialize(json, SnapshotJsonContext.Default.ProjectSnapshot);
        var defaultValue = Assert.Single(restored!.Fields).DefaultValue;
        Assert.NotNull(defaultValue);
        Assert.Null(defaultValue.Number);
    }

    [Fact]
    public void Deserialize_snapshot_without_collaborators_and_linked_repositories_yields_null()
    {
        // Snapshots written before the collaborator/linked-repository fields stay loadable
        // within schema version 1; the new fields deserialize as null ("not captured").
        const string Json =
            """
            {
              "schemaVersion": 1,
              "project": { "title": "T", "public": false, "closed": false },
              "fields": [], "views": [], "workflows": [], "items": []
            }
            """;

        var restored = JsonSerializer.Deserialize(Json, SnapshotJsonContext.Default.ProjectSnapshot);

        Assert.NotNull(restored);
        Assert.Null(restored.Collaborators);
        Assert.Null(restored.LinkedRepositories);
        Assert.Null(restored.LinkedTeams);
    }

    [Fact]
    public void View_without_tab_position_remains_backward_compatible()
    {
        const string Json =
            """
            {
              "schemaVersion": 1,
              "project": { "title": "T", "public": false, "closed": false },
              "fields": [],
              "views": [{
                "number": 7,
                "name": "Legacy",
                "layout": "TABLE_LAYOUT",
                "groupByFields": [],
                "sortByFields": [],
                "verticalGroupByFields": [],
                "visibleFields": []
              }],
              "workflows": [],
              "items": []
            }
            """;

        var restored = JsonSerializer.Deserialize(Json, SnapshotJsonContext.Default.ProjectSnapshot);

        Assert.Null(Assert.Single(restored!.Views).TabPosition);
        Assert.Equal(1, restored.SchemaVersion);
    }

    [Fact]
    public void Serialized_json_contains_schema_version()
    {
        var json = JsonSerializer.Serialize(CreateFullSnapshot(), SnapshotJsonContext.Default.ProjectSnapshot);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void Deserialize_without_schema_version_throws()
    {
        const string Json =
            """
            {
              "project": { "title": "T", "public": false, "closed": false },
              "fields": [], "views": [], "workflows": [], "items": []
            }
            """;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(Json, SnapshotJsonContext.Default.ProjectSnapshot));
    }

    [Fact]
    public async Task SnapshotFile_saves_and_loads_snapshot_json()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ghpmv-test-{Guid.NewGuid():N}");
        try
        {
            var original = CreateFullSnapshot();

            var path = await SnapshotFile.SaveAsync(original, directory, TestContext.Current.CancellationToken);

            Assert.Equal(Path.Combine(directory, "snapshot.json"), path);
            var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains("\n", text, StringComparison.Ordinal); // indented output has line breaks

            var restored = await SnapshotFile.LoadAsync(directory, TestContext.Current.CancellationToken);
            Assert.Equal(original.SchemaVersion, restored.SchemaVersion);
            Assert.Equal(original.Project, restored.Project);
            Assert.Equal(0, Assert.Single(restored.Views).TabPosition);
            Assert.Equal(original.Items.Count, restored.Items.Count);
            Assert.NotNull(restored.StatusUpdates);
            Assert.Equal(original.StatusUpdates!.Count, restored.StatusUpdates.Count);
            Assert.Equal("Fixture migration is complete.", restored.StatusUpdates[0].Body);
            Assert.Equal("COMPLETE", restored.StatusUpdates[0].Status);
            Assert.Equal("2026-01-05T09:00:00Z", restored.StatusUpdates[0].CreatedAt);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Roundtrip_preserves_status_updates()
    {
        var original = CreateFullSnapshot();

        var json = JsonSerializer.Serialize(original, SnapshotJsonContext.Default.ProjectSnapshot);
        var restored = JsonSerializer.Deserialize(json, SnapshotJsonContext.Default.ProjectSnapshot);

        Assert.NotNull(restored);
        Assert.NotNull(restored.StatusUpdates);
        Assert.Equal(2, restored.StatusUpdates.Count);

        // Reverse chronological order (newest first) is part of the contract, so the
        // sequence must survive serialization exactly as written.
        Assert.Equal(
            ["2026-01-05T09:00:00Z", "2026-01-01T09:00:00Z"],
            restored.StatusUpdates.Select(update => update.CreatedAt));

        var populated = restored.StatusUpdates[0];
        Assert.Equal("Fixture migration is complete.", populated.Body);
        Assert.Equal("COMPLETE", populated.Status);
        Assert.Equal("2026-01-01", populated.StartDate);
        Assert.Equal("2026-04-15", populated.TargetDate);
        Assert.Equal("octocat", populated.Creator);
        Assert.Equal("2026-01-05T09:00:00Z", populated.CreatedAt);
        Assert.Equal("2026-01-05T10:30:00Z", populated.UpdatedAt);

        var optionalNulls = restored.StatusUpdates[1];
        Assert.Equal("Kickoff with **Markdown**.\n\n- one\n- two", optionalNulls.Body);
        Assert.Equal("INACTIVE", optionalNulls.Status);
        Assert.Null(optionalNulls.StartDate);
        Assert.Null(optionalNulls.TargetDate);
        Assert.Null(optionalNulls.Creator);
        Assert.Equal("2026-01-01T09:00:00Z", optionalNulls.CreatedAt);
        Assert.Equal(optionalNulls.CreatedAt, optionalNulls.UpdatedAt);
    }

    [Fact]
    public void Statusless_update_round_trips_when_status_property_is_omitted()
    {
        var original = CreateFullSnapshot() with
        {
            StatusUpdates =
            [
                new StatusUpdateSnapshot
                {
                    Body = "Update without a status.",
                    Status = null,
                    CreatedAt = "2026-01-06T09:00:00Z",
                    UpdatedAt = "2026-01-06T09:00:00Z",
                },
            ],
        };

        var json = JsonSerializer.Serialize(original, SnapshotJsonContext.Default.ProjectSnapshot);
        using var document = JsonDocument.Parse(json);
        var serializedUpdate = document.RootElement.GetProperty("statusUpdates")[0];
        Assert.False(serializedUpdate.TryGetProperty("status", out _));

        var restored = JsonSerializer.Deserialize(json, SnapshotJsonContext.Default.ProjectSnapshot);

        var restoredUpdate = Assert.Single(Assert.IsType<ProjectSnapshot>(restored).StatusUpdates!);
        Assert.Null(restoredUpdate.Status);
        Assert.Equal("Update without a status.", restoredUpdate.Body);
    }

    [Fact]
    public void Deserialize_snapshot_without_status_updates_yields_null()
    {
        // Snapshots written before status update support stay loadable within schema
        // version 1; the new collection deserializes as null ("not captured").
        const string Json =
            """
            {
              "schemaVersion": 1,
              "project": { "title": "T", "public": false, "closed": false },
              "fields": [], "views": [], "workflows": [], "items": []
            }
            """;

        var restored = JsonSerializer.Deserialize(Json, SnapshotJsonContext.Default.ProjectSnapshot);

        Assert.NotNull(restored);
        Assert.Null(restored.Project.Template);
        Assert.Null(restored.StatusUpdates);
        Assert.Empty(restored.Items);
    }

    [Fact]
    public void Serialized_json_keeps_schema_version_one_when_status_updates_are_present()
    {
        // Status updates are an additive schema-v1 field: capturing them must not bump
        // the version, otherwise every previously written snapshot becomes unreadable.
        Assert.Equal(1, ProjectSnapshot.CurrentSchemaVersion);

        var json = JsonSerializer.Serialize(CreateFullSnapshot(), SnapshotJsonContext.Default.ProjectSnapshot);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("statusUpdates").GetArrayLength());
        Assert.True(document.RootElement.GetProperty("project").GetProperty("template").GetBoolean());
    }

    [Fact]
    public void Snapshot_with_empty_status_update_list_round_trips_as_empty_not_null()
    {
        // "Captured, none exist" ([]) and "not captured" (null) drive different importer
        // and verifier behavior, so the distinction must survive a roundtrip.
        var original = CreateFullSnapshot() with { StatusUpdates = [] };

        var json = JsonSerializer.Serialize(original, SnapshotJsonContext.Default.ProjectSnapshot);
        var restored = JsonSerializer.Deserialize(json, SnapshotJsonContext.Default.ProjectSnapshot);

        Assert.NotNull(restored);
        Assert.NotNull(restored.StatusUpdates);
        Assert.Empty(restored.StatusUpdates);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("statusUpdates").ValueKind);
        Assert.Equal(0, document.RootElement.GetProperty("statusUpdates").GetArrayLength());
    }
}
