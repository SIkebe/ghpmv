using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Ghpmv.Core.Export;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Tests;

public class ProjectExporterTests
{
    [Fact]
    public async Task Export_captures_the_project_template_state()
    {
        using var handler = new StubHandler(
            MetadataResponse("[]", template: true),
            EmptyItemsResponse,
            EmptyStatusUpdatesResponse,
            FieldsResponse("[]"));
        using var client = CreateClient(handler);

        var snapshot = await new ProjectExporter(client).ExportAsync(
            "source",
            1,
            TestContext.Current.CancellationToken);

        Assert.True(snapshot.Project.Template);
        Assert.Contains("template", handler.RequestBodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_prefers_configured_visible_fields_from_view_configuration()
    {
        using var handler = new StubHandler(
            MetadataResponse(
                """
                [{"number":3,"name":"All","layout":"TABLE_LAYOUT","filter":null,
                  "groupByFields":{"nodes":[]},"verticalGroupByFields":{"nodes":[]},"sortByFields":{"nodes":[]},
                  "configuration":{"visibleFields":{"nodes":[{"name":"Status"},{"name":"Title"}]}},
                  "fields":{"nodes":[{"name":"Legacy field"}]}}]
                """),
            EmptyItemsResponse,
            EmptyStatusUpdatesResponse,
            FieldsResponse(
                """
                [
                  {"__typename":"ProjectV2Field","id":"PVTF_title","name":"Title","dataType":"TITLE","isIssueField":false,"issueField":null},
                  {"__typename":"ProjectV2SingleSelectField","id":"PVTSSF_status","name":"Status",
                   "dataType":"SINGLE_SELECT","isIssueField":false,"issueField":null,
                   "options":[{"id":"todo","name":"Todo","color":"GRAY","description":null}]}
                ]
                """));
        using var client = CreateClient(handler);

        var snapshot = await new ProjectExporter(client).ExportAsync(
            "source",
            1,
            TestContext.Current.CancellationToken);

        Assert.Equal(["Status", "Title"], Assert.Single(snapshot.Views).VisibleFields);
        Assert.Contains("configuration", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.Contains("visibleFields", handler.RequestBodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_captures_graphql_position_order_independently_of_view_number()
    {
        using var handler = new StubHandler(
            MetadataResponse(
                """
                [
                  {"number":9,"name":"Roadmap","layout":"ROADMAP_LAYOUT","filter":null,
                   "groupByFields":{"nodes":[]},"verticalGroupByFields":{"nodes":[]},"sortByFields":{"nodes":[]},
                   "configuration":{"visibleFields":{"nodes":[]}}},
                  {"number":2,"name":"Table","layout":"TABLE_LAYOUT","filter":null,
                   "groupByFields":{"nodes":[]},"verticalGroupByFields":{"nodes":[]},"sortByFields":{"nodes":[]},
                   "configuration":{"visibleFields":{"nodes":[]}}}
                ]
                """),
            EmptyItemsResponse,
            EmptyStatusUpdatesResponse,
            FieldsResponse("[]"));
        using var client = CreateClient(handler);

        var snapshot = await new ProjectExporter(client).ExportAsync(
            "source",
            1,
            TestContext.Current.CancellationToken);

        Assert.Equal(["Roadmap", "Table"], snapshot.Views.Select(view => view.Name));
        Assert.Equal([0, 1], snapshot.Views.Select(view => view.TabPosition));
        Assert.Contains(
            "views(first: 50, orderBy: { field: POSITION, direction: ASC })",
            handler.RequestBodies[0],
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_paginates_position_ordered_views_without_resetting_tab_positions()
    {
        using var handler = new StubHandler(
            MetadataResponse(
                """
                [{"number":9,"name":"First","layout":"TABLE_LAYOUT","filter":null,
                  "groupByFields":{"nodes":[]},"verticalGroupByFields":{"nodes":[]},"sortByFields":{"nodes":[]},
                  "configuration":{"visibleFields":{"nodes":[]}}}]
                """,
                hasNextPage: true,
                endCursor: "view-cursor"),
            ViewsResponse(
                """
                [{"number":2,"name":"Second","layout":"BOARD_LAYOUT","filter":null,
                  "groupByFields":{"nodes":[]},"verticalGroupByFields":{"nodes":[]},"sortByFields":{"nodes":[]},
                  "configuration":{"visibleFields":{"nodes":[]}}}]
                """),
            EmptyItemsResponse,
            EmptyStatusUpdatesResponse,
            FieldsResponse("[]"));
        using var client = CreateClient(handler);

        var snapshot = await new ProjectExporter(client).ExportAsync(
            "source",
            1,
            TestContext.Current.CancellationToken);

        Assert.Equal(["First", "Second"], snapshot.Views.Select(view => view.Name));
        Assert.Equal([0, 1], snapshot.Views.Select(view => view.TabPosition));
        Assert.Contains("\"after\":\"view-cursor\"", handler.RequestBodies[1], StringComparison.Ordinal);
        Assert.Contains("orderBy", handler.RequestBodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_reads_linked_issue_field_identity_and_definition_directly_from_project_fields()
    {
        using var handler = new StubHandler(
            MetadataResponse("[]"),
            """
            {"data":{"organization":{"projectV2":{"items":{
              "nodes":[{
                "type":"ISSUE","isArchived":false,
                "content":{"number":7,"repository":{"nameWithOwner":"source/repo"}},
                "fieldValues":{"nodes":[
                  {
                    "__typename":"ProjectV2ItemIssueFieldValue",
                    "field":{"name":"Teams"},
                    "issueFieldValue":{
                      "__typename":"IssueFieldMultiSelectValue",
                      "options":[{"name":"Platform"},{"name":"SDK"}]
                    }
                  },{
                    "__typename":"ProjectV2ItemIssueFieldValue",
                    "field":{"name":"Notes"},
                    "issueFieldValue":{"__typename":"IssueFieldTextValue","value":"Needs review"}
                  },{
                    "__typename":"ProjectV2ItemFieldTextValue",
                    "field":{"name":"Notes"},
                    "text":"Project note"
                  }
                ]}
              }],
              "pageInfo":{"hasNextPage":false,"endCursor":null}
            }}}}}
            """,
            EmptyStatusUpdatesResponse,
            FieldsResponse(
                """
                [
                  {"__typename":"ProjectV2Field","id":"PVTF_title","name":"Title","dataType":"TITLE",
                   "isIssueField":false,"issueField":null},
                  {"__typename":"ProjectV2Field","id":"PVTF_notes","name":"Notes","dataType":"TEXT",
                   "isIssueField":false,"issueField":null},
                  {"__typename":"ProjectV2Field","id":"PVTF_issue_notes","name":"Notes","dataType":"TEXT",
                   "isIssueField":true,
                   "issueField":{"__typename":"IssueFieldText","name":"Notes","dataType":"TEXT",
                     "description":"Review notes","visibility":"ALL"}},
                  {"__typename":"ProjectV2MultiSelectField","id":"PVTMSF_teams","name":"Teams",
                   "dataType":"MULTI_SELECT","isIssueField":true,"multiSelectOptions":[],
                   "issueField":{"__typename":"IssueFieldMultiSelect","name":"Teams","dataType":"MULTI_SELECT",
                     "description":"Teams involved","visibility":"ALL",
                     "options":[
                       {"id":"IFO_platform","name":"Platform","color":"PURPLE","description":"Platform work"},
                       {"id":"IFO_sdk","name":"SDK","color":"GREEN","description":null}
                     ]}},
                  {"__typename":"ProjectV2SingleSelectField","id":"PVTSSF_priority","name":"Priority",
                   "dataType":"SINGLE_SELECT","isIssueField":true,"options":[],
                   "issueField":{"__typename":"IssueFieldSingleSelect","name":"Priority","dataType":"SINGLE_SELECT",
                     "description":null,"visibility":"ALL",
                     "options":[{"id":"IFO_high","name":"High","color":"RED","description":null}]}}
                ]
                """));
        using var client = CreateClient(handler);

        var snapshot = await new ProjectExporter(client).ExportAsync(
            "source",
            1,
            TestContext.Current.CancellationToken);

        var teams = snapshot.Fields.Single(field => field.Name == "Teams");
        Assert.Equal(["Platform", "SDK"], teams.Options!.Select(option => option.Name));
        Assert.Equal("Teams involved", teams.IssueField!.Description);
        var notes = snapshot.Fields.Single(field => field.Name == "Notes" && field.IssueField is not null);
        Assert.Equal("Review notes", notes.IssueField!.Description);
        Assert.Contains(snapshot.Fields, field => field.Name == "Notes" && field.IssueField is null);
        var priority = snapshot.Fields.Single(field => field.Name == "Priority");
        Assert.Equal(["High"], priority.Options!.Select(option => option.Name));

        var item = Assert.Single(snapshot.Items);
        Assert.Equal(
            "Needs review",
            item.FieldValues.Single(value => value is { FieldName: "Notes", IsIssueField: true }).Text);
        Assert.Equal(
            "Project note",
            item.FieldValues.Single(value => value is { FieldName: "Notes", IsIssueField: false }).Text);
        var teamsValue = item.FieldValues.Single(value => value is { FieldName: "Teams", IsIssueField: true });
        Assert.Equal(["Platform", "SDK"], teamsValue.MultiSelectOptionNames);
        Assert.Equal(5, handler.RequestBodies.Count);
        Assert.Contains("isIssueField", handler.RequestBodies[3], StringComparison.Ordinal);
        Assert.Contains("issueField", handler.RequestBodies[3], StringComparison.Ordinal);
        Assert.DoesNotContain(
            handler.RequestBodies,
            body => body.Contains("organization.issueFields", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Export_paginates_project_fields_without_truncating_the_snapshot()
    {
        using var handler = new StubHandler(
            MetadataResponse("[]"),
            EmptyItemsResponse,
            EmptyStatusUpdatesResponse,
            FieldsResponse(
                """
                [{"__typename":"ProjectV2Field","id":"PVTF_first","name":"First","dataType":"TEXT",
                  "isIssueField":false,"issueField":null}]
                """,
                hasNextPage: true,
                endCursor: "field-cursor"),
            FieldsResponse(
                """
                [{"__typename":"ProjectV2Field","id":"PVTF_second","name":"Second","dataType":"TEXT",
                  "isIssueField":false,"issueField":null}]
                """));
        using var client = CreateClient(handler);

        var snapshot = await new ProjectExporter(client).ExportAsync(
            "source",
            1,
            TestContext.Current.CancellationToken);

        Assert.Equal(["First", "Second"], snapshot.Fields.Select(field => field.Name));
        Assert.Equal(6, handler.RequestBodies.Count);
        Assert.Contains("\"after\":\"field-cursor\"", handler.RequestBodies[4], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_paginates_linked_teams_and_uses_stable_logical_identities()
    {
        using var handler = new StubHandler(
            [
                TeamsResponse(
                    """[{"name":"Platform","slug":"platform","organization":{"login":"source"}}]""",
                    hasNextPage: true,
                    endCursor: "team-cursor"),
                TeamsResponse(
                    """[{"name":"SDK","slug":"sdk","organization":{"login":"source"}}]"""),
            ],
            MetadataResponse("[]"),
            EmptyItemsResponse,
            EmptyStatusUpdatesResponse,
            FieldsResponse("[]"));
        using var client = CreateClient(handler);

        var snapshot = await new ProjectExporter(client).ExportAsync(
            "source",
            1,
            TestContext.Current.CancellationToken);

        var linkedTeams = Assert.IsAssignableFrom<IReadOnlyList<LinkedTeamSnapshot>>(snapshot.LinkedTeams);
        Assert.Equal(["source/platform", "source/sdk"], linkedTeams.Select(team => team.Identity));
        Assert.Equal(["Platform", "SDK"], linkedTeams.Select(team => team.Name));
        Assert.DoesNotContain(
            JsonSerializer.Serialize(snapshot, SnapshotJsonContext.Default.ProjectSnapshot),
            "TEAM_",
            StringComparison.Ordinal);
        Assert.Contains(handler.RequestBodies, body => body.Contains("\"after\":\"team-cursor\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task User_owned_export_has_empty_team_links_without_querying_teams()
    {
        using var handler = new StubHandler(
            MetadataResponse("[]").Replace("\"organization\"", "\"user\"", StringComparison.Ordinal),
            EmptyItemsResponse.Replace("\"organization\"", "\"user\"", StringComparison.Ordinal),
            EmptyStatusUpdatesResponse.Replace("\"organization\"", "\"user\"", StringComparison.Ordinal),
            FieldsResponse("[]").Replace("\"organization\"", "\"user\"", StringComparison.Ordinal));
        using var client = CreateClient(handler);

        var snapshot = await new ProjectExporter(client)
        {
            OwnerType = ProjectOwnerType.User,
        }.ExportAsync("source-user", 1, TestContext.Current.CancellationToken);

        Assert.Empty(snapshot.LinkedTeams!);
        Assert.DoesNotContain(handler.RequestBodies, body => body.Contains("teams(first:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Export_identifies_unset_linked_issue_field_without_item_value_evidence()
    {
        using var handler = new StubHandler(
            MetadataResponse("[]"),
            EmptyItemsResponse,
            EmptyStatusUpdatesResponse,
            FieldsResponse(
                """
                [{
                  "__typename":"ProjectV2MultiSelectField","id":"PVTMSF_teams","name":"Teams",
                  "dataType":"MULTI_SELECT","isIssueField":true,"multiSelectOptions":[],
                  "issueField":{"__typename":"IssueFieldMultiSelect","name":"Teams","dataType":"MULTI_SELECT",
                    "description":"Teams involved","visibility":"ORG_ONLY",
                    "options":[{"id":"IFO_sdk","name":"SDK","color":"GREEN","description":null}]}
                }]
                """));
        using var client = CreateClient(handler);

        var snapshot = await new ProjectExporter(client).ExportAsync(
            "source",
            1,
            TestContext.Current.CancellationToken);

        var teams = Assert.Single(snapshot.Fields);
        Assert.Equal("Teams involved", teams.IssueField!.Description);
        Assert.Equal("ORG_ONLY", teams.IssueField.Visibility);
        Assert.Equal(["SDK"], teams.Options!.Select(option => option.Name));
    }

    [Fact]
    public async Task Export_rejects_issue_field_without_linked_definition()
    {
        using var handler = new StubHandler(
            MetadataResponse("[]"),
            EmptyItemsResponse,
            EmptyStatusUpdatesResponse,
            FieldsResponse(
                """
                [{
                  "__typename":"ProjectV2Field","id":"PVTF_notes","name":"Notes","dataType":"TEXT",
                  "isIssueField":true,"issueField":null
                }]
                """));
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<GitHubGraphQLException>(() =>
            new ProjectExporter(client).ExportAsync(
                "source",
                1,
                TestContext.Current.CancellationToken));

        Assert.Contains("marked as an Issue Field", exception.Message, StringComparison.Ordinal);
        Assert.Contains("definition was unavailable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_rejects_mismatched_linked_issue_field_definition()
    {
        using var handler = new StubHandler(
            MetadataResponse("[]"),
            EmptyItemsResponse,
            EmptyStatusUpdatesResponse,
            FieldsResponse(
                """
                [{
                  "__typename":"ProjectV2Field","id":"PVTF_notes","name":"Notes","dataType":"TEXT",
                  "isIssueField":true,
                  "issueField":{"__typename":"IssueFieldNumber","name":"Effort","dataType":"NUMBER",
                    "description":null,"visibility":"ALL"}
                }]
                """));
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<GitHubGraphQLException>(() =>
            new ProjectExporter(client).ExportAsync(
                "source",
                1,
                TestContext.Current.CancellationToken));

        Assert.Contains("did not match its linked Issue Field", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "ordinary")]
    [InlineData(true, "linked")]
    public async Task Export_rejects_duplicate_field_identity(bool isIssueField, string identityKind)
    {
        var issueField = isIssueField
            ? """
              ,"issueField":{"__typename":"IssueFieldText","name":"Notes","dataType":"TEXT",
                "description":null,"visibility":"ALL"}
              """
            : ""","issueField":null""";
        var field =
            $$"""
            {"__typename":"ProjectV2Field","id":"PVTF_notes","name":"Notes","dataType":"TEXT",
             "isIssueField":{{isIssueField.ToString().ToLowerInvariant()}}{{issueField}}}
            """;
        using var handler = new StubHandler(
            MetadataResponse("[]"),
            EmptyItemsResponse,
            EmptyStatusUpdatesResponse,
            FieldsResponse($"[{field},{field}]"));
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<GitHubGraphQLException>(() =>
            new ProjectExporter(client).ExportAsync(
                "source",
                1,
                TestContext.Current.CancellationToken));

        Assert.Contains("duplicate field identity 'Notes'", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"({identityKind})", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_fails_instead_of_writing_an_incomplete_snapshot_when_field_enumeration_fails()
    {
        using var handler = new StubHandler(
            MetadataResponse("[]"),
            EmptyItemsResponse,
            EmptyStatusUpdatesResponse,
            """
            {"data":{"organization":{"projectV2":{"fields":null}}},"errors":[
              {"message":"Something went wrong while executing your query on the preview API."}
            ]}
            """,
            """
            {"data":{"organization":{"projectV2":{"fields":null}}},"errors":[
              {"message":"Something went wrong while executing your query on the preview API."}
            ]}
            """,
            """
            {"data":{"organization":{"projectV2":{"fields":null}}},"errors":[
              {"message":"Something went wrong while executing your query on the preview API."}
            ]}
            """,
            """
            {"data":{"organization":{"projectV2":{"fields":null}}},"errors":[
              {"message":"Something went wrong while executing your query on the preview API."}
            ]}
            """);
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<GitHubGraphQLException>(() =>
            new ProjectExporter(client).ExportAsync(
                "source",
                1,
                TestContext.Current.CancellationToken));

        Assert.Contains("No snapshot was written", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("--enable-browser-automation", exception.Message, StringComparison.Ordinal);
        Assert.Equal(7, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task Export_reads_ordinary_multi_select_field_definitions_and_item_values()
    {
        using var handler = new StubHandler(
            MetadataResponse("[]"),
            """
            {"data":{"organization":{"projectV2":{"items":{
              "nodes":[{
                "type":"DRAFT_ISSUE","isArchived":false,
                "content":{"title":"Draft","body":null,"creator":null,"createdAt":null,"assignees":{"nodes":[]}},
                "fieldValues":{"nodes":[{
                  "__typename":"ProjectV2ItemFieldMultiSelectValue",
                  "field":{"name":"Areas"},
                  "options":[{"name":"Platform"},{"name":"SDK"}]
                }]}
              }],
              "pageInfo":{"hasNextPage":false,"endCursor":null}
            }}}}}
            """,
            EmptyStatusUpdatesResponse,
            FieldsResponse(
                """
                [
                  {"__typename":"ProjectV2Field","id":"PVTF_title","name":"Title","dataType":"TITLE",
                   "isIssueField":false,"issueField":null},
                  {"__typename":"ProjectV2MultiSelectField","id":"PVTMSF_areas","name":"Areas",
                   "dataType":"MULTI_SELECT","isIssueField":false,"issueField":null,
                   "multiSelectOptions":[
                     {"id":"PVTMSFO_platform","name":"Platform","color":"PURPLE","description":"Platform work"},
                     {"id":"PVTMSFO_sdk","name":"SDK","color":"GREEN","description":null}
                   ]}
                ]
                """));
        using var client = CreateClient(handler);

        var snapshot = await new ProjectExporter(client).ExportAsync(
            "source",
            1,
            TestContext.Current.CancellationToken);

        var field = snapshot.Fields.Single(candidate => candidate.Name == "Areas");
        Assert.Null(field.IssueField);
        Assert.Equal(
            [
                ("PVTMSFO_platform", "Platform", "PURPLE", "Platform work"),
                ("PVTMSFO_sdk", "SDK", "GREEN", null),
            ],
            field.Options!.Select(option => (option.Id, option.Name, option.Color, option.Description)));
        var value = Assert.Single(Assert.Single(snapshot.Items).FieldValues);
        Assert.False(value.IsIssueField);
        Assert.Equal(["Platform", "SDK"], value.MultiSelectOptionNames);
    }

    [Fact]
    public async Task Export_captures_status_updates_in_reverse_chronological_order()
    {
        using var handler = new StubHandler(
            MetadataResponse("[]"),
            EmptyItemsResponse,
            StatusUpdatesResponse(
                """
                [
                  {"body":"Newest update","status":"COMPLETE","startDate":"2026-01-01","targetDate":"2026-04-15",
                   "creator":{"login":"octocat"},"createdAt":"2026-01-05T09:00:00Z","updatedAt":"2026-01-06T10:30:00Z"},
                  {"body":"Middle update","status":"AT_RISK","startDate":"2025-12-01","targetDate":"2026-03-01",
                   "creator":{"login":"hubot"},"createdAt":"2026-01-03T08:00:00Z","updatedAt":"2026-01-03T08:00:00Z"},
                  {"body":"Oldest update","status":"INACTIVE","startDate":"2025-11-01","targetDate":"2026-02-01",
                   "creator":{"login":"monalisa"},"createdAt":"2026-01-01T07:00:00Z","updatedAt":"2026-01-02T07:00:00Z"}
                ]
                """),
            FieldsResponse("[]"));
        using var client = CreateClient(handler);

        var snapshot = await new ProjectExporter(client).ExportAsync(
            "source",
            1,
            TestContext.Current.CancellationToken);

        Assert.NotNull(snapshot.StatusUpdates);
        Assert.Equal(
            ["Newest update", "Middle update", "Oldest update"],
            snapshot.StatusUpdates.Select(update => update.Body));
        Assert.Equal(
            ["COMPLETE", "AT_RISK", "INACTIVE"],
            snapshot.StatusUpdates.Select(update => update.Status));
        Assert.Equal(
            ["octocat", "hubot", "monalisa"],
            snapshot.StatusUpdates.Select(update => update.Creator));
        var newest = snapshot.StatusUpdates[0];
        Assert.Equal("2026-01-01", newest.StartDate);
        Assert.Equal("2026-04-15", newest.TargetDate);
        Assert.Equal("2026-01-05T09:00:00Z", newest.CreatedAt);
        Assert.Equal("2026-01-06T10:30:00Z", newest.UpdatedAt);

        // Reverse chronological order is the API's doing, not a client-side sort: the
        // query must ask for it explicitly or resume ordering silently changes.
        var statusUpdateRequest = Assert.Single(
            handler.RequestBodies,
            body => body.Contains("statusUpdates(first: $first", StringComparison.Ordinal));
        Assert.Contains(
            "orderBy: { field: CREATED_AT, direction: DESC }",
            statusUpdateRequest,
            StringComparison.Ordinal);
        Assert.True(
            snapshot.StatusUpdates
                .Select(update => DateTimeOffset.Parse(update.CreatedAt, CultureInfo.InvariantCulture))
                .SequenceEqual(
                    snapshot.StatusUpdates
                        .Select(update => DateTimeOffset.Parse(update.CreatedAt, CultureInfo.InvariantCulture))
                        .OrderByDescending(createdAt => createdAt)),
            "Exported status updates must stay newest-first.");
    }

    [Fact]
    public async Task Export_paginates_status_updates()
    {
        using var handler = new StubHandler(
            MetadataResponse("[]"),
            EmptyItemsResponse,
            StatusUpdatesResponse(
                """
                [{"body":"Page one","status":"ON_TRACK","startDate":null,"targetDate":null,
                  "creator":{"login":"octocat"},"createdAt":"2026-01-05T09:00:00Z","updatedAt":"2026-01-05T09:00:00Z"}]
                """,
                hasNextPage: true,
                endCursor: "c1"),
            StatusUpdatesResponse(
                """
                [{"body":"Page two","status":"OFF_TRACK","startDate":null,"targetDate":null,
                  "creator":{"login":"hubot"},"createdAt":"2026-01-04T09:00:00Z","updatedAt":"2026-01-04T09:00:00Z"}]
                """),
            FieldsResponse("[]"));
        using var client = CreateClient(handler);

        var snapshot = await new ProjectExporter(client).ExportAsync(
            "source",
            1,
            TestContext.Current.CancellationToken);

        Assert.NotNull(snapshot.StatusUpdates);
        Assert.Equal(["Page one", "Page two"], snapshot.StatusUpdates.Select(update => update.Body));
        Assert.Equal(["ON_TRACK", "OFF_TRACK"], snapshot.StatusUpdates.Select(update => update.Status));
        Assert.Equal(6, handler.RequestBodies.Count);
        Assert.Contains("\"after\":\"c1\"", handler.RequestBodies[3], StringComparison.Ordinal);
        Assert.Contains("\"first\":50", handler.RequestBodies[3], StringComparison.Ordinal);
        Assert.Contains("\"after\":null", handler.RequestBodies[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_leaves_optional_status_update_dates_and_creator_null()
    {
        using var handler = new StubHandler(
            MetadataResponse("[]"),
            EmptyItemsResponse,
            StatusUpdatesResponse(
                """
                [{"body":"No metadata","status":null,"startDate":null,"targetDate":null,
                  "createdAt":"2026-01-05T09:00:00Z","updatedAt":"2026-01-05T09:00:00Z"}]
                """),
            FieldsResponse("[]"));
        using var client = CreateClient(handler);

        var snapshot = await new ProjectExporter(client).ExportAsync(
            "source",
            1,
            TestContext.Current.CancellationToken);

        var update = Assert.Single(snapshot.StatusUpdates!);
        Assert.Null(update.Status);
        Assert.Null(update.StartDate);
        Assert.Null(update.TargetDate);
        Assert.Null(update.Creator);
        Assert.Equal("No metadata", update.Body);
        Assert.Equal("2026-01-05T09:00:00Z", update.CreatedAt);
    }

    [Fact]
    public async Task Export_sets_an_empty_status_update_list_when_the_project_has_none()
    {
        using var handler = new StubHandler(
            MetadataResponse("[]"),
            EmptyItemsResponse,
            EmptyStatusUpdatesResponse,
            FieldsResponse("[]"));
        using var client = CreateClient(handler);

        var snapshot = await new ProjectExporter(client).ExportAsync(
            "source",
            1,
            TestContext.Current.CancellationToken);

        // Null means "this snapshot predates status update capture"; the API path must
        // never produce that, otherwise verify would silently skip the category.
        Assert.NotNull(snapshot.StatusUpdates);
        Assert.Empty(snapshot.StatusUpdates);
    }

    [Fact]
    public async Task Export_requests_status_updates_after_items_and_before_fields()
    {
        using var handler = new StubHandler(
            MetadataResponse("[]"),
            EmptyItemsResponse,
            EmptyStatusUpdatesResponse,
            FieldsResponse("[]"));
        using var client = CreateClient(handler);

        await new ProjectExporter(client).ExportAsync(
            "source",
            1,
            TestContext.Current.CancellationToken);

        var items = handler.RequestBodies.FindIndex(
            body => body.Contains("items(first: $first", StringComparison.Ordinal));
        var statusUpdates = handler.RequestBodies.FindIndex(
            body => body.Contains("statusUpdates(first: $first", StringComparison.Ordinal));
        var fields = handler.RequestBodies.FindIndex(
            body => body.Contains("fields(first: $first", StringComparison.Ordinal));

        Assert.Equal(1, items);
        Assert.Equal(2, statusUpdates);
        Assert.Equal(3, fields);
    }

    private const string EmptyItemsResponse =
        """
        {"data":{"organization":{"projectV2":{"items":{
          "nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}
        }}}}}
        """;

    private const string EmptyStatusUpdatesResponse =
        """
        {"data":{"organization":{"projectV2":{"statusUpdates":{
          "nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}
        }}}}}
        """;

    private static string StatusUpdatesResponse(
        string nodes,
        bool hasNextPage = false,
        string? endCursor = null) =>
        "{\"data\":{\"organization\":{\"projectV2\":{\"statusUpdates\":{\"nodes\":" + nodes +
        ",\"pageInfo\":{\"hasNextPage\":" + hasNextPage.ToString().ToLowerInvariant() +
        ",\"endCursor\":" + (endCursor is null ? "null" : $"\"{endCursor}\"") + "}}}}}}";

    private static string MetadataResponse(
        string views,
        bool template = false,
        bool hasNextPage = false,
        string? endCursor = null) =>
        "{\"data\":{\"organization\":{\"projectV2\":{" +
        "\"title\":\"Roadmap\",\"shortDescription\":null,\"readme\":null,\"public\":false,\"closed\":false," +
        "\"template\":" + template.ToString().ToLowerInvariant() + "," +
        "\"views\":{\"nodes\":" + views +
        ",\"pageInfo\":{\"hasNextPage\":" + hasNextPage.ToString().ToLowerInvariant() +
        ",\"endCursor\":" + (endCursor is null ? "null" : $"\"{endCursor}\"") + "}}," +
        "\"workflows\":{\"nodes\":[]},\"repositories\":{\"nodes\":[]}" +
        "}}}}";

    private static string ViewsResponse(
        string views,
        bool hasNextPage = false,
        string? endCursor = null) =>
        "{\"data\":{\"organization\":{\"projectV2\":{\"views\":{\"nodes\":" + views +
        ",\"pageInfo\":{\"hasNextPage\":" + hasNextPage.ToString().ToLowerInvariant() +
        ",\"endCursor\":" + (endCursor is null ? "null" : $"\"{endCursor}\"") + "}}}}}}";

    private static string FieldsResponse(
        string fields,
        bool hasNextPage = false,
        string? endCursor = null) =>
        "{\"data\":{\"organization\":{\"projectV2\":{\"fields\":{\"nodes\":" + fields +
        ",\"pageInfo\":{\"hasNextPage\":" + hasNextPage.ToString().ToLowerInvariant() +
        ",\"endCursor\":" + (endCursor is null ? "null" : $"\"{endCursor}\"") + "}}}}}}";

    private static string TeamsResponse(
        string teams,
        bool hasNextPage = false,
        string? endCursor = null) =>
        "{\"data\":{\"organization\":{\"projectV2\":{\"teams\":{\"nodes\":" + teams +
        ",\"pageInfo\":{\"hasNextPage\":" + hasNextPage.ToString().ToLowerInvariant() +
        ",\"endCursor\":" + (endCursor is null ? "null" : $"\"{endCursor}\"") + "}}}}}}";

    private static GitHubGraphQLClient CreateClient(HttpMessageHandler handler) =>
        new(
            "dummy-token",
            new Uri("https://example.test/graphql"),
            handler,
            delayAsync: static (_, _) => Task.CompletedTask);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;
        private readonly Queue<string> _teamResponses;

        public StubHandler(params string[] responses)
            : this([], responses)
        {
        }

        public StubHandler(IReadOnlyList<string> teamResponses, params string[] responses)
        {
            _responses = new Queue<string>(responses);
            _teamResponses = new Queue<string>(teamResponses);
        }

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(body);
            var response = body.Contains("teams(first:", StringComparison.Ordinal)
                ? _teamResponses.Count > 0
                    ? _teamResponses.Dequeue()
                    : TeamsResponse("[]")
                : _responses.Dequeue();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }
}
