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
            """
            {"data":{"organization":{"projectV2":{
              "title":"Roadmap","shortDescription":null,"readme":null,"public":false,"closed":false,
              "views":{"nodes":[{
                "number":3,"name":"All","layout":"TABLE_LAYOUT","filter":null,
                "groupByFields":{"nodes":[]},"verticalGroupByFields":{"nodes":[]},"sortByFields":{"nodes":[]},
                "configuration":{"visibleFields":{"nodes":[{"name":"Status"},{"name":"Title"}]}},
                "fields":{"nodes":[{"name":"Legacy field"}]}
              }]},"workflows":{"nodes":[]},"repositories":{"nodes":[]}
            }}}}
            """,
            """
            {"data":{"organization":{"projectV2":{"items":{
              "nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}
            }}}}}
            """,
            """
            {"data":{"organization":{"projectV2":{"fields":{"nodes":[
              {"__typename":"ProjectV2Field","id":"PVTF_title","name":"Title","dataType":"TITLE"},
              {"__typename":"ProjectV2SingleSelectField","id":"PVTSSF_status","name":"Status",
               "dataType":"SINGLE_SELECT","options":[
                 {"id":"todo","name":"Todo","color":"GRAY","description":null}
               ]}
            ]}}}}}
            """);
        using var client = new GitHubGraphQLClient(
            "dummy-token",
            new Uri("https://example.test/graphql"),
            handler,
            delayAsync: static (_, _) => Task.CompletedTask);
        var snapshot = await new ProjectExporter(client).ExportAsync(
            "source",
            1,
            TestContext.Current.CancellationToken);

        Assert.Equal(["Status", "Title"], Assert.Single(snapshot.Views).VisibleFields);
        Assert.Contains("configuration", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.Contains("visibleFields", handler.RequestBodies[0], StringComparison.Ordinal);
    }

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
                    "field":{"id":"PVTF_issue_teams","databaseId":201,"name":"Teams"},
                    "issueFieldValue":{
                      "__typename":"IssueFieldMultiSelectValue",
                      "options":[{"name":"Platform"},{"name":"SDK"}]
                    }
                  },{
                    "__typename":"ProjectV2ItemIssueFieldValue",
                    "field":{"id":"PVTF_issue_notes","databaseId":202,"name":"Notes"},
                    "issueFieldValue":{"__typename":"IssueFieldTextValue","value":"Needs review"}
                  },{
                     "__typename":"ProjectV2ItemFieldTextValue",
                     "field":{"name":"Notes"},
                     "text":"Project note"
                  },{
                      "__typename":"ProjectV2ItemIssueFieldValue",
                     "field":{"id":"PVTF_issue_priority","databaseId":203,"name":"Priority"},
                     "issueFieldValue":{"__typename":"IssueFieldSingleSelectValue","name":"High"}
                  }
                ]}
              }],
              "pageInfo":{"hasNextPage":false,"endCursor":null}
            }}}}}
            """,
            """
            {"data":{"organization":{"projectV2":{"fields":{"nodes":[
              {"__typename":"ProjectV2Field","id":"PVTF_title","name":"Title","dataType":"TITLE"},
              {"__typename":"ProjectV2Field","id":"PVTF_unrelated","name":"Unrelated","dataType":"TEXT"},
              {"__typename":"ProjectV2Field","id":"PVTF_notes","databaseId":102,"name":"Notes","dataType":"TEXT"},
              {"__typename":"ProjectV2Field","id":"PVTF_issue_notes","databaseId":202,"name":"Notes","dataType":"TEXT"},
              {"__typename":"ProjectV2MultiSelectField","id":"PVTMSF_teams","databaseId":201,"name":"Teams",
               "dataType":"MULTI_SELECT","multiSelectOptions":[]},
              {"__typename":"ProjectV2SingleSelectField","id":"PVTSSF_priority","databaseId":203,"name":"Priority",
               "dataType":"SINGLE_SELECT","options":[]}
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
                },
                {
                  "__typename":"IssueFieldSingleSelect","id":"IFSS_priority","name":"Priority",
                  "dataType":"SINGLE_SELECT","description":null,"visibility":"ALL",
                  "options":[{"id":"IFO_high","name":"High","color":"RED","description":null}]
                }
              ],
              "pageInfo":{"hasNextPage":false,"endCursor":null}
            }}}}
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
        var notes = snapshot.Fields.Single(candidate => candidate.Name == "Notes" && candidate.IssueField is not null);
        Assert.Equal("TEXT", notes.DataType);
        Assert.Equal("Review notes", notes.IssueField!.Description);
        Assert.Contains(snapshot.Fields, candidate => candidate.Name == "Notes" && candidate.IssueField is null);

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
        Assert.Equal(4, handler.RequestBodies.Count);
        Assert.Contains("dataType", handler.RequestBodies[2], StringComparison.Ordinal);
        Assert.Contains("multiSelectOptions", handler.RequestBodies[2], StringComparison.Ordinal);
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
              {"__typename":"ProjectV2Field","id":"PVTF_notes","name":"Notes","dataType":"TEXT"}
            ]}}}}}
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
    public async Task Export_does_not_guess_unobserved_linked_issue_field_identity()
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
              {"__typename":"ProjectV2Field","id":"PVTF_title","name":"Title","dataType":"TITLE"},
              {"__typename":"ProjectV2MultiSelectField","id":"PVTMSF_teams","name":"Teams",
               "dataType":"MULTI_SELECT","multiSelectOptions":[]}
            ]}}}}}
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
        Assert.Empty(teams.Options!);
        Assert.Null(teams.IssueField);
        Assert.Equal(3, handler.RequestBodies.Count);
        Assert.DoesNotContain(
            handler.RequestBodies,
            body => body.Contains("issueFields", StringComparison.Ordinal));
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

    [Theory]
    [InlineData("MULTI_SELECT", false, true)]
    [InlineData("TEXT", false, false)]
    [InlineData("MULTI_SELECT", true, false)]
    public async Task Export_enriches_only_type_matching_linked_catalog_entries(
        string linkedDataType,
        bool duplicateOrdinaryField,
        bool shouldSucceed)
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
        var entries = new List<ProjectFieldCatalogEntry>
        {
            new(new FieldSnapshot { Name = "Title", DataType = "TITLE" }, false),
            new(new FieldSnapshot { Name = "Hidden", DataType = "TEXT" }, false),
            new(new FieldSnapshot { Name = "Teams", DataType = "TEXT" }, false),
            new(
                new FieldSnapshot
                {
                    Name = "Teams",
                    DataType = linkedDataType,
                    Options = [],
                },
                true),
        };
        if (duplicateOrdinaryField)
        {
            entries.Add(new(new FieldSnapshot { Name = "Teams", DataType = "NUMBER" }, false));
        }

        var catalog = new ProjectFieldCatalog { Entries = entries };

        var exporter = new ProjectExporter(client)
        {
            CompleteFieldCatalogProviderAsync = (viewNumber, _) =>
            {
                requestedView = viewNumber;
                return Task.FromResult(catalog);
            },
        };
        if (!shouldSucceed)
        {
            var exception = await Assert.ThrowsAsync<GitHubGraphQLException>(() => exporter.ExportAsync(
                "source",
                1,
                TestContext.Current.CancellationToken));

            Assert.Contains(
                duplicateOrdinaryField
                    ? "duplicate field identity 'Teams' (ordinary)"
                    : "organization Issue Field catalog reported MULTI_SELECT",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Equal(duplicateOrdinaryField ? 2 : 3, handler.RequestBodies.Count);
            return;
        }

        var snapshot = await exporter.ExportAsync(
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
    public async Task Browser_assisted_export_uses_catalog_when_linked_field_database_id_is_null()
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
                  "field":{"id":"PVTF_issue_teams","databaseId":null,"name":"Teams"},
                  "issueFieldValue":{
                    "__typename":"IssueFieldMultiSelectValue",
                    "options":[{"name":"SDK"}]
                  }
                }]}
              }],
              "pageInfo":{"hasNextPage":false,"endCursor":null}
            }}}}}
            """,
            """
            {"data":{"organization":{"issueFields":{"nodes":[{
              "__typename":"IssueFieldMultiSelect","id":"IFM_teams","name":"Teams",
              "dataType":"MULTI_SELECT","description":"Teams involved","visibility":"ALL",
              "options":[{"id":"IFO_sdk","name":"SDK","color":"GREEN","description":null}]
            }],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
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
                new(new FieldSnapshot { Name = "Teams", DataType = "MULTI_SELECT", Options = [] }, true),
            ],
        };

        var snapshot = await new ProjectExporter(client)
        {
            CompleteFieldCatalogProviderAsync = (_, _) => Task.FromResult(catalog),
        }.ExportAsync("source", 1, TestContext.Current.CancellationToken);

        var teams = snapshot.Fields.Single(field => field.Name == "Teams");
        Assert.NotNull(teams.IssueField);
        var itemValue = Assert.Single(Assert.Single(snapshot.Items).FieldValues);
        Assert.True(itemValue.IsIssueField);
        Assert.Equal(["SDK"], itemValue.MultiSelectOptionNames);
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
                  "field":{"id":"PVTF_issue_teams","databaseId":201,"name":"Teams"},
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
        Assert.Contains("linked field", exception.Message, StringComparison.Ordinal);
        Assert.Contains("did not contain that identity", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task Export_rejects_item_field_identities_missing_from_the_complete_catalog()
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
            Entries =
            [
                new(new FieldSnapshot { Name = "Title", DataType = "TITLE" }, false),
                new(new FieldSnapshot { Name = "Notes", DataType = "TEXT" }, true),
            ],
        };

        var exception = await Assert.ThrowsAsync<GitHubGraphQLException>(() =>
            new ProjectExporter(client)
            {
                CompleteFieldCatalogProviderAsync = (_, _) => Task.FromResult(catalog),
            }.ExportAsync("source", 1, TestContext.Current.CancellationToken));

        Assert.Contains("Notes", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ordinary", exception.Message, StringComparison.Ordinal);
        Assert.Contains("did not contain that identity", exception.Message, StringComparison.Ordinal);
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

    [Fact]
    public async Task Export_reads_ordinary_multi_select_field_definitions_and_item_values()
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
            """
            {"data":{"organization":{"projectV2":{"fields":{"nodes":[
              {"__typename":"ProjectV2Field","id":"PVTF_title","name":"Title","dataType":"TITLE"},
              {"__typename":"ProjectV2MultiSelectField","id":"PVTMSF_areas","name":"Areas",
               "dataType":"MULTI_SELECT","multiSelectOptions":[
                 {"id":"PVTMSFO_platform","name":"Platform","color":"PURPLE","description":"Platform work"},
                 {"id":"PVTMSFO_sdk","name":"SDK","color":"GREEN","description":null}
               ]}
            ]}}}}}
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

        var field = snapshot.Fields.Single(candidate => candidate.Name == "Areas");
        Assert.Equal("MULTI_SELECT", field.DataType);
        Assert.Null(field.IssueField);
        Assert.Equal(
            [
                ("PVTMSFO_platform", "Platform", "PURPLE", "Platform work"),
                ("PVTMSFO_sdk", "SDK", "GREEN", null),
            ],
            field.Options!.Select(option => (option.Id, option.Name, option.Color, option.Description)));

        var value = Assert.Single(Assert.Single(snapshot.Items).FieldValues);
        Assert.Equal("Areas", value.FieldName);
        Assert.Equal(false, value.IsIssueField);
        Assert.Equal(["Platform", "SDK"], value.MultiSelectOptionNames);

        Assert.Equal(3, handler.RequestBodies.Count);
        Assert.Contains("ProjectV2ItemFieldMultiSelectValue", handler.RequestBodies[1], StringComparison.Ordinal);
        Assert.Contains("options { name }", handler.RequestBodies[1], StringComparison.Ordinal);
        Assert.Contains("ProjectV2MultiSelectField", handler.RequestBodies[2], StringComparison.Ordinal);
        Assert.Contains("multiSelectOptions", handler.RequestBodies[2], StringComparison.Ordinal);
        Assert.DoesNotContain(
            handler.RequestBodies,
            body => body.Contains("issueFields", StringComparison.Ordinal));
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
