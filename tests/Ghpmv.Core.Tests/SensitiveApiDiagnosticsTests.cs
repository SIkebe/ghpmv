using System.Net;
using System.Text.Json;
using Ghpmv.Cli;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;

namespace Ghpmv.Core.Tests;

public sealed class SensitiveApiDiagnosticsTests
{
    private const string Secret = "SYNTHETIC-RESPONSE-SECRET";
    private const string Failure = """
        {"errors":[{"type":"FORBIDDEN","message":"Resource not accessible SYNTHETIC-RESPONSE-SECRET","extensions":{"secret":"SYNTHETIC-RESPONSE-SECRET"},"path":["viewer","SYNTHETIC-RESPONSE-SECRET"]}]}
        """;

    [Fact]
    public async Task Large_malicious_graphql_arrays_produce_a_bounded_redacted_import_report()
    {
        const string privateEnum = "SYNTHETIC_PRIVATE_ENUM";
        var responseName = new string('x', 128);
        var path = Enumerable.Range(0, 256).Select(index => index switch
        {
            1 => privateEnum,
            2 => Secret,
            255 => "SYNTHETIC_PRIVATE_TAIL",
            _ => responseName,
        }).ToArray();
        var body = JsonSerializer.Serialize(new
        {
            errors = Enumerable.Range(0, 256).Select(_ => new
            {
                type = "FORBIDDEN",
                message = $"Resource not accessible {Secret}",
                path,
                extensions = new { secret = Secret },
            }),
        });
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "bounded-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var handler = new StubHandler(Response(HttpStatusCode.OK, body));
            using var client = new GitHubGraphQLClient("SYNTHETIC-TOKEN", null, handler, (_, _) => Task.CompletedTask);
            var query = $"query($syntheticInput: SyntheticInput = {{ syntheticInputField: {privateEnum} }}) {{ {responseName} }}";
            var exception = await Assert.ThrowsAsync<GitHubGraphQLException>(() =>
                client.QueryAsync(query, cancellationToken: TestContext.Current.CancellationToken));
            var diagnostics = new ImportFailureDiagnostics("synthetic", "organization", null, false);
            await diagnostics.SaveFailureAsync(directory, exception, TestContext.Current.CancellationToken);
            var report = await File.ReadAllTextAsync(
                Path.Combine(directory, ImportFailureDiagnostics.FileName), TestContext.Current.CancellationToken);

            Assert.True(System.Text.Encoding.UTF8.GetByteCount(body) > 4_000_000);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(report) < 90_000);
            Assert.DoesNotContain(Secret, report, StringComparison.Ordinal);
            Assert.DoesNotContain(privateEnum, report, StringComparison.Ordinal);
            Assert.DoesNotContain("SYNTHETIC_PRIVATE_TAIL", report, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(report);
            var detail = Assert.Single(document.RootElement.GetProperty("exceptions").EnumerateArray());
            var errors = detail.GetProperty("graphQlErrors");
            Assert.Equal(17, errors.GetArrayLength());
            for (var index = 0; index < 16; index++)
            {
                var retainedPath = errors[index].GetProperty("path");
                Assert.Equal(33, retainedPath.GetArrayLength());
                Assert.Equal(responseName, retainedPath[0].GetString());
                Assert.Equal("[redacted]", retainedPath[1].GetString());
                Assert.Equal("[redacted]", retainedPath[2].GetString());
                Assert.Equal("[path truncated]", retainedPath[32].GetString());
            }

            Assert.Equal("Additional GraphQL errors truncated.", errors[16].GetProperty("message").GetString());
            Assert.Empty(Directory.GetFiles(directory, "ghpmv-sensitive-api-*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Graphql_failure_is_safe_everywhere_except_explicit_sensitive_file(bool enabled)
    {
        var directory = CreateDirectory();
        var warnings = new List<string>();
        try
        {
            using var sink = new SensitiveApiDiagnostics(directory, message =>
            {
                Assert.Empty(Directory.GetFiles(directory, "ghpmv-sensitive-api-*"));
                warnings.Add(message);
            });
            using var handler = new StubHandler(Response(HttpStatusCode.OK, Failure));
            using var client = new GitHubGraphQLClient("SYNTHETIC-TOKEN", null, handler, (_, _) => Task.CompletedTask)
            {
                SensitiveDiagnostics = enabled ? sink : null,
                OnRetry = warnings.Add,
            };
            var exception = await Assert.ThrowsAsync<GitHubGraphQLException>(() => client.QueryAsync(
                "query { viewer { login } }", new { privateInput = "SYNTHETIC-INPUT" }, TestContext.Current.CancellationToken));
            Assert.Equal("FORBIDDEN", exception.ErrorType);
            Assert.Equal("ABCD:1234:5678:9ABC:01234567", exception.RequestId);
            Assert.Equal("query", exception.OperationKind);
            Assert.Equal(0, exception.RetryCount);
            Assert.Contains("HTTP 200", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);

            var diagnostics = new ImportFailureDiagnostics("synthetic", "organization", null, false);
            diagnostics.RecordProgress(exception.Message);
            await diagnostics.SaveFailureAsync(directory, new InvalidOperationException(exception.Message, exception), TestContext.Current.CancellationToken);
            var report = await File.ReadAllTextAsync(Path.Combine(directory, ImportFailureDiagnostics.FileName), TestContext.Current.CancellationToken);
            Assert.DoesNotContain(Secret, report, StringComparison.Ordinal);
            Assert.Contains("ABCD:1234:5678:9ABC:01234567", report, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, string.Join('\n', warnings), StringComparison.Ordinal);
            sink.Dispose();
            Assert.Equal(enabled, File.Exists(sink.FilePath));
            if (enabled)
            {
                using var captured = JsonDocument.Parse(await File.ReadAllTextAsync(sink.FilePath, TestContext.Current.CancellationToken));
                Assert.Equal(Failure, captured.RootElement.GetProperty("body").GetString());
                Assert.Contains("SENSITIVE", Assert.Single(warnings), StringComparison.Ordinal);
                Assert.Contains(sink.FilePath, warnings[0], StringComparison.Ordinal);
                Assert.DoesNotContain("SYNTHETIC-TOKEN", captured.RootElement.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain("SYNTHETIC-INPUT", captured.RootElement.ToString(), StringComparison.Ordinal);
            }
            else
            {
                Assert.Empty(warnings);
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Rest_failure_and_probe_never_expose_body_or_untrusted_headers(bool probe, bool enabled)
    {
        var directory = CreateDirectory();
        var warnings = new List<string>();
        try
        {
            using var sink = new SensitiveApiDiagnostics(directory, warnings.Add);
            var response = Response(HttpStatusCode.Forbidden, Secret);
            response.ReasonPhrase = Secret;
            response.Headers.Remove("X-GitHub-Request-Id");
            response.Headers.TryAddWithoutValidation("X-GitHub-Request-Id", Secret);
            response.Headers.TryAddWithoutValidation("X-Accepted-GitHub-Permissions", Secret);
            using var handler = new StubHandler(response);
            using var rest = new GitHubRestClient("SYNTHETIC-TOKEN", null, handler) { SensitiveDiagnostics = enabled ? sink : null };
            Exception exception;
            if (probe)
            {
                exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ImportCapabilityPreflight.ValidateAsync(
                    new ImportCapabilityPlan(true, false, false, false, []),
                    "synthetic", new Dictionary<string, string>(), rest, TestContext.Current.CancellationToken));
                Assert.Contains("reports missing input: False", exception.Message, StringComparison.Ordinal);
            }
            else
            {
                exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
                    rest.PostAsync("synthetic", new { value = "SYNTHETIC-INPUT" }, TestContext.Current.CancellationToken));
                Assert.Equal(HttpStatusCode.Forbidden, ((HttpRequestException)exception).StatusCode);
            }

            Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
            Assert.Contains("403", exception.Message, StringComparison.Ordinal);
            Assert.Contains("[redacted]", exception.Message, StringComparison.Ordinal);
            var diagnostics = new ImportFailureDiagnostics("synthetic", "organization", null, false);
            diagnostics.RecordProgress(exception.Message);
            await diagnostics.SaveFailureAsync(directory, exception, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(Secret, await File.ReadAllTextAsync(Path.Combine(directory, ImportFailureDiagnostics.FileName), TestContext.Current.CancellationToken), StringComparison.Ordinal);
            sink.Dispose();
            Assert.Equal(enabled, File.Exists(sink.FilePath));
            if (enabled)
            {
                using var captured = JsonDocument.Parse(await File.ReadAllTextAsync(sink.FilePath, TestContext.Current.CancellationToken));
                Assert.Equal(Secret, captured.RootElement.GetProperty("body").GetString());
            }
            Assert.DoesNotContain(Secret, string.Join('\n', warnings), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Success_responses_and_malformed_http_success_are_not_collected()
    {
        var directory = CreateDirectory();
        try
        {
            using var sink = new SensitiveApiDiagnostics(directory, _ => Assert.Fail("Unexpected warning."));
            using var graphHandler = new StubHandler(Response(HttpStatusCode.OK, """{"data":{"viewer":{"login":"SYNTHETIC-SUCCESS"}}}"""));
            using var graph = new GitHubGraphQLClient("token", null, graphHandler, (_, _) => Task.CompletedTask) { SensitiveDiagnostics = sink };
            await graph.GetViewerLoginAsync(TestContext.Current.CancellationToken);
            using var restHandler = new StubHandler(Response(HttpStatusCode.OK, """{"value":"SYNTHETIC-SUCCESS"}"""));
            using var rest = new GitHubRestClient("token", null, restHandler) { SensitiveDiagnostics = sink };
            await rest.GetAsync("synthetic", TestContext.Current.CancellationToken);
            using var malformedHandler = new StubHandler(Response(HttpStatusCode.OK, Secret));
            using var malformed = new GitHubRestClient("token", null, malformedHandler) { SensitiveDiagnostics = sink };
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => malformed.GetAsync("synthetic", TestContext.Current.CancellationToken));
            Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
            using var malformedGraphHandler = new StubHandler(Enumerable.Range(0, 4).Select(_ => Response(HttpStatusCode.OK, Secret)).ToArray());
            using var malformedGraph = new GitHubGraphQLClient("token", null, malformedGraphHandler, (_, _) => Task.CompletedTask) { SensitiveDiagnostics = sink };
            var graphError = await Assert.ThrowsAsync<GitHubGraphQLException>(() => malformedGraph.GetViewerLoginAsync(TestContext.Current.CancellationToken));
            Assert.DoesNotContain(Secret, graphError.ToString(), StringComparison.Ordinal);
            Assert.Equal("malformed-response", graphError.FailureReason);
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Retried_failures_are_collected_once_with_retry_counts_but_progress_is_safe()
    {
        var directory = CreateDirectory();
        var messages = new List<string>();
        try
        {
            using var sink = new SensitiveApiDiagnostics(directory, messages.Add);
            using var handler = new StubHandler(
                Response(HttpStatusCode.BadGateway, Secret),
                Response(HttpStatusCode.OK, """{"errors":[{"type":"INTERNAL","message":"Something went wrong while executing your query SYNTHETIC-RESPONSE-SECRET"}]}"""),
                Response(HttpStatusCode.OK, Failure));
            using var client = new GitHubGraphQLClient("token", null, handler, (_, _) => Task.CompletedTask)
            {
                SensitiveDiagnostics = sink,
                OnRetry = messages.Add,
            };
            var exception = await Assert.ThrowsAsync<GitHubGraphQLException>(() => client.GetViewerLoginAsync(TestContext.Current.CancellationToken));
            Assert.Equal(2, exception.RetryCount);
            Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, string.Join('\n', messages), StringComparison.Ordinal);
            sink.Dispose();
            var lines = await File.ReadAllLinesAsync(sink.FilePath, TestContext.Current.CancellationToken);
            Assert.Equal(3, lines.Length);
            for (var index = 0; index < lines.Length; index++)
            {
                using var entry = JsonDocument.Parse(lines[index]);
                Assert.Equal(index, entry.RootElement.GetProperty("retryCount").GetInt32());
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Concurrent_records_are_bounded_complete_and_owner_only_on_unix()
    {
        var directory = CreateDirectory();
        try
        {
            var warnings = new List<string>();
            using var sink = new SensitiveApiDiagnostics(directory, warnings.Add);
            var body = new string('x', SensitiveApiDiagnostics.MaximumBodyCharacters) + Secret;
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
                sink.Record(ApiOperation.RestGet, HttpStatusCode.BadRequest, Secret, 0, body), TestContext.Current.CancellationToken)));
            sink.Dispose();
            Assert.Single(warnings);
            var lines = await File.ReadAllLinesAsync(sink.FilePath, TestContext.Current.CancellationToken);
            Assert.Equal(8, lines.Length);
            foreach (var line in lines)
            {
                using var entry = JsonDocument.Parse(line);
                Assert.True(entry.RootElement.GetProperty("truncated").GetBoolean());
                Assert.Equal(body.Length, entry.RootElement.GetProperty("originalBodyCharacters").GetInt32());
                Assert.Equal(SensitiveApiDiagnostics.MaximumBodyCharacters, entry.RootElement.GetProperty("body").GetString()!.Length);
                Assert.Equal("[redacted]", entry.RootElement.GetProperty("requestId").GetString());
            }

            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(sink.FilePath));
            }

            Assert.Throws<ObjectDisposedException>(() => sink.Record(ApiOperation.RestGet, HttpStatusCode.BadRequest, null, 0, Secret));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task File_failure_is_explicit_does_not_clobber_or_expose_body_and_create_stays_ambiguous()
    {
        var directory = CreateDirectory();
        var messages = new List<string>();
        try
        {
            using var sink = new SensitiveApiDiagnostics(directory, messages.Add);
            await File.WriteAllTextAsync(sink.FilePath, "existing", TestContext.Current.CancellationToken);
            using var handler = new StubHandler(Response(HttpStatusCode.BadGateway, Secret));
            using var client = new GitHubGraphQLClient("token", null, handler, (_, _) => Task.CompletedTask) { SensitiveDiagnostics = sink };
            var exception = await Assert.ThrowsAsync<AmbiguousMutationResultException>(() => client.MutationAsync(
                "createProjectV2", "mutation { createProjectV2 { projectV2 { id } } }",
                requiredResultPath: "projectV2.id", cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal("sensitive-diagnostic-write-failure", exception.FailureReason);
            Assert.IsType<IOException>(exception.InnerException);
            Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
            Assert.Contains(messages, message => message.StartsWith("error:", StringComparison.Ordinal));
            Assert.DoesNotContain(Secret, string.Join('\n', messages), StringComparison.Ordinal);
            Assert.Equal("existing", await File.ReadAllTextAsync(sink.FilePath, TestContext.Current.CancellationToken));
            Assert.Throws<IOException>(() => sink.Record(ApiOperation.RestGet, HttpStatusCode.BadRequest, null, 0, Secret));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transport_exception_details_are_not_logged_or_nested(bool rest)
    {
        var messages = new List<string>();
        using var handler = new ThrowingHandler();
        if (rest)
        {
            using var client = new GitHubRestClient("token", null, handler);
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("synthetic", TestContext.Current.CancellationToken));
            Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        }
        else
        {
            using var client = new GitHubGraphQLClient("token", null, handler, (_, _) => Task.CompletedTask) { OnRetry = messages.Add };
            var error = await Assert.ThrowsAsync<GitHubGraphQLException>(() => client.GetViewerLoginAsync(TestContext.Current.CancellationToken));
            Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
            Assert.Equal(3, error.RetryCount);
        }

        Assert.DoesNotContain(Secret, string.Join('\n', messages), StringComparison.Ordinal);
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "diagnostics-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
        response.Headers.Add("X-GitHub-Request-Id", "ABCD:1234:5678:9ABC:01234567");
        return response;
    }

    private sealed class StubHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_responses.Dequeue());
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException(Secret);
    }
}
