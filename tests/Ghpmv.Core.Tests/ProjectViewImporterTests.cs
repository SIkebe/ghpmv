using System.Net;
using System.Text;
using System.Text.Json;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Tests;

public class ProjectViewImporterTests
{
    [Fact]
    public async Task Created_project_reuses_default_and_applies_api_settings()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-view-import-").FullName;
        try
        {
            using var handler = new ViewHandler(directory);
            using var client = CreateClient(handler);
            var log = new ProjectImportLog();
            var importer = new ProjectViewImporter(client, log, ct => log.SaveAsync(directory, ct))
            {
                UserMapping = new Dictionary<string, string> { ["octocat"] = "octocat_target" },
                RepositoryMapping = new Dictionary<string, string> { ["source/repo"] = "target/repo" },
                OrganizationMapping = new Dictionary<string, string> { ["source"] = "target" },
                BrowserEnrichmentPlanned = true,
            };
            var view = View(
                number: 3,
                name: "Roadmap",
                layout: "ROADMAP_LAYOUT",
                filter: "assignee:octocat repo:source/repo org:source",
                visibleFields: ["Status", "Title"]);

            var result = await importer.ImportAsync(
                [view],
                "PVT_target",
                new Dictionary<string, string>
                {
                    ["Title"] = "PVTF_title",
                    ["Status"] = "PVTSSF_status",
                },
                ProjectImportOutcome.Created,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, result[3]);
            Assert.Equal(0, handler.CreateCount);
            var update = Assert.Single(handler.RequestBodies, body => body.Contains("updateProjectV2View", StringComparison.Ordinal));
            using var document = JsonDocument.Parse(update);
            var variables = document.RootElement.GetProperty("variables");
            Assert.Equal("PVTV_default", variables.GetProperty("viewId").GetString());
            Assert.Equal("Roadmap", variables.GetProperty("name").GetString());
            Assert.Equal("ROADMAP_LAYOUT", variables.GetProperty("layout").GetString());
            Assert.Equal(
                "assignee:octocat_target repo:target/repo org:target",
                variables.GetProperty("filter").GetString());
            Assert.Equal(
                ["PVTSSF_status", "PVTF_title"],
                variables.GetProperty("configuration")
                    .GetProperty("visibleFieldIds")
                    .EnumerateArray()
                    .Select(id => id.GetString()));
            Assert.Empty(importer.Warnings);
            Assert.Empty(log.PendingViews);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Ambiguous_view_create_is_adopted_without_resending()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-view-resume-").FullName;
        try
        {
            using var handler = new ViewHandler(directory) { FailCreateAmbiguously = true };
            using var client = CreateClient(handler);
            var log = new ProjectImportLog();
            var importer = new ProjectViewImporter(client, log, ct => log.SaveAsync(directory, ct))
            {
                BrowserEnrichmentPlanned = true,
            };
            var view = View(2, "Board", "BOARD_LAYOUT", filter: null, visibleFields: []);

            await Assert.ThrowsAsync<AmbiguousMutationResultException>(
                () => importer.ImportAsync(
                    [view],
                    "PVT_target",
                    new Dictionary<string, string>(),
                    ProjectImportOutcome.Updated,
                    TestContext.Current.CancellationToken));

            var pending = Assert.Single(log.PendingViews).Value;
            Assert.Equal("PVT_target", pending.ProjectId);
            Assert.Equal(["PVTV_existing"], pending.ExistingViewIds);
            Assert.True(handler.PendingWasPresentAtCreate);

            handler.Resume = true;
            var resumedLog = await ProjectImportLog.LoadAsync(directory, TestContext.Current.CancellationToken);
            var resumedImporter = new ProjectViewImporter(
                client,
                resumedLog,
                ct => resumedLog.SaveAsync(directory, ct))
            {
                BrowserEnrichmentPlanned = true,
            };

            var result = await resumedImporter.ImportAsync(
                [view],
                "PVT_target",
                new Dictionary<string, string>(),
                ProjectImportOutcome.Updated,
                TestContext.Current.CancellationToken);

            Assert.Equal(8, result[2]);
            Assert.Equal(1, handler.CreateCount);
            Assert.Empty(resumedLog.PendingViews);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Unmatched_view_is_created_updated_mapped_and_clears_pending_operation()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-view-create-").FullName;
        try
        {
            using var handler = new ViewHandler(directory);
            using var client = CreateClient(handler);
            var log = new ProjectImportLog();
            var importer = new ProjectViewImporter(client, log, ct => log.SaveAsync(directory, ct))
            {
                BrowserEnrichmentPlanned = true,
            };

            var result = await importer.ImportAsync(
                [View(2, "Board", "BOARD_LAYOUT", filter: null, visibleFields: [])],
                "PVT_target",
                new Dictionary<string, string>(),
                ProjectImportOutcome.Updated,
                TestContext.Current.CancellationToken);

            Assert.Equal(8, result[2]);
            Assert.Equal(1, handler.CreateCount);
            Assert.True(handler.PendingWasPresentAtCreate);
            var update = Assert.Single(handler.RequestBodies, body => body.Contains("updateProjectV2View", StringComparison.Ordinal));
            using var document = JsonDocument.Parse(update);
            Assert.Equal(
                "PVTV_created",
                document.RootElement.GetProperty("variables").GetProperty("viewId").GetString());
            Assert.Empty(log.PendingViews);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_visible_field_is_omitted_with_a_warning()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-view-missing-field-").FullName;
        try
        {
            using var handler = new ViewHandler(directory) { MissingField = true };
            using var client = CreateClient(handler);
            var log = new ProjectImportLog();
            var importer = new ProjectViewImporter(client, log, ct => log.SaveAsync(directory, ct));

            await importer.ImportAsync(
                [View(1, "View 1", "TABLE_LAYOUT", filter: null, visibleFields: ["Missing"])],
                "PVT_target",
                new Dictionary<string, string>(),
                ProjectImportOutcome.Created,
                TestContext.Current.CancellationToken);

            Assert.Contains(importer.Warnings, warning =>
                warning.Contains("visible field 'Missing' was not found", StringComparison.Ordinal));
            var update = Assert.Single(handler.RequestBodies, body => body.Contains("updateProjectV2View", StringComparison.Ordinal));
            using var document = JsonDocument.Parse(update);
            Assert.Empty(document.RootElement.GetProperty("variables")
                .GetProperty("configuration")
                .GetProperty("visibleFieldIds")
                .EnumerateArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Api_only_update_warns_when_existing_browser_settings_cannot_be_cleared()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-view-existing-settings-").FullName;
        try
        {
            using var handler = new ViewHandler(directory);
            using var client = CreateClient(handler);
            var log = new ProjectImportLog();
            var importer = new ProjectViewImporter(client, log, ct => log.SaveAsync(directory, ct));

            await importer.ImportAsync(
                [View(1, "View 1", "TABLE_LAYOUT", filter: null, visibleFields: [])],
                "PVT_target",
                new Dictionary<string, string>(),
                ProjectImportOutcome.Updated,
                TestContext.Current.CancellationToken);

            Assert.Contains(importer.Warnings, warning =>
                warning.Contains("existing browser-only settings", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Api_only_board_without_a_column_warns_about_the_default_column()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-view-board-column-").FullName;
        try
        {
            using var handler = new ViewHandler(directory);
            using var client = CreateClient(handler);
            var log = new ProjectImportLog();
            var importer = new ProjectViewImporter(client, log, ct => log.SaveAsync(directory, ct));

            await importer.ImportAsync(
                [View(2, "Board", "BOARD_LAYOUT", filter: null, visibleFields: [])],
                "PVT_target",
                new Dictionary<string, string>(),
                ProjectImportOutcome.Updated,
                TestContext.Current.CancellationToken);

            Assert.Contains(importer.Warnings, warning =>
                warning.Contains("column-by", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Api_only_created_project_does_not_warn_that_the_default_view_preexisted()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-view-created-warning-").FullName;
        try
        {
            using var handler = new ViewHandler(directory);
            using var client = CreateClient(handler);
            var log = new ProjectImportLog();
            var importer = new ProjectViewImporter(client, log, ct => log.SaveAsync(directory, ct));

            await importer.ImportAsync(
                [View(1, "Backlog", "TABLE_LAYOUT", filter: null, visibleFields: [])],
                "PVT_target",
                new Dictionary<string, string>(),
                ProjectImportOutcome.Created,
                TestContext.Current.CancellationToken);

            Assert.DoesNotContain(importer.Warnings, warning =>
                warning.Contains("existing browser-only settings", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static GitHubGraphQLClient CreateClient(HttpMessageHandler handler)
        => new("token", new Uri("https://example.test/graphql"), handler, (_, _) => Task.CompletedTask);

    private static ViewSnapshot View(
        int number,
        string name,
        string layout,
        string? filter,
        IReadOnlyList<string> visibleFields) => new()
        {
            Number = number,
            Name = name,
            Layout = layout,
            Filter = filter,
            GroupByFields = [],
            SortByFields = [],
            VerticalGroupByFields = [],
            VisibleFields = visibleFields,
        };

    private sealed class ViewHandler(string directory) : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];

        public bool FailCreateAmbiguously { get; init; }

        public bool Resume { get; set; }

        public bool MissingField { get; init; }

        public int CreateCount { get; private set; }

        public bool PendingWasPresentAtCreate { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(body);
            using var document = JsonDocument.Parse(body);
            var query = document.RootElement.GetProperty("query").GetString()!;

            if (query.Contains("views(first:", StringComparison.Ordinal))
            {
                return Json(Resume
                    ? """
                      {"data":{"node":{"views":{"nodes":[
                        {"id":"PVTV_existing","number":4,"name":"Existing","layout":"TABLE_LAYOUT"},
                        {"id":"PVTV_created","number":8,"name":"Board","layout":"BOARD_LAYOUT"}
                      ],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                      """
                    : """
                      {"data":{"node":{"views":{"nodes":[
                        {"id":"PVTV_default","number":1,"name":"View 1","layout":"TABLE_LAYOUT"}
                      ],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                      """.Replace("PVTV_default", FailCreateAmbiguously ? "PVTV_existing" : "PVTV_default", StringComparison.Ordinal)
                        .Replace("\"number\":1", FailCreateAmbiguously ? "\"number\":4" : "\"number\":1", StringComparison.Ordinal)
                        .Replace("\"View 1\"", FailCreateAmbiguously ? "\"Existing\"" : "\"View 1\"", StringComparison.Ordinal));
            }

            if (query.Contains("field(name:", StringComparison.Ordinal))
            {
                return MissingField
                    ? Json("""{"data":{"node":{"field":null}},"errors":[{"type":"NOT_FOUND","message":"Could not resolve to a Unions::ProjectV2FieldConfiguration with the name Missing"}]}""")
                    : throw new InvalidOperationException($"Unexpected field lookup: {query}");
            }

            if (query.Contains("createProjectV2View", StringComparison.Ordinal))
            {
                CreateCount++;
                var log = await ProjectImportLog.LoadAsync(directory, cancellationToken);
                PendingWasPresentAtCreate = log.PendingViews.Count == 1;
                if (FailCreateAmbiguously && !Resume)
                {
                    throw new HttpRequestException("Response ended prematurely.");
                }

                return Json(
                    """{"data":{"createProjectV2View":{"projectV2View":{"id":"PVTV_created","number":8,"name":"Board","layout":"BOARD_LAYOUT"}}}}""");
            }

            if (query.Contains("updateProjectV2View", StringComparison.Ordinal))
            {
                var variables = document.RootElement.GetProperty("variables");
                var id = variables.GetProperty("viewId").GetString();
                var number = id == "PVTV_default" ? 1 : 8;
                var name = variables.GetProperty("name").GetString();
                var layout = variables.GetProperty("layout").GetString();
                return Json(JsonSerializer.Serialize(new
                {
                    data = new
                    {
                        updateProjectV2View = new
                        {
                            projectV2View = new { id, number, name, layout },
                        },
                    },
                }));
            }

            throw new InvalidOperationException($"Unexpected operation: {query}");
        }

        private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };
    }
}
