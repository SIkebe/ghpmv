using System.Net;
using System.Text;
using System.Text.Json;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Tests;

public class TeamLinkImportTests
{
    [Fact]
    public async Task Import_links_mapped_team_once_and_skips_it_on_rerun()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-team-import-").FullName;
        try
        {
            using var handler = new TeamImportHandler();
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client)
            {
                TeamMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["source/platform"] = "target/engineering",
                },
                OperationLogDirectory = directory,
            };
            var snapshot = Snapshot(
                new LinkedTeamSnapshot { Organization = "source", Slug = "platform", Name = "Platform" });

            await importer.ImportIntoAsync(snapshot, "target", 7, TestContext.Current.CancellationToken);
            await importer.ImportIntoAsync(snapshot, "target", 7, TestContext.Current.CancellationToken);

            Assert.Equal(1, handler.LinkMutationCount);
            Assert.Equal(2, handler.TeamResolutionCount);
            Assert.Contains(handler.RequestBodies, body =>
            {
                using var document = JsonDocument.Parse(body);
                var variables = document.RootElement.GetProperty("variables");
                return variables.TryGetProperty("organization", out var organization)
                    && organization.GetString() == "target"
                    && variables.GetProperty("slug").GetString() == "engineering";
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_reuses_mapped_linked_team_for_explicit_collaborator()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-team-import-").FullName;
        try
        {
            using var handler = new TeamImportHandler();
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client)
            {
                TeamMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["source/platform"] = "target/engineering",
                },
                OperationLogDirectory = directory,
            };
            var snapshot = Snapshot(
                new LinkedTeamSnapshot { Organization = "source", Slug = "platform", Name = "Platform" }) with
            {
                Collaborators =
                [
                    new CollaboratorSnapshot { Type = "TEAM", Login = "platform", Role = "WRITER" },
                ],
            };

            await importer.ImportIntoAsync(snapshot, "target", 7, TestContext.Current.CancellationToken);

            Assert.Equal(1, handler.TeamResolutionCount);
            var request = Assert.Single(handler.RequestBodies, body =>
                body.Contains("updateProjectV2Collaborators", StringComparison.Ordinal));
            using var document = JsonDocument.Parse(request);
            var collaborator = Assert.Single(
                document.RootElement.GetProperty("variables").GetProperty("collaborators").EnumerateArray());
            Assert.Equal("T_target", collaborator.GetProperty("teamId").GetString());
            Assert.Equal("WRITER", collaborator.GetProperty("role").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Unresolved_team_fails_before_new_project_mutation()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-team-import-").FullName;
        try
        {
            using var handler = new TeamImportHandler(teamExists: false, projectExists: false);
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                importer.ImportAsync(
                    Snapshot(new LinkedTeamSnapshot { Organization = "source", Slug = "missing", Name = "Missing" }),
                    "target",
                    TestContext.Current.CancellationToken));

            Assert.Contains("unresolved:", exception.Message, StringComparison.Ordinal);
            Assert.Contains("before any project write", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, handler.MutationCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Ambiguous_mapping_fails_before_existing_project_mutation()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-team-import-").FullName;
        try
        {
            using var handler = new TeamImportHandler();
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client)
            {
                TeamMapping = new Dictionary<string, string>
                {
                    ["source/platform"] = "target/engineering",
                    ["source/sdk"] = "target/engineering",
                },
                OperationLogDirectory = directory,
            };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                importer.ImportIntoAsync(
                    Snapshot(
                        new LinkedTeamSnapshot { Organization = "source", Slug = "platform", Name = "Platform" },
                        new LinkedTeamSnapshot { Organization = "source", Slug = "sdk", Name = "SDK" }),
                    "target",
                    7,
                    TestContext.Current.CancellationToken));

            Assert.Contains("ambiguous:", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, handler.MutationCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Cross_organization_mapping_fails_before_existing_project_mutation()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-team-import-").FullName;
        try
        {
            using var handler = new TeamImportHandler();
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client)
            {
                TeamMapping = new Dictionary<string, string>
                {
                    ["source/platform"] = "other-org/platform",
                },
                OperationLogDirectory = directory,
            };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                importer.ImportIntoAsync(
                    Snapshot(new LinkedTeamSnapshot { Organization = "source", Slug = "platform", Name = "Platform" }),
                    "target",
                    7,
                    TestContext.Current.CancellationToken));

            Assert.Contains("target Project belongs to 'target'", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, handler.TeamResolutionCount);
            Assert.Equal(0, handler.MutationCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Writer_without_project_admin_permission_fails_preflight()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-team-import-").FullName;
        try
        {
            using var handler = new TeamImportHandler(viewerCanUpdate: true, viewerCanManageAccess: false);
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                importer.ImportIntoAsync(
                    Snapshot(new LinkedTeamSnapshot { Organization = "source", Slug = "platform", Name = "Platform" }),
                    "target",
                    7,
                    TestContext.Current.CancellationToken));

            Assert.Contains("permission:", exception.Message, StringComparison.Ordinal);
            Assert.Contains("does not have Project admin access", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, handler.MutationCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Team_member_without_team_admin_permission_fails_preflight()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-team-import-").FullName;
        try
        {
            using var handler = new TeamImportHandler(viewerCanAdministerTeam: false);
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                importer.ImportIntoAsync(
                    Snapshot(new LinkedTeamSnapshot { Organization = "source", Slug = "platform", Name = "Platform" }),
                    "target",
                    7,
                    TestContext.Current.CancellationToken));

            Assert.Contains("permission:", exception.Message, StringComparison.Ordinal);
            Assert.Contains("cannot administer target Team 'target/platform'", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, handler.MutationCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task User_owned_import_ignores_team_links()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-team-import-").FullName;
        try
        {
            using var handler = new TeamImportHandler(ownerField: "user");
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client)
            {
                OwnerType = ProjectOwnerType.User,
                OperationLogDirectory = directory,
            };

            await importer.ImportIntoAsync(
                Snapshot(new LinkedTeamSnapshot { Organization = "source", Slug = "platform", Name = "Platform" }),
                "target-user",
                7,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, handler.TeamResolutionCount);
            Assert.Equal(0, handler.LinkMutationCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ProjectSnapshot Snapshot(params LinkedTeamSnapshot[] teams) => new()
    {
        SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
        Project = new ProjectInfoSnapshot { Title = "Roadmap", Public = false, Closed = false },
        Fields = [],
        Views = [],
        Workflows = [],
        Items = [],
        LinkedTeams = teams,
    };

    private static GitHubGraphQLClient CreateClient(HttpMessageHandler handler)
        => new(
            "dummy-token",
            new Uri("https://example.test/graphql"),
            handler,
            delayAsync: static (_, _) => Task.CompletedTask);

    private sealed class TeamImportHandler(
        bool teamExists = true,
        bool projectExists = true,
        bool viewerCanUpdate = true,
        bool viewerCanManageAccess = true,
        bool viewerCanAdministerTeam = true,
        string ownerField = "organization") : HttpMessageHandler
    {
        private bool _linked;

        public List<string> RequestBodies { get; } = [];

        public int MutationCount { get; private set; }

        public int LinkMutationCount { get; private set; }

        public int TeamResolutionCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(body);
            using var document = JsonDocument.Parse(body);
            var query = document.RootElement.GetProperty("query").GetString()!;
            if (query.Contains("mutation", StringComparison.Ordinal))
            {
                MutationCount++;
            }

            string response;
            if (query.Contains("projectV2(number:", StringComparison.Ordinal))
            {
                response = projectExists
                    ? "{\"data\":{\"" + ownerField + "\":{\"projectV2\":{\"id\":\"PVT_target\",\"number\":7,\"title\":\"Roadmap\",\"url\":\"https://example.test/projects/7\",\"public\":false,\"viewerCanUpdate\":" + viewerCanUpdate.ToString().ToLowerInvariant() + ",\"viewerCanClose\":" + viewerCanManageAccess.ToString().ToLowerInvariant() + ",\"viewerCanReopen\":false}}}}"
                    : "{\"data\":{\"" + ownerField + "\":{\"projectV2\":null}}}";
            }
            else if (query.Contains("projectsV2(first:", StringComparison.Ordinal))
            {
                response = "{\"data\":{\"" + ownerField + "\":{\"projectsV2\":{\"nodes\":[],\"pageInfo\":{\"hasNextPage\":false,\"endCursor\":null}}}}}";
            }
            else if (query.Contains("team(slug:", StringComparison.Ordinal))
            {
                TeamResolutionCount++;
                response = teamExists
                    ? "{\"data\":{\"organization\":{\"team\":{\"id\":\"T_target\",\"name\":\"Engineering\",\"slug\":\"engineering\",\"viewerCanAdminister\":" + viewerCanAdministerTeam.ToString().ToLowerInvariant() + ",\"organization\":{\"login\":\"target\"}}}}}"
                    : """{"data":{"organization":{"team":null}}}""";
            }
            else if (query.Contains("query($login: String!)", StringComparison.Ordinal))
            {
                response = "{\"data\":{\"" + ownerField + "\":{\"id\":\"O_target\"}}}";
            }
            else if (query.Contains("updateProjectV2(", StringComparison.Ordinal))
            {
                response = """{"data":{"updateProjectV2":{"projectV2":{"id":"PVT_target"}}}}""";
            }
            else if (query.Contains("fields(first:", StringComparison.Ordinal))
            {
                response = """{"data":{"node":{"fields":{"nodes":[]}}}}""";
            }
            else if (query.Contains("teams(first:", StringComparison.Ordinal))
            {
                response = "{\"data\":{\"node\":{\"teams\":{\"nodes\":" +
                    (_linked ? "[{\"id\":\"T_target\"}]" : "[]") +
                    ",\"pageInfo\":{\"hasNextPage\":false,\"endCursor\":null}}}}}";
            }
            else if (query.Contains("linkProjectV2ToTeam", StringComparison.Ordinal))
            {
                LinkMutationCount++;
                _linked = true;
                response = """{"data":{"linkProjectV2ToTeam":{"team":{"id":"T_target"}}}}""";
            }
            else if (query.Contains("updateProjectV2Collaborators", StringComparison.Ordinal))
            {
                response = """{"data":{"updateProjectV2Collaborators":{"collaborators":{"nodes":[{"__typename":"Team"}]}}}}""";
            }
            else
            {
                throw new InvalidOperationException($"Unexpected GraphQL request: {query}");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }
}
