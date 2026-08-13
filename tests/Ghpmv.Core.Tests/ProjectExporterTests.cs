using System.Net;
using System.Text;
using Ghpmv.Core.Export;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Tests;

public class ProjectExporterTests
{
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
        Assert.Equal(3, handler.RequestBodies.Count);
        Assert.Contains("isIssueField", handler.RequestBodies[2], StringComparison.Ordinal);
        Assert.Contains("issueField", handler.RequestBodies[2], StringComparison.Ordinal);
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
        Assert.Equal(4, handler.RequestBodies.Count);
        Assert.Contains("\"after\":\"field-cursor\"", handler.RequestBodies[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_identifies_unset_linked_issue_field_without_item_value_evidence()
    {
        using var handler = new StubHandler(
            MetadataResponse("[]"),
            EmptyItemsResponse,
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
        Assert.Equal(6, handler.RequestBodies.Count);
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

    private const string EmptyItemsResponse =
        """
        {"data":{"organization":{"projectV2":{"items":{
          "nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}
        }}}}}
        """;

    private static string MetadataResponse(string views) =>
        "{\"data\":{\"organization\":{\"projectV2\":{" +
        "\"title\":\"Roadmap\",\"shortDescription\":null,\"readme\":null,\"public\":false,\"closed\":false," +
        "\"views\":{\"nodes\":" + views + "},\"workflows\":{\"nodes\":[]},\"repositories\":{\"nodes\":[]}" +
        "}}}}";

    private static string FieldsResponse(
        string fields,
        bool hasNextPage = false,
        string? endCursor = null) =>
        "{\"data\":{\"organization\":{\"projectV2\":{\"fields\":{\"nodes\":" + fields +
        ",\"pageInfo\":{\"hasNextPage\":" + hasNextPage.ToString().ToLowerInvariant() +
        ",\"endCursor\":" + (endCursor is null ? "null" : $"\"{endCursor}\"") + "}}}}}}";

    private static GitHubGraphQLClient CreateClient(HttpMessageHandler handler) =>
        new(
            "dummy-token",
            new Uri("https://example.test/graphql"),
            handler,
            delayAsync: static (_, _) => Task.CompletedTask);

    private sealed class StubHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json"),
            };
        }
    }
}
