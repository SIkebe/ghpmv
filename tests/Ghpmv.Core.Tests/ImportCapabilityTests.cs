using System.Net;
using System.Diagnostics;
using System.Text;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Tests;

public class ImportCapabilityTests
{
    [Fact]
    public async Task Requirements_command_prints_snapshot_driven_gates_without_a_token()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-capabilities-").FullName;
        try
        {
            var snapshot = MinimalSnapshot() with
            {
                Fields =
                [
                    new FieldSnapshot
                    {
                        Name = "Teams",
                        DataType = "MULTI_SELECT",
                        Options = [],
                        IssueField = new IssueFieldConfigurationSnapshot { Visibility = "ALL" },
                    },
                ],
                LinkedTeams =
                [
                    new LinkedTeamSnapshot { Organization = "source", Slug = "platform", Name = "Platform" },
                ],
            };
            await SnapshotFile.SaveAsync(snapshot, directory, TestContext.Current.CancellationToken);
            var startInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[]
            {
                Path.Combine(AppContext.BaseDirectory, "ghpmv.dll"),
                "requirements",
                "--in",
                directory,
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start ghpmv requirements.");
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.Equal(0, process.ExitCode);
            Assert.Empty(await error);
            Assert.Contains(
                "requires-organization-administrator=true",
                await output,
                StringComparison.Ordinal);
            Assert.Contains(
                "requires-team-administrator=true",
                await output,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Analyzer_derives_snapshot_roles_and_repository_capabilities()
    {
        var snapshot = MinimalSnapshot() with
        {
            Project = MinimalSnapshot().Project with { Public = true },
            Fields =
            [
                new FieldSnapshot
                {
                    Name = "Teams",
                    DataType = "MULTI_SELECT",
                    Options = [],
                    IssueField = new IssueFieldConfigurationSnapshot { Visibility = "ALL" },
                },
            ],
            Collaborators =
            [
                new CollaboratorSnapshot { Type = "TEAM", Login = "platform", Role = "WRITER" },
            ],
            LinkedTeams =
            [
                new LinkedTeamSnapshot { Organization = "source", Slug = "platform", Name = "Platform" },
            ],
            LinkedRepositories = ["source/repo"],
            Workflows =
            [
                new WorkflowSnapshot
                {
                    Number = 1,
                    Name = "Auto-add",
                    Enabled = true,
                    Ui = new WorkflowUiSnapshot { Repository = "repo" },
                },
            ],
            Items =
            [
                new ItemSnapshot
                {
                    Type = "ISSUE",
                    Position = 0,
                    IsArchived = false,
                    Repository = "source/repo",
                    Number = 1,
                    FieldValues =
                    [
                        new FieldValueSnapshot
                        {
                            FieldName = "Teams",
                            IsIssueField = true,
                            MultiSelectOptionNames = ["Platform"],
                        },
                    ],
                },
                new ItemSnapshot
                {
                    Type = "PULL_REQUEST",
                    Position = 1,
                    IsArchived = false,
                    Repository = "source/repo",
                    Number = 2,
                    FieldValues = [],
                },
            ],
        };

        var plan = ImportCapabilityAnalyzer.Analyze(snapshot, includeBrowserAutomation: true);

        Assert.True(plan.RequiresOrganizationAdministrator);
        Assert.True(plan.RequiresProjectAdministrator);
        Assert.True(plan.RequiresMembersRead);
        Assert.True(plan.RequiresTeamAdministrator);
        Assert.True(plan.RequiresVisibilityManagement);
        var fullName = Assert.Single(plan.Repositories, requirement =>
            requirement.SourceRepository == "source/repo");
        Assert.True(fullName.Capabilities.HasFlag(RepositoryCapability.IssuesRead));
        Assert.True(fullName.Capabilities.HasFlag(RepositoryCapability.PullRequestsRead));
        Assert.True(fullName.Capabilities.HasFlag(RepositoryCapability.IssuesWrite));
        Assert.True(fullName.Capabilities.HasFlag(RepositoryCapability.ContentsWrite));
        Assert.True(fullName.Capabilities.HasFlag(RepositoryCapability.SameOwner));
        var shortName = Assert.Single(plan.Repositories, requirement =>
            requirement.SourceRepository == "repo");
        Assert.True(shortName.Capabilities.HasFlag(RepositoryCapability.BrowserAccess));
        Assert.True(shortName.Capabilities.HasFlag(RepositoryCapability.SameOwner));

        var apiOnlyPlan = ImportCapabilityAnalyzer.Analyze(
            snapshot,
            includeBrowserAutomation: false);
        Assert.DoesNotContain(
            apiOnlyPlan.Repositories,
            requirement => requirement.SourceRepository == "repo");
    }

    [Fact]
    public void Analyzer_ignores_team_capabilities_for_user_owned_projects()
    {
        var snapshot = MinimalSnapshot() with
        {
            Collaborators =
            [
                new CollaboratorSnapshot { Type = "TEAM", Login = "platform", Role = "WRITER" },
            ],
            LinkedTeams =
            [
                new LinkedTeamSnapshot { Organization = "source", Slug = "platform", Name = "Platform" },
            ],
        };

        var plan = ImportCapabilityAnalyzer.Analyze(
            snapshot,
            ownerType: ProjectOwnerType.User);

        Assert.False(plan.RequiresProjectAdministrator);
        Assert.False(plan.RequiresMembersRead);
        Assert.False(plan.RequiresTeamAdministrator);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Analyzer_requires_issue_write_for_every_issue_when_snapshot_has_issue_fields(
        bool includeLegacyValue)
    {
        var snapshot = MinimalSnapshot() with
        {
            Fields =
            [
                new FieldSnapshot
                {
                    Name = "Teams",
                    DataType = "MULTI_SELECT",
                    Options = [],
                    IssueField = new IssueFieldConfigurationSnapshot { Visibility = "ALL" },
                },
            ],
            Items =
            [
                new ItemSnapshot
                {
                    Type = "ISSUE",
                    Position = 0,
                    IsArchived = false,
                    Repository = "source/repo",
                    Number = 1,
                    FieldValues = includeLegacyValue
                        ?
                        [
                            new FieldValueSnapshot
                            {
                                FieldName = "Teams",
                                IsIssueField = null,
                                MultiSelectOptionNames = ["Platform"],
                            },
                        ]
                        : [],
                },
            ],
        };

        var requirement = Assert.Single(
            ImportCapabilityAnalyzer.Analyze(snapshot).Repositories);

        Assert.True(requirement.Capabilities.HasFlag(RepositoryCapability.IssuesWrite));
    }

    [Fact]
    public async Task Preflight_accepts_admin_and_writable_mapped_repository()
    {
        using var handler = new QueueHandler(
            Probe(
                HttpStatusCode.UnprocessableEntity,
                "issue_fields=write",
                """{"message":"Invalid request. Invalid input: data cannot be null."}"""),
            Json(
                """
                {"full_name":"target/repo","permissions":{"pull":true,"triage":true,"push":true,"maintain":false,"admin":false}}
                """));
        using var rest = new GitHubRestClient("token", baseUri: null, handler);
        var plan = new ImportCapabilityPlan(
            RequiresOrganizationAdministrator: true,
            RequiresProjectAdministrator: false,
            RequiresMembersRead: false,
            RequiresVisibilityManagement: false,
            [new RepositoryCapabilityRequirement(
                "source/repo",
                RepositoryCapability.MetadataRead | RepositoryCapability.IssuesWrite | RepositoryCapability.SameOwner)]);

        await ImportCapabilityPreflight.ValidateAsync(
            plan,
            "target",
            new Dictionary<string, string> { ["source/repo"] = "target/repo" },
            rest,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["orgs/target/issue-fields", "repos/target/repo"],
            handler.Paths);
    }

    [Fact]
    public async Task Preflight_accepts_classic_pat_validation_without_permission_header()
    {
        using var handler = new QueueHandler(
            new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            {
                Content = new StringContent(
                    """{"message":"Invalid request. Invalid input: object is missing required keys: name, data_type."}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        using var rest = new GitHubRestClient("token", baseUri: null, handler);
        var plan = new ImportCapabilityPlan(
            RequiresOrganizationAdministrator: true,
            RequiresProjectAdministrator: false,
            RequiresMembersRead: false,
            RequiresVisibilityManagement: false,
            []);

        await ImportCapabilityPreflight.ValidateAsync(
            plan,
            "target",
            new Dictionary<string, string>(),
            rest,
            TestContext.Current.CancellationToken);

        Assert.Single(handler.Paths);
    }

    [Fact]
    public async Task Preflight_rejects_non_admin_before_repository_access()
    {
        using var handler = new QueueHandler(
            Probe(
                HttpStatusCode.Forbidden,
                "issue_fields=write",
                """{"message":"Resource not accessible by personal access token"}"""));
        using var rest = new GitHubRestClient("token", baseUri: null, handler);
        var plan = new ImportCapabilityPlan(
            RequiresOrganizationAdministrator: true,
            RequiresProjectAdministrator: false,
            RequiresMembersRead: false,
            RequiresVisibilityManagement: false,
            []);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ImportCapabilityPreflight.ValidateAsync(
                plan,
                "target",
                new Dictionary<string, string>(),
                rest,
                TestContext.Current.CancellationToken));

        Assert.Contains("administrator", exception.Message, StringComparison.Ordinal);
        Assert.Single(handler.Paths);
    }

    [Fact]
    public async Task Preflight_rejects_cross_owner_linked_repository()
    {
        using var handler = new QueueHandler();
        using var rest = new GitHubRestClient("token", baseUri: null, handler);
        var plan = new ImportCapabilityPlan(
            RequiresOrganizationAdministrator: false,
            RequiresProjectAdministrator: false,
            RequiresMembersRead: false,
            RequiresVisibilityManagement: false,
            [new RepositoryCapabilityRequirement("source/repo", RepositoryCapability.SameOwner)]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ImportCapabilityPreflight.ValidateAsync(
                plan,
                "target",
                new Dictionary<string, string> { ["source/repo"] = "other/repo" },
                rest,
                TestContext.Current.CancellationToken));

        Assert.Contains("must belong to target organization", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task Preflight_rejects_missing_mapping_without_network()
    {
        using var handler = new QueueHandler();
        using var rest = new GitHubRestClient("token", baseUri: null, handler);
        var plan = new ImportCapabilityPlan(
            RequiresOrganizationAdministrator: false,
            RequiresProjectAdministrator: false,
            RequiresMembersRead: false,
            RequiresVisibilityManagement: false,
            [new RepositoryCapabilityRequirement("source/repo", RepositoryCapability.IssuesRead)]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ImportCapabilityPreflight.ValidateAsync(
                plan,
                "target",
                new Dictionary<string, string>(),
                rest,
                TestContext.Current.CancellationToken));

        Assert.Contains("requires a mapping", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task Preflight_requires_push_role_for_linked_repository_contents()
    {
        using var handler = new QueueHandler(
            Json(
                """
                {"full_name":"target/repo","permissions":{"pull":true,"triage":true,"push":false,"maintain":false,"admin":false}}
                """));
        using var rest = new GitHubRestClient("token", baseUri: null, handler);
        var plan = new ImportCapabilityPlan(
            RequiresOrganizationAdministrator: false,
            RequiresProjectAdministrator: false,
            RequiresMembersRead: false,
            RequiresVisibilityManagement: false,
            [new RepositoryCapabilityRequirement(
                "source/repo",
                RepositoryCapability.IssuesWrite | RepositoryCapability.ContentsWrite)]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ImportCapabilityPreflight.ValidateAsync(
                plan,
                "target",
                new Dictionary<string, string> { ["source/repo"] = "target/repo" },
                rest,
                TestContext.Current.CancellationToken));

        Assert.Contains("ContentsWrite", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preflight_validates_members_read_for_team_collaborators()
    {
        using var handler = new QueueHandler(Json("[]"));
        using var rest = new GitHubRestClient("token", baseUri: null, handler);
        var plan = new ImportCapabilityPlan(
            RequiresOrganizationAdministrator: false,
            RequiresProjectAdministrator: true,
            RequiresMembersRead: true,
            RequiresVisibilityManagement: false,
            []);

        await ImportCapabilityPreflight.ValidateAsync(
            plan,
            "target",
            new Dictionary<string, string>(),
            rest,
            TestContext.Current.CancellationToken);

        Assert.Equal(["orgs/target/teams?per_page=1"], handler.Paths);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Project_preflight_rejects_missing_admin_for_privileged_changes(
        bool collaborators,
        bool visibilityChange)
    {
        var plan = new ImportCapabilityPlan(
            RequiresOrganizationAdministrator: false,
            RequiresProjectAdministrator: collaborators,
            RequiresMembersRead: false,
            RequiresVisibilityManagement: visibilityChange,
            []);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ImportCapabilityPreflight.ValidateProjectCapabilities(
                plan,
                projectNumber: 42,
                viewerCanUpdate: false,
                visibilityChangeRequired: visibilityChange));

        Assert.Contains("Project #42", exception.Message, StringComparison.Ordinal);
    }

    private static ProjectSnapshot MinimalSnapshot() => new()
    {
        SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
        Project = new ProjectInfoSnapshot
        {
            Title = "Project",
            Public = false,
            Closed = false,
        },
        Fields = [],
        Views = [],
        Workflows = [],
        Items = [],
    };

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Probe(
        HttpStatusCode statusCode,
        string acceptedPermissions,
        string json)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        response.Headers.TryAddWithoutValidation(
            "X-Accepted-GitHub-Permissions",
            acceptedPermissions);
        return response;
    }

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery.TrimStart('/'));
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No response configured.");
            }

            return Task.FromResult(_responses.Dequeue());
        }
    }
}
