using System.Net;
using System.Text.Json;
using Ghpmv.Cli;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;

namespace Ghpmv.Core.Tests;

public sealed class ApiDiagnosticCorrelationTests
{
    private const string Secret = "SYNTHETIC-CORRELATION-BODY-SECRET";
    private const string ErrorBody = """{"errors":[{"type":"FORBIDDEN","message":"SYNTHETIC-CORRELATION-BODY-SECRET"}]}""";

    [Theory]
    [InlineData(200, null)]
    [InlineData(401, null)]
    [InlineData(200, "SYNTHETIC-PRIVATE-HEADER")]
    [InlineData(403, "SYNTHETIC-PRIVATE-HEADER")]
    public async Task Graphql_exception_report_and_raw_record_join_without_a_vendor_request_id(int status, string? requestId)
    {
        using var fixture = new Fixture();
        using var handler = new Handler(_ => Reply((HttpStatusCode)status, ErrorBody, requestId));
        using var client = GraphQl(fixture.Session, handler);
        var error = await Assert.ThrowsAsync<GitHubGraphQLException>(() =>
            client.QueryAsync("query { viewer { login } }", new { privateInput = "SYNTHETIC-INPUT-SECRET" }, TestContext.Current.CancellationToken));
        Assert.Equal(requestId is null ? null : "[redacted]", error.RequestId);
        var attempt = Assert.IsType<ApiRequestAttempt>(error.RequestAttempt);
        using var report = await fixture.ReportAsync(error);
        fixture.Sink!.Dispose();
        using var raw = JsonDocument.Parse(Assert.Single(await File.ReadAllLinesAsync(fixture.Sink.FilePath, TestContext.Current.CancellationToken)));
        AssertJoin(fixture, attempt, report.RootElement, raw.RootElement);
        Assert.Equal(ErrorBody, raw.RootElement.GetProperty("body").GetString());
        Assert.Contains(attempt.AttemptId, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, report.RootElement.GetRawText() + error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC-INPUT-SECRET", raw.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC-TOKEN-SECRET", raw.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rest_and_probe_failures_keep_attempt_metadata_in_safe_reports(bool probe)
    {
        using var fixture = new Fixture();
        using var handler = new Handler(_ => Reply(HttpStatusCode.Forbidden, Secret, "SYNTHETIC-PRIVATE-HEADER"));
        using var client = new GitHubRestClient("SYNTHETIC-TOKEN-SECRET", null, handler) { DiagnosticSession = fixture.Session };
        Exception error = probe
            ? await Assert.ThrowsAsync<InvalidOperationException>(() => ImportCapabilityPreflight.ValidateAsync(
                new(true, false, false, false, []), "synthetic", new Dictionary<string, string>(), client, TestContext.Current.CancellationToken))
            : await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync("synthetic", new { input = "SYNTHETIC-INPUT" }, TestContext.Current.CancellationToken));
        var diagnostic = Assert.IsType<GitHubRestFailureDiagnostic>(GitHubRestClient.GetFailureDiagnostic(error));
        var attempt = Assert.IsType<ApiRequestAttempt>(diagnostic.RequestAttempt);
        using var report = await fixture.ReportAsync(error);
        fixture.Sink!.Dispose();
        using var raw = JsonDocument.Parse(Assert.Single(await File.ReadAllLinesAsync(fixture.Sink.FilePath, TestContext.Current.CancellationToken)));
        AssertJoin(fixture, attempt, report.RootElement, raw.RootElement);
        var detail = Assert.Single(report.RootElement.GetProperty("exceptions").EnumerateArray());
        Assert.Equal(probe ? "RestValidationProbe" : "RestPost", detail.GetProperty("operationKind").GetString());
        Assert.Equal("403 Forbidden", detail.GetProperty("statusCode").GetString());
        Assert.DoesNotContain(Secret, error.ToString() + report.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retries_use_new_ids_and_final_transport_failure_does_not_claim_an_earlier_raw_response()
    {
        using var fixture = new Fixture();
        using var handler = new Handler(index => index switch
        {
            0 => Reply(HttpStatusCode.BadGateway, Secret, "ABCD:1234"),
            1 => Reply(HttpStatusCode.BadGateway, Secret, "SYNTHETIC-PRIVATE-HEADER"),
            2 => Reply(HttpStatusCode.BadGateway, Secret),
            _ => throw new HttpRequestException(Secret),
        });
        using var client = GraphQl(fixture.Session, handler);
        var error = await Assert.ThrowsAsync<GitHubGraphQLException>(() => client.QueryAsync("query { viewer { login } }", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(3, error.RetryCount);
        Assert.Null(error.RequestId);
        var attempt = Assert.IsType<ApiRequestAttempt>(error.RequestAttempt);
        Assert.Equal("not-captured", attempt.SensitiveResponseCapture);
        using var report = await fixture.ReportAsync(error);
        Assert.Equal(fixture.Sink!.FilePath, report.RootElement.GetProperty("sensitiveDiagnosticsFile").GetString());
        fixture.Sink.Dispose();
        var lines = await File.ReadAllLinesAsync(fixture.Sink.FilePath, TestContext.Current.CancellationToken);
        Assert.Equal(3, lines.Length);
        var ids = new HashSet<string>(StringComparer.Ordinal) { attempt.AttemptId };
        for (var index = 0; index < lines.Length; index++)
        {
            using var raw = JsonDocument.Parse(lines[index]);
            Assert.Equal(fixture.Session.RunId, raw.RootElement.GetProperty("runId").GetString());
            Assert.True(ids.Add(raw.RootElement.GetProperty("attemptId").GetString()!));
            Assert.Equal(index, raw.RootElement.GetProperty("retryCount").GetInt32());
            Assert.Equal(index switch { 0 => "ABCD:1234", 1 => "[redacted]", _ => null }, raw.RootElement.GetProperty("requestId").GetString());
        }
    }

    [Fact]
    public async Task Graphql_retries_join_only_the_final_response_and_do_not_change_logical_mutation_identity()
    {
        using var fixture = new Fixture();
        var mutationIds = new List<string>();
        using var handler = new Handler(index => Reply(HttpStatusCode.OK, index < 2
            ? """{"errors":[{"type":"UNPROCESSABLE","message":"temporary conflict SYNTHETIC-CORRELATION-BODY-SECRET"}]}"""
            : ErrorBody), request =>
            {
                using var json = JsonDocument.Parse(request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult());
                mutationIds.Add(json.RootElement.GetProperty("variables").GetProperty("clientMutationId").GetString()!);
            });
        using var client = GraphQl(fixture.Session, handler);
        var error = await Assert.ThrowsAsync<GitHubGraphQLException>(() => client.MutationAsync(
            "createSynthetic", "mutation { createSynthetic { id } }", clientMutationId: "logical-synthetic-operation",
            requiredResultPath: "id", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, error.RetryCount);
        Assert.All(mutationIds, id => Assert.Equal("logical-synthetic-operation", id));
        using var report = await fixture.ReportAsync(error);
        fixture.Sink!.Dispose();
        var lines = await File.ReadAllLinesAsync(fixture.Sink.FilePath, TestContext.Current.CancellationToken);
        Assert.Equal(3, lines.Length);
        var attempts = lines.Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        Assert.Equal(3, attempts.Select(entry => entry.GetProperty("attemptId").GetString()).Distinct().Count());
        AssertJoin(fixture, error.RequestAttempt!, report.RootElement, attempts[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_session_includes_nested_clients_and_keeps_primary_and_cleanup_attempts_separate(bool enabled)
    {
        using var fixture = new Fixture(enabled);
        using var graphHandler = new Handler(_ => Reply(HttpStatusCode.OK, ErrorBody));
        using var graph = GraphQl(fixture.Session, graphHandler);
        var primary = await Assert.ThrowsAsync<GitHubGraphQLException>(() => graph.QueryAsync("query { viewer { login } }", cancellationToken: TestContext.Current.CancellationToken));
        using var restHandler = new Handler(_ => Reply(HttpStatusCode.Forbidden, Secret));
        using var rest = new GitHubRestClient("synthetic", null, restHandler) { DiagnosticSession = fixture.Session };
        var cleanup = await Assert.ThrowsAsync<HttpRequestException>(() => rest.GetAsync("synthetic", TestContext.Current.CancellationToken));
        async Task<GitHubGraphQLException> NestedAsync()
        {
            using var handler = new Handler(_ => Reply(HttpStatusCode.OK, ErrorBody));
            using var nested = GraphQl(fixture.Session, handler);
            return await Assert.ThrowsAsync<GitHubGraphQLException>(() => nested.QueryAsync("query { viewer { login } }", cancellationToken: TestContext.Current.CancellationToken));
        }
        var nestedError = await NestedAsync();
        var attempts = new[] { primary, cleanup, (Exception)nestedError }.Select(ApiDiagnosticSession.GetAttempt).ToArray();
        Assert.All(attempts, attempt => Assert.Equal(fixture.Session.RunId, attempt!.RunId));
        Assert.Equal(3, attempts.Select(attempt => attempt!.AttemptId).Distinct().Count());
        using var diagnostics = new ImportFailureDiagnostics("synthetic", "organization", null, false, fixture.Session);
        diagnostics.SetStage("importing-project");
        var lines = new List<string>();
        await new ImportFailureFinalizer(diagnostics, fixture.Directory, lines.Add).CompleteAsync(
            primary, () => Task.FromException(cleanup), null);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Directory, "import-error.json"), TestContext.Current.CancellationToken));
        var details = report.RootElement.GetProperty("exceptions").EnumerateArray().ToArray();
        Assert.Equal(attempts[0]!.AttemptId, details[1].GetProperty("attemptId").GetString());
        Assert.Equal(attempts[1]!.AttemptId, details[2].GetProperty("attemptId").GetString());
        var cleanupDetail = report.RootElement.GetProperty("cleanupFailures")[0].GetProperty("exceptions")[0];
        Assert.Equal(attempts[1]!.AttemptId, cleanupDetail.GetProperty("attemptId").GetString());
        Assert.DoesNotContain(Secret, string.Join('\n', lines) + report.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.NotEqual(fixture.Session.RunId, new ApiDiagnosticSession().RunId);
        if (!enabled) Assert.Empty(System.IO.Directory.GetFiles(fixture.Directory, "ghpmv-sensitive-api-*"));
    }

    [Theory]
    [InlineData(false, "transport")]
    [InlineData(true, "transport")]
    [InlineData(true, "malformed")]
    [InlineData(true, "missing")]
    [InlineData(true, "local")]
    [InlineData(true, "success")]
    public async Task No_capture_does_not_claim_a_file_or_record(bool enabled, string kind)
    {
        using var fixture = new Fixture(enabled);
        using var handler = new Handler(_ => kind switch
        {
            "transport" => throw new HttpRequestException(Secret),
            "malformed" => Reply(HttpStatusCode.OK, Secret),
            "missing" => Reply(HttpStatusCode.OK, "{}"),
            _ => Reply(HttpStatusCode.OK, """{"data":{"viewer":{"login":"synthetic"}}}"""),
        });
        using var client = GraphQl(fixture.Session, handler);
        Exception error;
        if (kind == "local")
        {
            error = await Assert.ThrowsAsync<ArgumentException>(() => client.QueryAsync(" ", cancellationToken: TestContext.Current.CancellationToken));
        }
        else if (kind == "success")
        {
            await client.QueryAsync("query { viewer { login } }", cancellationToken: TestContext.Current.CancellationToken);
            error = new InvalidOperationException("Synthetic local failure after success.");
        }
        else
        {
            error = await Assert.ThrowsAsync<GitHubGraphQLException>(() => client.QueryAsync("query { viewer { login } }", cancellationToken: TestContext.Current.CancellationToken));
        }
        using var report = await fixture.ReportAsync(error);
        Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("sensitiveDiagnosticsFile").ValueKind);
        Assert.Equal(enabled ? "not-created" : "disabled", report.RootElement.GetProperty("sensitiveDiagnosticsState").GetString());
        var attempt = ApiDiagnosticSession.GetAttempt(error);
        if (kind is "local" or "success") Assert.Null(attempt);
        else Assert.Equal(enabled ? "not-captured" : "disabled", Assert.IsType<ApiRequestAttempt>(attempt).SensitiveResponseCapture);
        Assert.Empty(System.IO.Directory.GetFiles(fixture.Directory, "ghpmv-sensitive-api-*"));
    }

    [Fact]
    public async Task Collision_is_not_misrepresented_as_an_owned_file_and_create_failure_is_ambiguous()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.Sink!.FilePath, "synthetic-existing-file", TestContext.Current.CancellationToken);
        using var handler = new Handler(_ => Reply(HttpStatusCode.BadGateway, Secret));
        using var client = GraphQl(fixture.Session, handler);
        var error = await Assert.ThrowsAsync<AmbiguousMutationResultException>(() => client.MutationAsync(
            "createSynthetic", "mutation { createSynthetic { id } }", requiredResultPath: "id", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("sensitive-diagnostic-write-failure", error.FailureReason);
        Assert.IsType<IOException>(error.InnerException);
        Assert.Equal("failed", error.RequestAttempt!.SensitiveResponseCapture);
        Assert.Same(error.RequestAttempt, ApiDiagnosticSession.GetAttempt(error.InnerException!));
        using var report = await fixture.ReportAsync(error);
        Assert.Equal("unavailable", report.RootElement.GetProperty("sensitiveDiagnosticsState").GetString());
        Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("sensitiveDiagnosticsFile").ValueKind);
        Assert.Equal("synthetic-existing-file", await File.ReadAllTextAsync(fixture.Sink.FilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Failed_flush_retains_an_owned_partial_file_but_does_not_claim_a_flushed_record()
    {
        using var fixture = new Fixture(enabled: false);
        using var sink = new SensitiveApiDiagnostics(fixture.Directory, _ => { }, fixture.Session)
        {
            OpenFile = static (path, options) => new FailingFlushStream(path, options),
        };
        using var handler = new Handler(_ => Reply(HttpStatusCode.OK, ErrorBody));
        using var client = GraphQl(fixture.Session, handler);
        var first = await Assert.ThrowsAsync<GitHubGraphQLException>(() => client.QueryAsync("query { viewer { login } }", cancellationToken: TestContext.Current.CancellationToken));
        var second = await Assert.ThrowsAsync<IOException>(() => client.QueryAsync("query { viewer { login } }", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("captured", first.RequestAttempt!.SensitiveResponseCapture);
        Assert.Equal("failed", ApiDiagnosticSession.GetAttempt(second)!.SensitiveResponseCapture);
        Assert.NotEqual(first.RequestAttempt.AttemptId, ApiDiagnosticSession.GetAttempt(second)!.AttemptId);
        using var report = await fixture.ReportAsync(second);
        Assert.Equal("partial", report.RootElement.GetProperty("sensitiveDiagnosticsState").GetString());
        Assert.Equal(sink.FilePath, report.RootElement.GetProperty("sensitiveDiagnosticsFile").GetString());
        sink.Dispose();
        Assert.True(File.Exists(sink.FilePath));
        Assert.DoesNotContain(Secret, report.RootElement.GetRawText() + second.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_finalizer_records_close_failure_before_saving_file_state_without_replacing_primary_ids()
    {
        using var fixture = new Fixture(enabled: false);
        using var sink = new SensitiveApiDiagnostics(fixture.Directory, _ => { }, fixture.Session)
        {
            OpenFile = static (path, options) => new FailingCloseStream(path, options),
        };
        using var handler = new Handler(_ => Reply(HttpStatusCode.OK, ErrorBody));
        using var client = GraphQl(fixture.Session, handler);
        var primary = await Assert.ThrowsAsync<GitHubGraphQLException>(() => client.QueryAsync("query { viewer { login } }", cancellationToken: TestContext.Current.CancellationToken));
        using var diagnostics = new ImportFailureDiagnostics("synthetic", "organization", null, false, fixture.Session);
        var final = await new ImportFailureFinalizer(diagnostics, fixture.Directory, _ => { }).CompleteAsync(primary, null, null, sink.Dispose);
        Assert.IsType<AggregateException>(final);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Directory, "import-error.json"), TestContext.Current.CancellationToken));
        Assert.Equal("partial", report.RootElement.GetProperty("sensitiveDiagnosticsState").GetString());
        Assert.Equal(sink.FilePath, report.RootElement.GetProperty("sensitiveDiagnosticsFile").GetString());
        Assert.Equal(primary.RequestAttempt!.AttemptId, report.RootElement.GetProperty("exceptions")[1].GetProperty("attemptId").GetString());
        Assert.Equal("disposing-api-diagnostics", report.RootElement.GetProperty("cleanupFailures")[0].GetProperty("stage").GetString());
        Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("cleanupFailures")[0].GetProperty("exceptions")[0].GetProperty("attemptId").ValueKind);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Timeout_and_response_read_failure_have_local_attempt_ids_without_raw_records(bool rest, bool timeout)
    {
        using var fixture = new Fixture();
        using var handler = new Handler(_ => timeout
            ? throw new TaskCanceledException(Secret)
            : new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new UnreadableContent() });
        Exception error;
        if (rest)
        {
            using var client = new GitHubRestClient("synthetic", null, handler) { DiagnosticSession = fixture.Session };
            error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("synthetic", TestContext.Current.CancellationToken));
        }
        else
        {
            using var client = GraphQl(fixture.Session, handler);
            error = await Assert.ThrowsAsync<GitHubGraphQLException>(() => client.QueryAsync("query { viewer { login } }", cancellationToken: TestContext.Current.CancellationToken));
        }
        var attempt = Assert.IsType<ApiRequestAttempt>(ApiDiagnosticSession.GetAttempt(error));
        Assert.Equal(fixture.Session.RunId, attempt.RunId);
        Assert.Equal("not-captured", attempt.SensitiveResponseCapture);
        using var report = await fixture.ReportAsync(error);
        Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("sensitiveDiagnosticsFile").ValueKind);
        Assert.DoesNotContain(Secret, report.RootElement.GetRawText() + error.ToString(), StringComparison.Ordinal);
    }

    private static void AssertJoin(Fixture fixture, ApiRequestAttempt attempt, JsonElement report, JsonElement raw)
    {
        Assert.Equal(fixture.Session.RunId, report.GetProperty("runId").GetString());
        Assert.Equal(fixture.Sink!.FilePath, report.GetProperty("sensitiveDiagnosticsFile").GetString());
        Assert.True(Path.IsPathFullyQualified(report.GetProperty("sensitiveDiagnosticsFile").GetString()!));
        Assert.Equal("available", report.GetProperty("sensitiveDiagnosticsState").GetString());
        var detail = Assert.Single(report.GetProperty("exceptions").EnumerateArray());
        Assert.Equal(attempt.RunId, detail.GetProperty("runId").GetString());
        Assert.Equal(attempt.AttemptId, detail.GetProperty("attemptId").GetString());
        Assert.Equal(attempt.RunId, raw.GetProperty("runId").GetString());
        Assert.Equal(attempt.AttemptId, raw.GetProperty("attemptId").GetString());
        Assert.Equal("captured", detail.GetProperty("sensitiveResponseCapture").GetString());
    }

    private static GitHubGraphQLClient GraphQl(ApiDiagnosticSession session, HttpMessageHandler handler) =>
        new("SYNTHETIC-TOKEN-SECRET", null, handler, (_, _) => Task.CompletedTask) { DiagnosticSession = session };

    private static HttpResponseMessage Reply(HttpStatusCode status, string body, string? requestId = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
        if (requestId is not null) response.Headers.TryAddWithoutValidation("X-GitHub-Request-Id", requestId);
        return response;
    }

    private sealed class Handler(Func<int, HttpResponseMessage> response, Action<HttpRequestMessage>? observe = null) : HttpMessageHandler
    {
        private int _count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            observe?.Invoke(request);
            return Task.FromResult(response(_count++));
        }
    }

    private sealed class FailingFlushStream(string path, FileStreamOptions options) : FileStream(path, options)
    {
        private int _flushes;
        public override void Flush(bool flushToDisk)
        {
            if (++_flushes == 2) throw new IOException("Synthetic flush failure.");
            base.Flush(flushToDisk);
        }
    }

    private sealed class UnreadableContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromException(new HttpRequestException(Secret));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class FailingCloseStream(string path, FileStreamOptions options) : FileStream(path, options)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) throw new IOException("Synthetic close failure.");
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(bool enabled = true)
        {
            System.IO.Directory.CreateDirectory(Directory);
            if (enabled) Sink = new SensitiveApiDiagnostics(Directory, _ => { }, Session);
        }

        public string Directory { get; } = Path.Combine(Environment.CurrentDirectory, "correlation-test-" + Guid.NewGuid().ToString("N"));
        public ApiDiagnosticSession Session { get; } = new();
        public SensitiveApiDiagnostics? Sink { get; }

        public async Task<JsonDocument> ReportAsync(Exception error)
        {
            var reportDirectory = Path.Combine(Directory, "reports");
            System.IO.Directory.CreateDirectory(reportDirectory);
            using var diagnostics = new ImportFailureDiagnostics("synthetic", "organization", null, false, Session);
            var lines = new List<string>();
            diagnostics.WriteFailure(error, lines.Add);
            Assert.Contains($"runId: {Session.RunId}", lines);
            if (ApiDiagnosticSession.GetAttempt(error) is { } attempt)
                Assert.Contains($"attemptId: {attempt.AttemptId}", lines);
            Assert.DoesNotContain(Secret, string.Join('\n', lines), StringComparison.Ordinal);
            var path = await diagnostics.SaveFailureAsync(reportDirectory, error, TestContext.Current.CancellationToken);
            return JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }

        public void Dispose()
        {
            Sink?.Dispose();
            System.IO.Directory.Delete(Directory, true);
        }
    }
}
