using System.Net;
using System.Text;
using System.Text.Json;
using Ghpmv.Cli;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Tests;

public sealed class MigrationDiagnosticRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_project_preflight_failure_keeps_resolved_target_in_stderr_and_report(bool importByNumber)
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-resolved-context-").FullName;
        try
        {
            using var diagnostics = new ImportFailureDiagnostics("destination", "organization", null, false);
            var snapshot = new ProjectSnapshot
            {
                SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
                Project = new() { Title = "Demo project", Public = false, Closed = false, Template = false },
                Source = new() { Owner = "source", Number = 12, Id = "PVT_source", Title = "Demo project" },
                Fields = [], Items = [], Views = [], Workflows = [], StatusUpdates = [], LinkedRepositories = [],
                LinkedTeams = [new() { Organization = "source", Slug = "team", Name = "Demo team" }],
            };
            diagnostics.SetSnapshot(snapshot);
            diagnostics.SetStage("preflight");
            using var handler = new TeamPreflightHandler();
            using var client = new GitHubGraphQLClient("token", null, handler, static (_, _) => Task.CompletedTask);
            var importer = new ProjectImporter(client)
            {
                OperationLogDirectory = directory,
                OnConflict = ConflictAction.Update,
                OnTargetProjectResolved = diagnostics.SetTargetProject,
                OnTargetIdentityResolved = diagnostics.SetTargetIdentity,
            };
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => importByNumber
                ? importer.ImportIntoAsync(snapshot, "destination", 42, TestContext.Current.CancellationToken)
                : importer.ImportAsync(snapshot, "destination", TestContext.Current.CancellationToken));
            var context = Assert.IsType<MigrationDiagnosticContext>(MigrationDiagnostics.Get(exception));
            Assert.Equal("PVT_known", context.Target?.Id);
            Assert.Equal(42, context.Target?.Number);
            Assert.Equal("destination", context.Target?.Owner);
            Assert.Equal("Team", context.Element?.Kind);
            Assert.Equal("destination/team", context.Element?.Name);
            Assert.Equal("source", context.Source?.Owner);
            Assert.Equal("preflight-linked-team", context.Operation);
            var output = new List<string>();
            diagnostics.WriteFailure(exception, output.Add);
            Assert.Contains(output, line => line.Contains("target: destination / Project 42", StringComparison.Ordinal));
            Assert.Contains(output, line => line.Contains("PVT_known", StringComparison.Ordinal));
            Assert.Contains("operation: preflight-linked-team", output);
            await diagnostics.SaveFailureAsync(directory, exception, TestContext.Current.CancellationToken);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(directory, ImportFailureDiagnostics.FileName), TestContext.Current.CancellationToken));
            var target = report.RootElement.GetProperty("context").GetProperty("target");
            Assert.Equal("PVT_known", target.GetProperty("id").GetString());
            Assert.Equal(42, target.GetProperty("number").GetInt32());
            Assert.Equal("preflight-linked-team", report.RootElement.GetProperty("context").GetProperty("operation").GetString());
            var apiFailure = report.RootElement.GetProperty("exceptions").EnumerateArray()
                .Single(detail => detail.GetProperty("type").GetString() == typeof(GitHubGraphQLException).FullName);
            Assert.Equal("query", apiFailure.GetProperty("operationKind").GetString());
            Assert.DoesNotContain("SYNTHETIC-RAW-BODY", report.RootElement.GetRawText(), StringComparison.Ordinal);
            Assert.Equal(2, handler.RequestCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData("preflight-linked-team", false)]
    [InlineData("preflight-linked-team", true)]
    public async Task Query_failures_preserve_scoped_operation_or_use_query_when_unspecified(string? operation, bool withoutInternalRetry)
    {
        using var scope = MigrationDiagnostics.Begin(new() { Operation = operation });
        using var handler = new TeamPreflightHandler();
        using var client = new GitHubGraphQLClient("token", null, handler, static (_, _) => Task.CompletedTask);
        const string query = "query { organization(login: \"synthetic\") { team(slug: \"synthetic\") { id } } }";
        var exception = await Assert.ThrowsAsync<GitHubGraphQLException>(() => withoutInternalRetry
            ? client.QueryWithoutInternalErrorRetryAsync(query, null, TestContext.Current.CancellationToken)
            : client.QueryAsync(query, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(operation ?? "query", MigrationDiagnostics.Get(exception)?.Operation);
        Assert.Equal("query", exception.OperationKind);
        Assert.Equal("FORBIDDEN", exception.ErrorType);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public void Stderr_and_recoverable_failure_text_include_known_ids_and_mapped_repository()
    {
        using var diagnostics = new ImportFailureDiagnostics("destination", "organization", 42, false);
        using var scope = MigrationDiagnostics.Begin(new()
        {
            Source = new() { Owner = "source", Number = 12, Id = "PVT_source" },
            Target = new() { Owner = "destination", Number = 42, Id = "PVT_target" },
            Item = new()
            {
                Kind = "ISSUE", Repository = "source/repo", TargetRepository = "destination/repo\nforged",
                Number = 7, SourceId = "I_source", TargetId = "PVTI_target",
            },
            Element = new() { Kind = "Field", Name = "Text", SourceId = "PVTF_source", TargetId = "PVTF_target" },
            Operation = "updateProjectV2ItemFieldValue",
        });
        var exception = new GitHubGraphQLException("SYNTHETIC-RAW-BODY");
        MigrationDiagnostics.Attach(exception);
        var output = new List<string>();
        diagnostics.WriteFailure(exception, output.Add);
        foreach (var text in new[] { string.Join('\n', output), MigrationDiagnostics.Failure(exception) })
        {
            foreach (var identifier in new[] { "PVT_source", "PVT_target", "I_source", "PVTI_target", "PVTF_source", "PVTF_target" })
            {
                Assert.Contains(identifier, text, StringComparison.Ordinal);
            }
            Assert.Contains("source/repo#7", text, StringComparison.Ordinal);
            Assert.Contains("target repository destination/repo\\u000aforged#7", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\nforged", text, StringComparison.Ordinal);
            Assert.DoesNotContain("SYNTHETIC-RAW-BODY", text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Get")]
    [InlineData("Post")]
    [InlineData("Put")]
    [InlineData("Delete")]
    public async Task Safe_rest_metadata_survives_stderr_report_and_recoverable_failure_text(string method)
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-rest-context-").FullName;
        try
        {
            using var handler = new RestFailureHandler();
            using var rest = new GitHubRestClient("token", null, handler);
            Task RequestAsync() => method switch
            {
                "Get" => rest.GetAsync("synthetic", TestContext.Current.CancellationToken),
                "Post" => rest.PostAsync("synthetic", new { value = "SYNTHETIC-INPUT" }, TestContext.Current.CancellationToken),
                "Put" => rest.PutAsync("synthetic", new { value = "SYNTHETIC-INPUT" }, TestContext.Current.CancellationToken),
                "Delete" => rest.DeleteAsync("synthetic", TestContext.Current.CancellationToken),
                _ => throw new InvalidOperationException("Unexpected test method."),
            };
            var exception = await Assert.ThrowsAsync<HttpRequestException>(RequestAsync);
            using var diagnostics = new ImportFailureDiagnostics("destination", "organization", 42, false);
            var output = new List<string>();
            diagnostics.WriteFailure(exception, output.Add);
            await diagnostics.SaveFailureAsync(directory, exception, TestContext.Current.CancellationToken);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(directory, ImportFailureDiagnostics.FileName), TestContext.Current.CancellationToken));
            var detail = Assert.Single(report.RootElement.GetProperty("exceptions").EnumerateArray());
            Assert.Equal("403 Forbidden", detail.GetProperty("statusCode").GetString());
            Assert.Equal("ABCD:1234", detail.GetProperty("requestId").GetString());
            Assert.Equal("Rest" + method, detail.GetProperty("operationKind").GetString());
            Assert.Equal(0, detail.GetProperty("retryCount").GetInt32());
            Assert.Equal("http-error", detail.GetProperty("failureReason").GetString());
            foreach (var text in new[] { string.Join('\n', output), MigrationDiagnostics.Failure(exception), detail.GetRawText() })
            {
                Assert.Contains("Rest" + method, text, StringComparison.Ordinal);
                Assert.Contains("ABCD:1234", text, StringComparison.Ordinal);
                Assert.DoesNotContain("SYNTHETIC-", text, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Arbitrary_http_messages_cannot_impersonate_safe_rest_diagnostics()
    {
        var exception = new HttpRequestException("GitHub REST error 403 SYNTHETIC-RAW-BODY", null, HttpStatusCode.Forbidden);
        Assert.Null(GitHubRestClient.GetFailureDiagnostic(exception));
        Assert.DoesNotContain("SYNTHETIC-", ImportFailureDiagnostics.FormatExceptionForReport(exception), StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC-", MigrationDiagnostics.Failure(exception), StringComparison.Ordinal);
    }

    private sealed class RestFailureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = Json("""{"message":"SYNTHETIC-RAW-BODY"}""", HttpStatusCode.Forbidden);
            response.Headers.Add("X-GitHub-Request-Id", "ABCD:1234");
            return Task.FromResult(response);
        }
    }

    private sealed class TeamPreflightHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (body.Contains("team(slug:", StringComparison.Ordinal))
            {
                return Json("""{"errors":[{"type":"FORBIDDEN","message":"Resource not accessible SYNTHETIC-RAW-BODY"}]}""");
            }
            const string project = """{"id":"PVT_known","number":42,"title":"Demo project","url":"https://github.com/orgs/destination/projects/42","viewerCanUpdate":true,"viewerCanClose":true}""";
            if (body.Contains("projectsV2(", StringComparison.Ordinal))
            {
                return Json("""{"data":{"organization":{"projectsV2":{"nodes":[""" + project + """],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}""");
            }
            if (body.Contains("projectV2(number:", StringComparison.Ordinal))
            {
                return Json("""{"data":{"organization":{"projectV2":""" + project + "}}}");
            }
            throw new InvalidOperationException("Unexpected synthetic request.");
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
