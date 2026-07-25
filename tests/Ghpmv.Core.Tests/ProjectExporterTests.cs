using System.Net;
using System.Text;
using Ghpmv.Core.Export;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Tests;

public class ProjectExporterTests
{
    [Fact]
    public async Task Export_reads_projected_multi_select_issue_fields()
    {
        using var handler = new StubHandler(
            """
            {"data":{"organization":{"projectV2":{
              "title":"Roadmap","shortDescription":null,"readme":null,"public":false,"closed":false,
              "views":{"nodes":[]},"workflows":{"nodes":[]},"repositories":{"nodes":[]}
            }}}}
            """,
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
                  },{
                      "__typename":"ProjectV2ItemIssueFieldValue",
                     "field":{"name":"Priority"},
                     "issueFieldValue":{"__typename":"IssueFieldSingleSelectValue","name":"High"}
                  }
                ]}
              }],
              "pageInfo":{"hasNextPage":false,"endCursor":null}
            }}}}}
            """,
            """
            {"data":{"organization":{"projectV2":{"fields":{"nodes":[
              {"__typename":"ProjectV2Field","id":"PVTF_title","name":"Title"},
              {"__typename":"ProjectV2Field","id":"PVTF_unrelated","name":"Unrelated"},
              {"__typename":"ProjectV2Field","id":"PVTF_notes","name":"Notes"},
              {"__typename":"ProjectV2Field","id":"PVTF_teams","name":"Teams"}
            ]}}}}}
            """,
            """
            {"data":{"organization":{"issueFields":{
              "nodes":[
                {
                  "__typename":"IssueFieldMultiSelect","id":"IFM_teams","name":"Teams",
                  "dataType":"MULTI_SELECT","description":"Teams involved","visibility":"ALL",
                  "options":[
                    {"id":"IFO_platform","name":"Platform","color":"PURPLE","description":"Platform work"},
                    {"id":"IFO_sdk","name":"SDK","color":"GREEN","description":null}
                  ]
                },
                {
                  "__typename":"IssueFieldMultiSelect","id":"IFM_unrelated","name":"Unrelated",
                  "dataType":"MULTI_SELECT","description":null,"visibility":"ALL","options":[]
                },
                {
                  "__typename":"IssueFieldText","id":"IFT_notes","name":"Notes",
                  "dataType":"TEXT","description":"Review notes","visibility":"ALL"
                }
              ],
              "pageInfo":{"hasNextPage":false,"endCursor":null}
            }}}}
            """,
            """
            {"data":{"nodes":[
              {"id":"PVTF_title","dataType":"TITLE"},
              {"id":"PVTF_notes","dataType":"TEXT"}
            ]}}
            """,
            """
            {"data":{"nodes":[
              {"id":"PVTF_unrelated","dataType":"TEXT"}
            ]}}
            """,
            """
            {"data":{"nodes":[null]},"errors":[
              {"message":"Something went wrong while executing your query on the preview API."}
            ]}
            """,
            """
            {"data":{"nodes":[null]},"errors":[
              {"message":"Something went wrong while executing your query on the preview API."}
            ]}
            """,
            """
            {"data":{"nodes":[null]},"errors":[
              {"message":"Something went wrong while executing your query on the preview API."}
            ]}
            """,
            """
            {"data":{"nodes":[null]},"errors":[
              {"message":"Something went wrong while executing your query on the preview API."}
            ]}
            """);
        using var client = new GitHubGraphQLClient(
            "dummy-token",
            new Uri("https://example.test/graphql"),
            handler,
            delayAsync: null);

        var snapshot = await new ProjectExporter(client).ExportAsync(
            "source",
            1,
            TestContext.Current.CancellationToken);

        var field = snapshot.Fields.Single(candidate => candidate.Name == "Teams");
        Assert.Equal("MULTI_SELECT", field.DataType);
        Assert.Equal(["Platform", "SDK"], field.Options!.Select(option => option.Name));
        Assert.Equal("Teams involved", field.IssueField!.Description);
        Assert.Equal("ALL", field.IssueField.Visibility);
        var unrelated = snapshot.Fields.Single(candidate => candidate.Name == "Unrelated");
        Assert.Equal("TEXT", unrelated.DataType);
        Assert.Null(unrelated.IssueField);
        var notes = snapshot.Fields.Single(candidate => candidate.Name == "Notes");
        Assert.Equal("TEXT", notes.DataType);
        Assert.Equal("Review notes", notes.IssueField!.Description);

        var item = Assert.Single(snapshot.Items);
        Assert.Equal(
            ["Platform", "SDK"],
            item.FieldValues.Single(value => value.FieldName == "Teams").MultiSelectOptionNames);
        Assert.Equal(
            "Needs review",
            item.FieldValues.Single(value => value is { FieldName: "Notes", IsIssueField: true }).Text);
        Assert.Equal(
            "Project note",
            item.FieldValues.Single(value => value is { FieldName: "Notes", IsIssueField: false }).Text);
        Assert.Equal("High", item.FieldValues.Single(value => value.FieldName == "Priority").SingleSelectOptionName);
        Assert.Equal(10, handler.RequestBodies.Count);
        Assert.DoesNotContain("dataType", handler.RequestBodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_without_issue_fields_does_not_read_the_organization_catalog()
    {
        using var handler = new StubHandler(
            """
            {"data":{"organization":{"projectV2":{
              "title":"Roadmap","shortDescription":null,"readme":null,"public":false,"closed":false,
              "views":{"nodes":[]},"workflows":{"nodes":[]},"repositories":{"nodes":[]}
            }}}}
            """,
            """
            {"data":{"organization":{"projectV2":{"items":{
              "nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}
            }}}}}
            """,
            """
            {"data":{"organization":{"projectV2":{"fields":{"nodes":[
              {"__typename":"ProjectV2Field","id":"PVTF_notes","name":"Notes"}
            ]}}}}}
            """,
            """
            {"data":{"nodes":[{"id":"PVTF_notes","dataType":"TEXT"}]}}
            """);
        using var client = new GitHubGraphQLClient(
            "dummy-token",
            new Uri("https://example.test/graphql"),
            handler,
            delayAsync: null);

        var snapshot = await new ProjectExporter(client).ExportAsync(
            "source",
            1,
            TestContext.Current.CancellationToken);

        Assert.Equal("TEXT", Assert.Single(snapshot.Fields).DataType);
        Assert.DoesNotContain(
            handler.RequestBodies,
            body => body.Contains("issueFields", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Export_resolves_untyped_linked_issue_field_without_an_item_value()
    {
        using var handler = new StubHandler(
            """
            {"data":{"organization":{"projectV2":{
              "title":"Roadmap","shortDescription":null,"readme":null,"public":false,"closed":false,
              "views":{"nodes":[]},"workflows":{"nodes":[]},"repositories":{"nodes":[]}
            }}}}
            """,
            """
            {"data":{"organization":{"projectV2":{"items":{
              "nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}
            }}}}}
            """,
            """
            {"data":{"organization":{"projectV2":{"fields":{"nodes":[
              {"__typename":"ProjectV2Field","id":"PVTF_title","name":"Title"},
              {"__typename":"ProjectV2Field","id":"PVTF_teams","name":"Teams"}
            ]}}}}}
            """,
            """
            {"data":{"nodes":[{"id":"PVTF_title","dataType":"TITLE"},null]}}
            """,
            """
            {"data":{"organization":{"issueFields":{
              "nodes":[{
                "__typename":"IssueFieldMultiSelect","id":"IFM_teams","name":"Teams",
                "dataType":"MULTI_SELECT","description":"Teams involved","visibility":"ALL",
                "options":[{"id":"IFO_sdk","name":"SDK","color":"GREEN","description":null}]
              }],
              "pageInfo":{"hasNextPage":false,"endCursor":null}
            }}}}
            """,
            """
            {"data":{"nodes":[{"id":"PVTF_title","dataType":"TITLE"}]}}
            """,
            """
            {"data":{"nodes":[null]},"errors":[
              {"message":"Something went wrong while executing your query on the preview API."}
            ]}
            """,
            """
            {"data":{"nodes":[null]},"errors":[
              {"message":"Something went wrong while executing your query on the preview API."}
            ]}
            """,
            """
            {"data":{"nodes":[null]},"errors":[
              {"message":"Something went wrong while executing your query on the preview API."}
            ]}
            """,
            """
            {"data":{"nodes":[null]},"errors":[
              {"message":"Something went wrong while executing your query on the preview API."}
            ]}
            """);
        using var client = new GitHubGraphQLClient(
            "dummy-token",
            new Uri("https://example.test/graphql"),
            handler,
            delayAsync: null);

        var snapshot = await new ProjectExporter(client).ExportAsync(
            "source",
            1,
            TestContext.Current.CancellationToken);

        var teams = snapshot.Fields.Single(field => field.Name == "Teams");
        Assert.Equal("MULTI_SELECT", teams.DataType);
        Assert.Equal(["SDK"], teams.Options!.Select(option => option.Name));
        Assert.Equal(10, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task Export_fails_instead_of_writing_an_incomplete_snapshot_when_field_enumeration_fails()
    {
        using var handler = new StubHandler(
            """
            {"data":{"organization":{"projectV2":{
              "title":"Roadmap","shortDescription":null,"readme":null,"public":false,"closed":false,
              "views":{"nodes":[]},"workflows":{"nodes":[]},"repositories":{"nodes":[]}
            }}}}
            """,
            """
            {"data":{"organization":{"projectV2":{"items":{
              "nodes":[],
              "pageInfo":{"hasNextPage":false,"endCursor":null}
            }}}}}
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
            """,
            """
            {"data":{"organization":{"projectV2":{"fields":null}}},"errors":[
              {"message":"Something went wrong while executing your query on the preview API."}
            ]}
            """);
        using var client = new GitHubGraphQLClient(
            "dummy-token",
            new Uri("https://example.test/graphql"),
            handler,
            delayAsync: static (_, _) => Task.CompletedTask);

        var exception = await Assert.ThrowsAsync<GitHubGraphQLException>(() =>
            new ProjectExporter(client).ExportAsync(
                "source",
                1,
                TestContext.Current.CancellationToken));

        Assert.Contains("No snapshot was written", exception.Message, StringComparison.Ordinal);
        Assert.Contains("--enable-browser-automation", exception.Message, StringComparison.Ordinal);
        Assert.Equal(6, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task Export_uses_the_complete_catalog_without_querying_the_project_field_connection()
    {
        using var handler = new StubHandler(
            """
            {"data":{"organization":{"projectV2":{
              "title":"Roadmap","shortDescription":null,"readme":null,"public":false,"closed":false,
              "views":{"nodes":[{
                "number":3,"name":"All","layout":"TABLE_LAYOUT","filter":null,
                "groupByFields":{"nodes":[]},"verticalGroupByFields":{"nodes":[]},
                "sortByFields":{"nodes":[]},"fields":{"nodes":[]}
              }]},"workflows":{"nodes":[]},"repositories":{"nodes":[]}
            }}}}
            """,
            """
            {"data":{"organization":{"projectV2":{"items":{
              "nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}
            }}}}}
            """,
            """
            {"data":{"organization":{"issueFields":{"nodes":[
              {"__typename":"IssueFieldMultiSelect","id":"IFM_teams","name":"Teams",
               "dataType":"MULTI_SELECT","description":"Teams involved","visibility":"ALL",
               "options":[{"id":"IFO_sdk","name":"SDK","color":"GREEN","description":null}]}
            ],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
            """);
        using var client = new GitHubGraphQLClient(
            "dummy-token",
            new Uri("https://example.test/graphql"),
            handler,
            delayAsync: static (_, _) => Task.CompletedTask);
        int? requestedView = null;
        var catalog = new ProjectFieldCatalog
        {
            Entries =
            [
                new(new FieldSnapshot { Name = "Title", DataType = "TITLE" }, false),
                new(new FieldSnapshot { Name = "Hidden", DataType = "TEXT" }, false),
                new(new FieldSnapshot { Name = "Teams", DataType = "TEXT" }, false),
                new(
                    new FieldSnapshot
                    {
                        Name = "Teams",
                        DataType = "MULTI_SELECT",
                        Options = [],
                    },
                    true),
            ],
        };

        var snapshot = await new ProjectExporter(client)
        {
            CompleteFieldCatalogProviderAsync = (viewNumber, _) =>
            {
                requestedView = viewNumber;
                return Task.FromResult(catalog);
            },
        }.ExportAsync(
            "source",
            1,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, requestedView);
        Assert.Equal(["Title", "Hidden", "Teams", "Teams"], snapshot.Fields.Select(field => field.Name));
        var ordinaryTeams = snapshot.Fields.Single(field => field.Name == "Teams" && field.IssueField is null);
        Assert.Equal("TEXT", ordinaryTeams.DataType);
        var linkedTeams = snapshot.Fields.Single(field => field.Name == "Teams" && field.IssueField is not null);
        Assert.Equal("Teams involved", linkedTeams.IssueField!.Description);
        Assert.Equal(["SDK"], linkedTeams.Options!.Select(option => option.Name));
        Assert.Equal(3, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task Export_rejects_item_issue_fields_not_linked_by_the_complete_catalog()
    {
        using var handler = new StubHandler(
            """
            {"data":{"organization":{"projectV2":{
              "title":"Roadmap","shortDescription":null,"readme":null,"public":false,"closed":false,
              "views":{"nodes":[{
                "number":3,"name":"All","layout":"TABLE_LAYOUT","filter":null,
                "groupByFields":{"nodes":[]},"verticalGroupByFields":{"nodes":[]},
                "sortByFields":{"nodes":[]},"fields":{"nodes":[]}
              }]},"workflows":{"nodes":[]},"repositories":{"nodes":[]}
            }}}}
            """,
            """
            {"data":{"organization":{"projectV2":{"items":{
              "nodes":[{
                "type":"ISSUE","isArchived":false,
                "content":{"number":7,"repository":{"nameWithOwner":"source/repo"}},
                "fieldValues":{"nodes":[{
                  "__typename":"ProjectV2ItemIssueFieldValue",
                  "field":{"name":"Teams"},
                  "issueFieldValue":{
                    "__typename":"IssueFieldMultiSelectValue",
                    "options":[{"name":"SDK"}]
                  }
                }]}
              }],
              "pageInfo":{"hasNextPage":false,"endCursor":null}
            }}}}}
            """);
        using var client = new GitHubGraphQLClient(
            "dummy-token",
            new Uri("https://example.test/graphql"),
            handler,
            delayAsync: static (_, _) => Task.CompletedTask);
        var catalog = new ProjectFieldCatalog
        {
            Entries =
            [
                new(new FieldSnapshot { Name = "Title", DataType = "TITLE" }, false),
                new(new FieldSnapshot { Name = "Teams", DataType = "MULTI_SELECT", Options = [] }, false),
            ],
        };

        var exception = await Assert.ThrowsAsync<GitHubGraphQLException>(() =>
            new ProjectExporter(client)
            {
                CompleteFieldCatalogProviderAsync = (_, _) => Task.FromResult(catalog),
            }.ExportAsync(
                "source",
                1,
                TestContext.Current.CancellationToken));

        Assert.Contains("Teams", exception.Message, StringComparison.Ordinal);
        Assert.Contains("did not mark it as linked", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task Export_rejects_item_fields_missing_from_the_complete_catalog()
    {
        using var handler = new StubHandler(
            """
            {"data":{"organization":{"projectV2":{
              "title":"Roadmap","shortDescription":null,"readme":null,"public":false,"closed":false,
              "views":{"nodes":[{
                "number":3,"name":"All","layout":"TABLE_LAYOUT","filter":null,
                "groupByFields":{"nodes":[]},"verticalGroupByFields":{"nodes":[]},
                "sortByFields":{"nodes":[]},"fields":{"nodes":[]}
              }]},"workflows":{"nodes":[]},"repositories":{"nodes":[]}
            }}}}
            """,
            """
            {"data":{"organization":{"projectV2":{"items":{
              "nodes":[{
                "type":"DRAFT_ISSUE","isArchived":false,
                "content":{"title":"Draft","body":null,"creator":null,"createdAt":null,"assignees":{"nodes":[]}},
                "fieldValues":{"nodes":[{
                  "__typename":"ProjectV2ItemFieldTextValue",
                  "field":{"name":"Notes"},"text":"Observed"
                }]}
              }],
              "pageInfo":{"hasNextPage":false,"endCursor":null}
            }}}}}
            """);
        using var client = new GitHubGraphQLClient(
            "dummy-token",
            new Uri("https://example.test/graphql"),
            handler,
            delayAsync: static (_, _) => Task.CompletedTask);
        var catalog = new ProjectFieldCatalog
        {
            Entries = [new(new FieldSnapshot { Name = "Title", DataType = "TITLE" }, false)],
        };

        var exception = await Assert.ThrowsAsync<GitHubGraphQLException>(() =>
            new ProjectExporter(client)
            {
                CompleteFieldCatalogProviderAsync = (_, _) => Task.FromResult(catalog),
            }.ExportAsync("source", 1, TestContext.Current.CancellationToken));

        Assert.Contains("Notes", exception.Message, StringComparison.Ordinal);
        Assert.Contains("did not contain it", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task Export_rejects_view_fields_missing_from_the_complete_catalog()
    {
        using var handler = new StubHandler(
            """
            {"data":{"organization":{"projectV2":{
              "title":"Roadmap","shortDescription":null,"readme":null,"public":false,"closed":false,
              "views":{"nodes":[{
                "number":3,"name":"All","layout":"TABLE_LAYOUT","filter":null,
                "groupByFields":{"nodes":[]},"verticalGroupByFields":{"nodes":[]},
                "sortByFields":{"nodes":[]},"fields":{"nodes":[{"name":"Priority"}]}
              }]},"workflows":{"nodes":[]},"repositories":{"nodes":[]}
            }}}}
            """,
            """
            {"data":{"organization":{"projectV2":{"items":{
              "nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}
            }}}}}
            """);
        using var client = new GitHubGraphQLClient(
            "dummy-token",
            new Uri("https://example.test/graphql"),
            handler,
            delayAsync: static (_, _) => Task.CompletedTask);
        var catalog = new ProjectFieldCatalog
        {
            Entries = [new(new FieldSnapshot { Name = "Title", DataType = "TITLE" }, false)],
        };

        var exception = await Assert.ThrowsAsync<GitHubGraphQLException>(() =>
            new ProjectExporter(client)
            {
                CompleteFieldCatalogProviderAsync = (_, _) => Task.FromResult(catalog),
            }.ExportAsync("source", 1, TestContext.Current.CancellationToken));

        Assert.Contains("Priority", exception.Message, StringComparison.Ordinal);
        Assert.Contains("did not contain it", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.RequestBodies.Count);
    }

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
