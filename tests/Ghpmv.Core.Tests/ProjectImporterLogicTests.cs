using System.Net;
using System.Text;
using System.Text.Json;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Tests;

public class ProjectImporterLogicTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    public void Visibility_update_is_only_required_when_the_value_changes(
        bool currentPublic,
        bool desiredPublic,
        bool expected)
        => Assert.Equal(expected, ProjectImporter.ShouldUpdateVisibility(currentPublic, desiredPublic));

    [Fact]
    public async Task Conflict_skip_returns_skipped_without_sending_mutations()
    {
        const string response =
            """
            {"data":{"organization":{"projectsV2":{
              "nodes":[{"id":"PVT_existing","number":42,"title":"Roadmap","url":"https://github.com/orgs/target/projects/42"}],
              "pageInfo":{"hasNextPage":false,"endCursor":null}
            }}}}
            """;
        using var handler = new StubHandler(response);
        using var client = new GitHubGraphQLClient(
            "dummy-token",
            new Uri("https://example.test/graphql"),
            handler,
            delayAsync: null);
        var importer = new ProjectImporter(client)
        {
            OnConflict = ConflictAction.Skip,
            OperationLogDirectory = Path.Combine(Path.GetTempPath(), $"ghpmv-project-import-{Guid.NewGuid():N}"),
        };

        var result = await importer.ImportAsync(
            MinimalSnapshot("Roadmap"),
            "target",
            TestContext.Current.CancellationToken);

        Assert.Equal(ProjectImportOutcome.Skipped, result.Outcome);
        Assert.False(result.Created);
        Assert.Equal(42, result.ProjectNumber);
        Assert.Empty(result.FieldIds);
        var request = Assert.Single(handler.RequestBodies);
        using var document = JsonDocument.Parse(request);
        Assert.DoesNotContain(
            "mutation",
            document.RootElement.GetProperty("query").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Conflict_update_runs_prewrite_hook_before_sending_mutations()
    {
        const string response =
            """
            {"data":{"organization":{"projectsV2":{
              "nodes":[{"id":"PVT_existing","number":42,"title":"Roadmap","url":"https://github.com/orgs/target/projects/42"}],
              "pageInfo":{"hasNextPage":false,"endCursor":null}
            }}}}
            """;
        using var handler = new StubHandler(response);
        using var client = new GitHubGraphQLClient(
            "dummy-token",
            new Uri("https://example.test/graphql"),
            handler,
            delayAsync: null);
        var importer = new ProjectImporter(client)
        {
            OnConflict = ConflictAction.Update,
            BeforeWriteAsync = _ => throw new InvalidOperationException("authentication failed"),
            OperationLogDirectory = Path.Combine(Path.GetTempPath(), $"ghpmv-project-import-{Guid.NewGuid():N}"),
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => importer.ImportAsync(
                MinimalSnapshot("Roadmap"),
                "target",
                TestContext.Current.CancellationToken));

        Assert.Equal("authentication failed", exception.Message);
        var request = Assert.Single(handler.RequestBodies);
        using var document = JsonDocument.Parse(request);
        Assert.DoesNotContain(
            "mutation",
            document.RootElement.GetProperty("query").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_creates_and_links_multi_select_issue_field()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-project-import-").FullName;
        try
        {
            using var handler = new IssueFieldStubHandler();
            using var client = new GitHubGraphQLClient(
                "dummy-token",
                new Uri("https://example.test/graphql"),
                handler,
                delayAsync: null);
            var snapshot = MinimalSnapshot("Roadmap") with
            {
                Project = MinimalSnapshot("Roadmap").Project with
                {
                    ShortDescription = null,
                    Readme = null,
                    Public = false,
                    Closed = false,
                },
                Fields =
                [
                    new FieldSnapshot
                    {
                        Name = "Teams",
                        DataType = "MULTI_SELECT",
                        Options =
                        [
                            new SingleSelectOptionSnapshot { Id = "source-platform", Name = "Platform", Color = "PURPLE" },
                            new SingleSelectOptionSnapshot { Id = "source-sdk", Name = "SDK", Color = "GREEN" },
                        ],
                        IssueField = new IssueFieldConfigurationSnapshot
                        {
                            Description = "Teams involved",
                            Visibility = "ALL",
                        },
                    },
                ],
            };
            var importer = new ProjectImporter(client)
            {
                OperationLogDirectory = directory,
            };

            var result = await importer.ImportIntoAsync(
                snapshot,
                "target",
                7,
                TestContext.Current.CancellationToken);

            Assert.Equal("IFM_teams", result.IssueFieldIds["Teams"]);
            Assert.Equal("IFO_platform", result.IssueFieldOptionIds["Teams"]["Platform"]);
            Assert.Equal("IFO_sdk", result.IssueFieldOptionIds["Teams"]["SDK"]);
            Assert.False(result.FieldIds.ContainsKey("Teams"));
            Assert.Contains(handler.RequestBodies, body => body.Contains("createIssueField", StringComparison.Ordinal));
            var linkMutation = Assert.Single(
                handler.RequestBodies,
                body => body.Contains("createProjectV2IssueField", StringComparison.Ordinal));
            Assert.Contains("clientMutationId", linkMutation, StringComparison.Ordinal);
            Assert.DoesNotContain("projectV2Field", linkMutation, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_routes_ordinary_multi_select_through_project_field_mutations()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-project-import-").FullName;
        try
        {
            using var handler = new OrdinaryMultiSelectFieldStubHandler(existing: false);
            using var client = new GitHubGraphQLClient(
                "dummy-token",
                new Uri("https://example.test/graphql"),
                handler,
                delayAsync: null);
            var snapshot = MinimalSnapshot("Roadmap") with
            {
                Project = MinimalSnapshot("Roadmap").Project with
                {
                    ShortDescription = null,
                    Readme = null,
                    Public = false,
                    Closed = false,
                },
                Fields =
                [
                    new FieldSnapshot
                    {
                        Name = "Areas",
                        DataType = "MULTI_SELECT",
                        Options =
                        [
                            new SingleSelectOptionSnapshot { Id = "source-platform", Name = "Platform", Color = "PURPLE" },
                        ],
                    },
                ],
            };
            var importer = new ProjectImporter(client)
            {
                OperationLogDirectory = directory,
            };

            var result = await importer.ImportIntoAsync(
                snapshot,
                "target",
                7,
                TestContext.Current.CancellationToken);

            Assert.Equal("PVTMSF_areas", result.FieldIds["Areas"]);
            Assert.Empty(importer.Warnings);
            Assert.Contains(
                handler.RequestBodies,
                body => body.Contains("createProjectV2Field", StringComparison.Ordinal));
            Assert.DoesNotContain(
                handler.RequestBodies,
                body => body.Contains("createIssueField", StringComparison.Ordinal)
                    || body.Contains("createProjectV2IssueField", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_updates_existing_ordinary_multi_select_alongside_linked_multi_select()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-project-import-").FullName;
        try
        {
            using var handler = new IssueFieldStubHandler(existing: true, ordinaryMultiSelect: true);
            using var client = new GitHubGraphQLClient(
                "dummy-token",
                new Uri("https://example.test/graphql"),
                handler,
                delayAsync: null);
            var snapshot = MinimalSnapshot("Roadmap") with
            {
                Fields =
                [
                    new FieldSnapshot
                    {
                        Name = "Areas",
                        DataType = "MULTI_SELECT",
                        Options = [new SingleSelectOptionSnapshot { Id = "source-platform", Name = "Platform", Color = "PURPLE" }],
                    },
                    new FieldSnapshot
                    {
                        Name = "Teams",
                        DataType = "MULTI_SELECT",
                        Options = [new SingleSelectOptionSnapshot { Id = "source-sdk", Name = "SDK", Color = "GREEN" }],
                        IssueField = new IssueFieldConfigurationSnapshot
                        {
                            Description = "Teams involved",
                            Visibility = "ALL",
                        },
                    },
                ],
            };
            var importer = new ProjectImporter(client)
            {
                OperationLogDirectory = directory,
            };

            var result = await importer.ImportIntoAsync(
                snapshot,
                "target",
                7,
                TestContext.Current.CancellationToken);

            Assert.Equal("PVTMSF_areas", result.FieldIds["Areas"]);
            Assert.Equal("PVTMSFO_platform_updated", result.OptionIds["Areas"]["Platform"]);
            Assert.Equal("IFM_teams", result.IssueFieldIds["Teams"]);
            Assert.Empty(importer.Warnings);
            var fieldByNameRequest = Assert.Single(
                handler.RequestBodies,
                body => body.Contains("field(name:", StringComparison.Ordinal));
            Assert.Contains("ProjectV2FieldCommon", fieldByNameRequest, StringComparison.Ordinal);
            Assert.Contains("ProjectV2MultiSelectField", fieldByNameRequest, StringComparison.Ordinal);
            Assert.Contains("multiSelectOptions", fieldByNameRequest, StringComparison.Ordinal);
            Assert.Single(
                handler.RequestBodies,
                body => body.Contains("updateProjectV2Field", StringComparison.Ordinal));
            Assert.Single(
                handler.RequestBodies,
                body => body.Contains("createProjectV2IssueField", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_idempotently_ensures_existing_multi_select_issue_field_link_without_broken_reads()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-project-import-").FullName;
        try
        {
            using var handler = new IssueFieldStubHandler(existing: true);
            using var client = new GitHubGraphQLClient(
                "dummy-token",
                new Uri("https://example.test/graphql"),
                handler,
                delayAsync: null);
            var retries = new List<string>();
            client.OnRetry = retries.Add;
            var snapshot = MinimalSnapshot("Roadmap") with
            {
                Fields =
                [
                    new FieldSnapshot
                    {
                        Name = "Teams",
                        DataType = "MULTI_SELECT",
                        Options =
                        [
                            new SingleSelectOptionSnapshot { Id = "source-platform", Name = "Platform", Color = "PURPLE" },
                            new SingleSelectOptionSnapshot { Id = "source-sdk", Name = "SDK", Color = "GREEN" },
                        ],
                        IssueField = new IssueFieldConfigurationSnapshot
                        {
                            Description = "Teams involved",
                            Visibility = "ALL",
                        },
                    },
                ],
            };
            var progress = new List<string>();
            var importer = new ProjectImporter(client)
            {
                OperationLogDirectory = directory,
                OnProgress = progress.Add,
            };

            var result = await importer.ImportIntoAsync(
                snapshot,
                "target",
                7,
                TestContext.Current.CancellationToken);

            Assert.Equal("IFM_teams", result.IssueFieldIds["Teams"]);
            Assert.False(result.FieldIds.ContainsKey("Teams"));
            Assert.DoesNotContain(handler.RequestBodies, body => body.Contains("createIssueField", StringComparison.Ordinal));
            Assert.Single(
                handler.RequestBodies,
                body => body.Contains("createProjectV2IssueField", StringComparison.Ordinal));
            Assert.DoesNotContain(
                handler.RequestBodies,
                body => body.Contains("fields(first:", StringComparison.Ordinal)
                    || body.Contains("field(name:", StringComparison.Ordinal));
            Assert.Empty(retries);
            Assert.Contains(
                progress,
                message => message.Contains(
                    "linked multi-select Issue Fields are reconciled with an idempotent link mutation",
                    StringComparison.Ordinal));
            Assert.Contains(
                progress,
                message => message.Contains(
                    "Ensuring organization Issue Field 'Teams' is linked",
                    StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("TEXT", true, "IFT_teams", false, false)]
    [InlineData("MULTI_SELECT", false, "IFM_teams", false, true)]
    [InlineData("MULTI_SELECT", false, "IFM_teams", true, false)]
    public async Task Import_reconciles_same_named_normal_and_issue_fields_by_identity(
        string issueFieldDataType,
        bool textIssueField,
        string expectedIssueFieldId,
        bool fieldByNameReturnsLinked,
        bool shouldSucceed)
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-project-import-").FullName;
        try
        {
            using var handler = new IssueFieldStubHandler(
                existing: true,
                normalSameName: true,
                existingSameNamedLink: true,
                transientNormalDataTypeFailure: true,
                textIssueField: textIssueField,
                fieldByNameReturnsLinked: fieldByNameReturnsLinked);
            using var client = new GitHubGraphQLClient(
                "dummy-token",
                new Uri("https://example.test/graphql"),
                handler,
                delayAsync: static (_, _) => Task.CompletedTask);
            var snapshot = MinimalSnapshot("Roadmap") with
            {
                Fields =
                [
                    new FieldSnapshot
                    {
                        Name = "Teams",
                        DataType = "TEXT",
                    },
                    new FieldSnapshot
                    {
                        Name = "Teams",
                        DataType = issueFieldDataType,
                        Options = issueFieldDataType == "MULTI_SELECT"
                            ?
                            [
                                new SingleSelectOptionSnapshot { Id = "source-platform", Name = "Platform", Color = "PURPLE" },
                                new SingleSelectOptionSnapshot { Id = "source-sdk", Name = "SDK", Color = "GREEN" },
                            ]
                            : null,
                        IssueField = new IssueFieldConfigurationSnapshot
                        {
                            Description = "Teams involved",
                            Visibility = "ALL",
                        },
                    },
                ],
            };
            var importer = new ProjectImporter(client)
            {
                OperationLogDirectory = directory,
            };

            if (!shouldSucceed)
            {
                var exception = await Assert.ThrowsAsync<GitHubGraphQLException>(() => importer.ImportIntoAsync(
                    snapshot,
                    "target",
                    7,
                    TestContext.Current.CancellationToken));
                Assert.Contains("could not identify ordinary field 'Teams' separately", exception.Message, StringComparison.Ordinal);
                return;
            }

            var result = await importer.ImportIntoAsync(snapshot, "target", 7, TestContext.Current.CancellationToken);

            Assert.Equal(expectedIssueFieldId, result.IssueFieldIds["Teams"]);
            Assert.Equal("PVTF_teams", result.FieldIds["Teams"]);
            Assert.Equal(2, handler.NormalDataTypeQueryCount);
            Assert.Single(
                handler.RequestBodies,
                body => body.Contains("createProjectV2IssueField", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_batches_unambiguous_field_data_type_lookups()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-project-import-").FullName;
        try
        {
            using var handler = new IssueFieldStubHandler(existing: true, ordinaryFields: true);
            using var client = new GitHubGraphQLClient(
                "dummy-token",
                new Uri("https://example.test/graphql"),
                handler,
                delayAsync: static (_, _) => Task.CompletedTask);
            var snapshot = MinimalSnapshot("Roadmap") with
            {
                Fields =
                [
                    new FieldSnapshot { Name = "Notes", DataType = "TEXT" },
                    new FieldSnapshot { Name = "Estimate", DataType = "NUMBER" },
                    new FieldSnapshot
                    {
                        Name = "Teams",
                        DataType = "MULTI_SELECT",
                        Options =
                        [
                            new SingleSelectOptionSnapshot { Id = "source-platform", Name = "Platform", Color = "PURPLE" },
                            new SingleSelectOptionSnapshot { Id = "source-sdk", Name = "SDK", Color = "GREEN" },
                        ],
                        IssueField = new IssueFieldConfigurationSnapshot
                        {
                            Description = "Teams involved",
                            Visibility = "ALL",
                        },
                    },
                ],
            };
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            var result = await importer.ImportIntoAsync(
                snapshot,
                "target",
                7,
                TestContext.Current.CancellationToken);

            Assert.Equal("PVTF_notes", result.FieldIds["Notes"]);
            Assert.Equal("PVTF_estimate", result.FieldIds["Estimate"]);
            Assert.Single(
                handler.RequestBodies,
                body => body.Contains("nodes(ids:", StringComparison.Ordinal)
                    && body.Contains("PVTF_notes", StringComparison.Ordinal)
                    && body.Contains("PVTF_estimate", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_does_not_probe_linked_multi_select_issue_field_by_name()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-project-import-").FullName;
        try
        {
            using var handler = new IssueFieldStubHandler(existing: true, transientFieldByNameFailure: true);
            using var client = new GitHubGraphQLClient(
                "dummy-token",
                new Uri("https://example.test/graphql"),
                handler,
                delayAsync: static (_, _) => Task.CompletedTask);
            var snapshot = MinimalSnapshot("Roadmap") with
            {
                Fields =
                [
                    new FieldSnapshot
                    {
                        Name = "Teams",
                        DataType = "MULTI_SELECT",
                        Options =
                        [
                            new SingleSelectOptionSnapshot { Id = "source-platform", Name = "Platform", Color = "PURPLE" },
                            new SingleSelectOptionSnapshot { Id = "source-sdk", Name = "SDK", Color = "GREEN" },
                        ],
                        IssueField = new IssueFieldConfigurationSnapshot
                        {
                            Description = "Teams involved",
                            Visibility = "ALL",
                        },
                    },
                ],
            };
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            await importer.ImportIntoAsync(
                snapshot,
                "target",
                7,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, handler.FieldByNameQueryCount);
            Assert.DoesNotContain(handler.RequestBodies, body => body.Contains("field(name:", StringComparison.Ordinal));
            Assert.Contains(handler.RequestBodies, body => body.Contains("createProjectV2IssueField", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_creates_missing_normal_field_when_preview_connection_falls_back_by_name()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-project-import-").FullName;
        try
        {
            using var handler = new IssueFieldStubHandler(existing: true, missingNormalField: true);
            using var client = new GitHubGraphQLClient(
                "dummy-token",
                new Uri("https://example.test/graphql"),
                handler,
                delayAsync: null);
            var snapshot = MinimalSnapshot("Roadmap") with
            {
                Fields =
                [
                    new FieldSnapshot { Name = "Notes", DataType = "TEXT" },
                    new FieldSnapshot
                    {
                        Name = "Teams",
                        DataType = "MULTI_SELECT",
                        Options =
                        [
                            new SingleSelectOptionSnapshot { Id = "source-platform", Name = "Platform", Color = "PURPLE" },
                            new SingleSelectOptionSnapshot { Id = "source-sdk", Name = "SDK", Color = "GREEN" },
                        ],
                        IssueField = new IssueFieldConfigurationSnapshot
                        {
                            Description = "Teams involved",
                            Visibility = "ALL",
                        },
                    },
                ],
            };
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            var result = await importer.ImportIntoAsync(
                snapshot,
                "target",
                7,
                TestContext.Current.CancellationToken);

            Assert.Equal("PVTF_notes", result.FieldIds["Notes"]);
            Assert.Contains(handler.RequestBodies, body => body.Contains("createProjectV2Field", StringComparison.Ordinal));
            Assert.Contains(handler.RequestBodies, body => body.Contains("createProjectV2IssueField", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_updates_existing_issue_field_and_registers_replaced_options()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-project-import-").FullName;
        try
        {
            using var handler = new IssueFieldStubHandler(existing: true, requiresUpdate: true);
            using var client = new GitHubGraphQLClient(
                "dummy-token",
                new Uri("https://example.test/graphql"),
                handler,
                delayAsync: null);
            var snapshot = MinimalSnapshot("Roadmap") with
            {
                Fields =
                [
                    new FieldSnapshot
                    {
                        Name = "Teams",
                        DataType = "MULTI_SELECT",
                        Options =
                        [
                            new SingleSelectOptionSnapshot { Id = "source-platform", Name = "Platform", Color = "PURPLE" },
                            new SingleSelectOptionSnapshot { Id = "source-sdk", Name = "SDK", Color = "GREEN" },
                        ],
                        IssueField = new IssueFieldConfigurationSnapshot
                        {
                            Description = "Teams involved",
                            Visibility = "ALL",
                        },
                    },
                ],
            };
            var importer = new ProjectImporter(client)
            {
                OperationLogDirectory = directory,
            };

            var result = await importer.ImportIntoAsync(
                snapshot,
                "target",
                7,
                TestContext.Current.CancellationToken);

            var updateRequest = Assert.Single(
                handler.RequestBodies,
                body => body.Contains("updateIssueField", StringComparison.Ordinal));
            using var document = JsonDocument.Parse(updateRequest);
            var variables = document.RootElement.GetProperty("variables");
            Assert.Equal("IFM_teams", variables.GetProperty("id").GetString());
            Assert.Equal("Teams involved", variables.GetProperty("description").GetString());
            Assert.Equal("ALL", variables.GetProperty("visibility").GetString());
            Assert.Equal(
                ["Platform", "SDK"],
                variables.GetProperty("options").EnumerateArray().Select(option => option.GetProperty("name").GetString()));
            Assert.Equal("IFO_platform_updated", result.IssueFieldOptionIds["Teams"]["Platform"]);
            Assert.Equal("IFO_sdk_updated", result.IssueFieldOptionIds["Teams"]["SDK"]);
            Assert.DoesNotContain(handler.RequestBodies, body => body.Contains("createIssueField", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_creates_ordinary_multi_select_field_with_options_and_registers_option_ids()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-project-import-").FullName;
        try
        {
            using var handler = new OrdinaryMultiSelectFieldStubHandler(existing: false);
            using var client = new GitHubGraphQLClient(
                "dummy-token",
                new Uri("https://example.test/graphql"),
                handler,
                delayAsync: null);
            var snapshot = MinimalSnapshot("Roadmap") with
            {
                Fields =
                [
                    new FieldSnapshot
                    {
                        Name = "Areas",
                        DataType = "MULTI_SELECT",
                        Options =
                        [
                            new SingleSelectOptionSnapshot
                            {
                                Id = "source-platform",
                                Name = "Platform",
                                Color = "PURPLE",
                                Description = "Platform work",
                            },
                            new SingleSelectOptionSnapshot
                            {
                                Id = "source-sdk",
                                Name = "SDK",
                                Color = "GREEN",
                            },
                        ],
                    },
                ],
            };

            var result = await new ProjectImporter(client)
            {
                OperationLogDirectory = directory,
            }.ImportIntoAsync(
                snapshot,
                "target",
                7,
                TestContext.Current.CancellationToken);

            var request = Assert.Single(
                handler.RequestBodies,
                body => body.Contains("createProjectV2Field", StringComparison.Ordinal));
            using var document = JsonDocument.Parse(request);
            Assert.Contains(
                "multiSelectOptions: $multiSelectOptions",
                document.RootElement.GetProperty("query").GetString(),
                StringComparison.Ordinal);
            var variables = document.RootElement.GetProperty("variables");
            Assert.Equal("MULTI_SELECT", variables.GetProperty("dataType").GetString());
            var options = variables.GetProperty("multiSelectOptions");
            Assert.Equal(["Platform", "SDK"], options.EnumerateArray().Select(option => option.GetProperty("name").GetString()));
            Assert.Equal(["PURPLE", "GREEN"], options.EnumerateArray().Select(option => option.GetProperty("color").GetString()));
            Assert.Equal("Platform work", options[0].GetProperty("description").GetString());
            Assert.All(options.EnumerateArray(), option => Assert.False(option.TryGetProperty("id", out _)));

            Assert.Equal("PVTMSF_areas", result.FieldIds["Areas"]);
            Assert.Equal("PVTMSFO_platform", result.OptionIds["Areas"]["Platform"]);
            Assert.Equal("PVTMSFO_sdk", result.OptionIds["Areas"]["SDK"]);
            Assert.DoesNotContain(
                handler.RequestBodies,
                body => body.Contains("createIssueField", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_updates_ordinary_multi_select_options_and_preserves_matching_option_ids()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-project-import-").FullName;
        try
        {
            using var handler = new OrdinaryMultiSelectFieldStubHandler(existing: true);
            using var client = new GitHubGraphQLClient(
                "dummy-token",
                new Uri("https://example.test/graphql"),
                handler,
                delayAsync: null);
            var snapshot = MinimalSnapshot("Roadmap") with
            {
                Fields =
                [
                    new FieldSnapshot
                    {
                        Name = "Areas",
                        DataType = "MULTI_SELECT",
                        Options =
                        [
                            new SingleSelectOptionSnapshot
                            {
                                Id = "source-platform",
                                Name = "Platform",
                                Color = "PURPLE",
                                Description = "Platform work",
                            },
                            new SingleSelectOptionSnapshot { Id = "source-sdk", Name = "SDK", Color = "GREEN" },
                        ],
                    },
                ],
            };

            var result = await new ProjectImporter(client)
            {
                OperationLogDirectory = directory,
            }.ImportIntoAsync(
                snapshot,
                "target",
                7,
                TestContext.Current.CancellationToken);

            var request = Assert.Single(
                handler.RequestBodies,
                body => body.Contains("updateProjectV2Field", StringComparison.Ordinal));
            using var document = JsonDocument.Parse(request);
            Assert.Contains(
                "multiSelectOptions:",
                document.RootElement.GetProperty("query").GetString(),
                StringComparison.Ordinal);
            var variables = document.RootElement.GetProperty("variables");
            Assert.Equal("PVTMSF_areas", variables.GetProperty("fieldId").GetString());
            Assert.Equal(
                [
                    ("PVTMSFO_platform", "Platform", "PURPLE", "Platform work"),
                    (null, "SDK", "GREEN", string.Empty),
                ],
                variables.GetProperty("options").EnumerateArray()
                    .Select(option => (
                        option.TryGetProperty("id", out var id) ? id.GetString() : null,
                        option.GetProperty("name").GetString(),
                        option.GetProperty("color").GetString(),
                        option.GetProperty("description").GetString())));

            Assert.Equal("PVTMSF_areas", result.FieldIds["Areas"]);
            Assert.Equal("PVTMSFO_platform", result.OptionIds["Areas"]["Platform"]);
            Assert.Equal("PVTMSFO_sdk_updated", result.OptionIds["Areas"]["SDK"]);
            Assert.DoesNotContain(
                handler.RequestBodies,
                body => body.Contains("createProjectV2Field", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_rejects_ordinary_multi_select_without_options_before_api_calls(bool optionsAreNull)
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-project-import-").FullName;
        try
        {
            using var handler = new OrdinaryMultiSelectFieldStubHandler(existing: true);
            using var client = new GitHubGraphQLClient(
                "dummy-token",
                new Uri("https://example.test/graphql"),
                handler,
                delayAsync: null);
            var snapshot = MinimalSnapshot("Roadmap") with
            {
                Fields =
                [
                    new FieldSnapshot
                    {
                        Name = "Areas",
                        DataType = "MULTI_SELECT",
                        Options = optionsAreNull ? null : [],
                    },
                ],
            };

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                new ProjectImporter(client)
                {
                    OperationLogDirectory = directory,
                }.ImportIntoAsync(
                    snapshot,
                    "target",
                    7,
                    TestContext.Current.CancellationToken));

            Assert.Contains("Project multi-select field 'Areas' must define at least one option", exception.Message, StringComparison.Ordinal);
            Assert.Empty(handler.RequestBodies);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_rejects_ambiguous_ordinary_multi_select_and_linked_issue_field_identity()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-project-import-").FullName;
        try
        {
            using var handler = new IssueFieldStubHandler(
                existing: true,
                ordinaryMultiSelect: true,
                ordinaryMultiSelectIssueFieldCollision: true);
            using var client = new GitHubGraphQLClient(
                "dummy-token",
                new Uri("https://example.test/graphql"),
                handler,
                delayAsync: null);
            var snapshot = MinimalSnapshot("Roadmap") with
            {
                Fields =
                [
                    new FieldSnapshot
                    {
                        Name = "Areas",
                        DataType = "MULTI_SELECT",
                        Options = [new SingleSelectOptionSnapshot { Id = "source-platform", Name = "Platform", Color = "PURPLE" }],
                    },
                ],
            };

            var exception = await Assert.ThrowsAsync<GitHubGraphQLException>(() =>
                new ProjectImporter(client)
                {
                    OperationLogDirectory = directory,
                }.ImportIntoAsync(
                    snapshot,
                    "target",
                    7,
                    TestContext.Current.CancellationToken));

            Assert.Contains("Target project field 'Areas' is ambiguous", exception.Message, StringComparison.Ordinal);
            Assert.Contains("linked organization Issue Field", exception.Message, StringComparison.Ordinal);
            Assert.Contains(handler.RequestBodies, body => body.Contains("issueFields(first:", StringComparison.Ordinal));
            Assert.DoesNotContain(
                handler.RequestBodies,
                body => body.Contains("createProjectV2Field", StringComparison.Ordinal)
                    || body.Contains("updateProjectV2Field", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ProjectSnapshot MinimalSnapshot(string title) => new()
    {
        SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
        Project = new ProjectInfoSnapshot
        {
            Title = title,
            ShortDescription = "must not be applied",
            Readme = "must not be applied",
            Public = true,
            Closed = true,
        },
        Fields = [],
        Views = [],
        Workflows = [],
        Items = [],
    };

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

    private sealed class OrdinaryMultiSelectFieldStubHandler(bool existing) : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(body);
            var response = body switch
            {
                _ when body.Contains("projectV2(number:", StringComparison.Ordinal) =>
                    """{"data":{"organization":{"projectV2":{"id":"PVT_target","number":7,"title":"Roadmap","url":"https://github.com/orgs/target/projects/7","public":false}}}}""",
                _ when body.Contains("updateProjectV2(", StringComparison.Ordinal) =>
                    """{"data":{"updateProjectV2":{"projectV2":{"id":"PVT_target"}}}}""",
                _ when body.Contains("issueFields(first:", StringComparison.Ordinal) =>
                    """{"data":{"organization":{"issueFields":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}""",
                _ when body.Contains("fields(first:", StringComparison.Ordinal) =>
                    existing
                        ? """
                          {"data":{"node":{"fields":{"nodes":[{
                            "__typename":"ProjectV2MultiSelectField","id":"PVTMSF_areas","name":"Areas","dataType":"MULTI_SELECT",
                            "multiSelectOptions":[
                              {"id":"PVTMSFO_platform","name":"Platform"},
                              {"id":"PVTMSFO_old","name":"Old"}
                            ]
                          }]}}}}
                          """
                        : """{"data":{"node":{"fields":{"nodes":[{"__typename":"ProjectV2Field","id":"PVTF_title","name":"Title","dataType":"TITLE"}]}}}}""",
                _ when body.Contains("createProjectV2Field(", StringComparison.Ordinal) =>
                    """
                    {"data":{"createProjectV2Field":{"projectV2Field":{
                      "__typename":"ProjectV2MultiSelectField","id":"PVTMSF_areas","name":"Areas","dataType":"MULTI_SELECT",
                      "multiSelectOptions":[
                        {"id":"PVTMSFO_platform","name":"Platform"},
                        {"id":"PVTMSFO_sdk","name":"SDK"}
                      ]
                    }}}}
                    """,
                _ when body.Contains("updateProjectV2Field(", StringComparison.Ordinal) =>
                    """
                    {"data":{"updateProjectV2Field":{"projectV2Field":{
                      "__typename":"ProjectV2MultiSelectField","id":"PVTMSF_areas","name":"Areas","dataType":"MULTI_SELECT",
                      "multiSelectOptions":[
                        {"id":"PVTMSFO_platform","name":"Platform"},
                        {"id":"PVTMSFO_sdk_updated","name":"SDK"}
                      ]
                    }}}}
                    """,
                _ => throw new InvalidOperationException($"Unexpected request: {body}"),
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class IssueFieldStubHandler(
        bool existing = false,
        bool requiresUpdate = false,
        bool normalSameName = false,
        bool existingSameNamedLink = false,
        bool transientNormalDataTypeFailure = false,
        bool missingNormalField = false,
        bool transientFieldByNameFailure = false,
        bool ordinaryFields = false,
        bool textIssueField = false,
        bool fieldByNameReturnsLinked = false,
        bool ordinaryMultiSelect = false,
        bool ordinaryMultiSelectIssueFieldCollision = false) : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];

        public int NormalDataTypeQueryCount { get; private set; }

        public int FieldByNameQueryCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(body);
            var response = body switch
            {
                _ when body.Contains("projectV2(number:", StringComparison.Ordinal) =>
                    """{"data":{"organization":{"projectV2":{"id":"PVT_target","number":7,"title":"Roadmap","url":"https://github.com/orgs/target/projects/7","public":false}}}}""",
                _ when body.Contains("updateProjectV2(", StringComparison.Ordinal) =>
                    """{"data":{"updateProjectV2":{"projectV2":{"id":"PVT_target"}}}}""",
                _ when body.Contains("fields(first:", StringComparison.Ordinal) =>
                    ordinaryMultiSelect
                        ? """
                          {"data":{"node":{"fields":{"nodes":[{
                            "__typename":"ProjectV2MultiSelectField","id":"PVTMSF_areas","name":"Areas","dataType":"MULTI_SELECT",
                            "multiSelectOptions":[{"id":"PVTMSFO_old","name":"Old"}]
                          }]}}}}
                          """
                        : ordinaryFields
                        ? """{"data":{"node":{"fields":{"nodes":[{"__typename":"ProjectV2Field","id":"PVTF_notes","name":"Notes"},{"__typename":"ProjectV2Field","id":"PVTF_estimate","name":"Estimate"},{"__typename":"ProjectV2Field","id":"PVTF_linked_teams","name":"Teams"}]}}}}"""
                        : existingSameNamedLink
                        ? """{"data":{"node":{"fields":{"nodes":[{"__typename":"ProjectV2Field","id":"PVTF_teams","name":"Teams"},{"__typename":"ProjectV2Field","id":"PVTF_linked_teams","name":"Teams"}]}}}}"""
                        : existing
                        ? """{"data":{"node":null},"errors":[{"message":"Something went wrong while executing your query on the preview API."}]}"""
                        : """{"data":{"node":{"fields":{"nodes":[{"id":"PVTF_title","name":"Title","dataType":"TITLE"}]}}}}""",
                _ when body.Contains("field(name:", StringComparison.Ordinal) =>
                    ordinaryMultiSelect && body.Contains("\"name\":\"Areas\"", StringComparison.Ordinal)
                        ? """{"data":{"node":{"field":{"__typename":"ProjectV2MultiSelectField","id":"PVTMSF_areas","name":"Areas","dataType":"MULTI_SELECT","multiSelectOptions":[{"id":"PVTMSFO_platform","name":"Platform"}]}}}}"""
                        : ordinaryFields && body.Contains("\"name\":\"Notes\"", StringComparison.Ordinal)
                        ? """{"data":{"node":{"field":{"__typename":"ProjectV2Field","id":"PVTF_notes","name":"Notes"}}}}"""
                        : ordinaryFields && body.Contains("\"name\":\"Estimate\"", StringComparison.Ordinal)
                        ? """{"data":{"node":{"field":{"__typename":"ProjectV2Field","id":"PVTF_estimate","name":"Estimate"}}}}"""
                        : missingNormalField && body.Contains("\"name\":\"Notes\"", StringComparison.Ordinal)
                        ? """{"data":{"node":{"field":null}},"errors":[{"type":"NOT_FOUND","message":"Could not resolve to a Unions::ProjectV2FieldConfiguration with the name Notes"}]}"""
                        : normalSameName
                        ? fieldByNameReturnsLinked
                            ? """{"data":{"node":{"field":{"__typename":"ProjectV2Field","id":"PVTF_linked_teams","name":"Teams"}}}}"""
                            : """{"data":{"node":{"field":{"__typename":"ProjectV2Field","id":"PVTF_teams","name":"Teams"}}}}"""
                        : transientFieldByNameFailure
                        ? FieldByNameResponse()
                        : """{"data":{"node":null},"errors":[{"message":"Something went wrong while executing your query on the preview API."}]}""",
                _ when body.Contains("nodes(ids:", StringComparison.Ordinal) =>
                    ordinaryMultiSelect && body.Contains("PVTMSF_areas", StringComparison.Ordinal)
                        ? """
                          {"data":{"nodes":[{
                            "__typename":"ProjectV2MultiSelectField","id":"PVTMSF_areas","name":"Areas","dataType":"MULTI_SELECT",
                            "multiSelectOptions":[{"id":"PVTMSFO_old","name":"Old"}]
                          }]}}
                          """
                        : ordinaryFields
                        && body.Contains("PVTF_notes", StringComparison.Ordinal)
                        && body.Contains("PVTF_estimate", StringComparison.Ordinal)
                        ? """{"data":{"nodes":[{"id":"PVTF_notes","dataType":"TEXT"},{"id":"PVTF_estimate","dataType":"NUMBER"}]}}"""
                        : body.Contains("PVTF_title", StringComparison.Ordinal)
                        ? """{"data":{"nodes":[{"id":"PVTF_title","dataType":"TITLE"}]}}"""
                        : normalSameName && body.Contains("PVTF_teams", StringComparison.Ordinal)
                            ? NormalDataTypeResponse()
                            : """{"data":{"nodes":[null]},"errors":[{"message":"Something went wrong while executing your query on the preview API."}]}""",
                _ when body.Contains("issueFields(first:", StringComparison.Ordinal) =>
                    ordinaryMultiSelectIssueFieldCollision
                        ? """
                          {"data":{"organization":{"issueFields":{"nodes":[{
                            "__typename":"IssueFieldMultiSelect","id":"IFM_areas","name":"Areas",
                            "dataType":"MULTI_SELECT","description":null,"visibility":"ALL","options":[]
                          }],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                          """
                        : existing
                        ? textIssueField
                            ? """
                              {"data":{"organization":{"issueFields":{"nodes":[{
                                "__typename":"IssueFieldText","id":"IFT_teams","name":"Teams",
                                "dataType":"TEXT","description":"Teams involved","visibility":"ALL"
                              }],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                              """
                            : requiresUpdate
                            ? """
                              {"data":{"organization":{"issueFields":{"nodes":[{
                                "__typename":"IssueFieldMultiSelect","id":"IFM_teams","name":"Teams",
                                "dataType":"MULTI_SELECT","description":"Old description","visibility":"ALL",
                                "options":[
                                  {"id":"IFO_old","name":"Old","color":"GRAY","description":null}
                                ]
                              }],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                              """
                            : """
                          {"data":{"organization":{"issueFields":{"nodes":[{
                            "__typename":"IssueFieldMultiSelect","id":"IFM_teams","name":"Teams",
                            "dataType":"MULTI_SELECT","description":"Teams involved","visibility":"ALL",
                            "options":[
                              {"id":"IFO_platform","name":"Platform","color":"PURPLE","description":null},
                              {"id":"IFO_sdk","name":"SDK","color":"GREEN","description":null}
                            ]
                          }],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                          """
                        : """{"data":{"organization":{"issueFields":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}""",
                _ when body.Contains("updateIssueField(", StringComparison.Ordinal) =>
                    """
                    {"data":{"updateIssueField":{"issueField":{
                      "__typename":"IssueFieldMultiSelect","id":"IFM_teams","name":"Teams",
                      "dataType":"MULTI_SELECT","description":"Teams involved","visibility":"ALL",
                      "options":[
                        {"id":"IFO_platform_updated","name":"Platform","color":"PURPLE","description":null},
                        {"id":"IFO_sdk_updated","name":"SDK","color":"GREEN","description":null}
                      ]
                    }}}}
                    """,
                _ when ordinaryMultiSelect && body.Contains("updateProjectV2Field(", StringComparison.Ordinal) =>
                    """
                    {"data":{"updateProjectV2Field":{"projectV2Field":{
                      "__typename":"ProjectV2MultiSelectField","id":"PVTMSF_areas","name":"Areas","dataType":"MULTI_SELECT",
                      "multiSelectOptions":[
                        {"id":"PVTMSFO_platform_updated","name":"Platform"}
                      ]
                    }}}}
                    """,
                _ when body.Contains("organization(login:", StringComparison.Ordinal) =>
                    """{"data":{"organization":{"id":"O_target"}}}""",
                _ when body.Contains("createIssueField(", StringComparison.Ordinal) =>
                    """
                    {"data":{"createIssueField":{"issueField":{
                      "__typename":"IssueFieldMultiSelect","id":"IFM_teams","name":"Teams",
                      "dataType":"MULTI_SELECT","description":"Teams involved","visibility":"ALL",
                      "options":[
                        {"id":"IFO_platform","name":"Platform","color":"PURPLE","description":null},
                        {"id":"IFO_sdk","name":"SDK","color":"GREEN","description":null}
                      ]
                    }}}}
                    """,
                _ when body.Contains("createProjectV2Field(", StringComparison.Ordinal) =>
                    """{"data":{"createProjectV2Field":{"projectV2Field":{"__typename":"ProjectV2Field","id":"PVTF_notes","name":"Notes","dataType":"TEXT"}}}}""",
                _ when body.Contains("createProjectV2IssueField(", StringComparison.Ordinal) =>
                    """{"data":{"createProjectV2IssueField":{"clientMutationId":"link-operation"}}}""",
                _ => throw new InvalidOperationException($"Unexpected request: {body}"),
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }

        private string NormalDataTypeResponse()
        {
            NormalDataTypeQueryCount++;
            return transientNormalDataTypeFailure && NormalDataTypeQueryCount == 1
                ? """{"data":{"nodes":[null]},"errors":[{"message":"Something went wrong while executing your query on the preview API."}]}"""
                : textIssueField
                    ? """{"data":{"nodes":[{"id":"PVTF_teams","dataType":"TEXT"},{"id":"PVTF_linked_teams","dataType":"TEXT"}]}}"""
                : """{"data":{"nodes":[{"id":"PVTF_teams","dataType":"TEXT"}]}}""";
        }

        private string FieldByNameResponse()
        {
            FieldByNameQueryCount++;
            return FieldByNameQueryCount == 1
                ? """{"data":{"node":null},"errors":[{"message":"Something went wrong while executing your query on the preview API."}]}"""
                : """{"data":{"node":{"field":null}}}""";
        }
    }
}
