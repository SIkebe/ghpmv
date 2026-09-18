using System.Text.Json;
using Ghpmv.Cli;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;

namespace Ghpmv.Core.Tests;

public sealed class ImportFailureDiagnosticsTests
{
    [Fact]
    public async Task Cleanup_retains_distinct_context_and_escapes_identity_in_real_report_and_stderr_formatter()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "cleanup-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var diagnostics = CreateDiagnostics();
        diagnostics.SetStage("importing-project");
        diagnostics.SetTargetProject(34, "https://user:SYNTHETIC-URL-SECRET@target.example.test/orgs/target/projects/34?token=SYNTHETIC-URL-SECRET#SYNTHETIC-URL-SECRET");
        var primary = new GitHubGraphQLException("SYNTHETIC-RESPONSE-SECRET") { ErrorType = "UNPROCESSABLE" };
        using (MigrationDiagnostics.Begin(new()
        {
            Source = new()
            {
                Owner = "source-org", Number = 12, Title = "Demo project",
                Host = "https://user:SYNTHETIC-HOST-SECRET@source.example.test/path?token=SYNTHETIC-HOST-SECRET",
            },
            Target = new() { Owner = "target-org", Number = 34, Title = "Demo project" },
            Element = new() { Kind = "Field", Name = "Sprint\nerror: forged\u001b", DataType = "ITERATION" },
            Stage = "importing-project", Operation = "createProjectV2Field",
        }))
        {
            MigrationDiagnostics.Attach(primary);
        }
        var lines = new List<string>();
        diagnostics.WriteFailure(primary, lines.Add);
        try
        {
            var final = await new ImportFailureFinalizer(diagnostics, directory, lines.Add).CompleteAsync(
                primary, () => Task.FromException(new GitHubGraphQLException("SYNTHETIC-CLEANUP-SECRET")),
                disposeBrowserAsync: null);
            Assert.IsType<AggregateException>(final);
            using var report = await LoadReportAsync(directory, TestContext.Current.CancellationToken);
            var root = report.RootElement;
            Assert.Equal("importing-project", root.GetProperty("stage").GetString());
            Assert.Equal("Sprint\\u000aerror: forged\\u001b", root.GetProperty("context").GetProperty("element").GetProperty("name").GetString());
            Assert.Equal("createProjectV2Field", root.GetProperty("context").GetProperty("operation").GetString());
            var cleanup = Assert.Single(root.GetProperty("cleanupFailures").EnumerateArray());
            Assert.Equal("restoring-template-state", cleanup.GetProperty("context").GetProperty("stage").GetString());
            Assert.Equal("Project", cleanup.GetProperty("context").GetProperty("element").GetProperty("kind").GetString());
            Assert.Equal("https://target.example.test/orgs/target/projects/34", root.GetProperty("targetProjectUrl").GetString());
            var output = string.Join("\n", lines) + root.GetRawText();
            Assert.Contains("source: source-org / Project 12", output, StringComparison.Ordinal);
            Assert.DoesNotContain("\nerror: forged", output, StringComparison.Ordinal);
            Assert.DoesNotContain("SYNTHETIC-", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Old_reports_and_resume_errors_load_without_context()
    {
        var report = JsonSerializer.Deserialize(
            """
            {"occurredAtUtc":"2026-01-01T00:00:00Z","command":"import","targetOwner":"target-org","ownerType":"organization",
             "browserAutomationEnabled":false,"stage":"importing-project","progress":[],"cleanupFailures":[],
             "exceptions":[{"depth":0,"type":"System.InvalidOperationException","message":"old error"}]}
            """, ImportFailureJsonContext.Default.ImportFailureReport);
        Assert.NotNull(report);
        Assert.Null(report.Context);
        Assert.Null(Assert.Single(report.Exceptions).Context);
        Assert.Null(report.RunId);
        Assert.Null(report.SensitiveDiagnosticsFile);
        Assert.Null(report.SensitiveDiagnosticsState);
        Assert.Null(report.Exceptions[0].AttemptId);
        Assert.Null(report.Exceptions[0].SensitiveResponseCapture);
        var log = JsonSerializer.Deserialize(
            """
            {"projectId":"PVT_target","itemStates":{"issue":{"targetItemId":"PVTI_target","fieldValuesError":"old error"}}}
            """, ImportLogJsonContext.Default.ImportLog);
        Assert.NotNull(log);
        var state = Assert.Single(log.ItemStates).Value;
        Assert.Equal("old error", state.FieldValuesError);
        Assert.Null(state.FieldValuesErrorContext);
        Assert.Null(state.FieldValueFailures);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("unauthorized")]
    public async Task Restore_failure_still_disposes_browser_and_saves_diagnostics(string failureKind)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = CreateDirectory();
        var diagnostics = CreateDiagnostics();
        diagnostics.SetStage("finalizing-template-state");
        diagnostics.CaptureFailureStage();
        var browserDisposed = false;
        Exception restoreFailure = failureKind == "json"
            ? new JsonException("invalid import log")
            : new UnauthorizedAccessException("import log is read-only");

        try
        {
            var result = await new ImportFailureFinalizer(diagnostics, directory).CompleteAsync(
                new InvalidOperationException("primary failure"),
                () => Task.FromException(restoreFailure),
                () =>
                {
                    browserDisposed = true;
                    return ValueTask.CompletedTask;
                });

            Assert.True(browserDisposed);
            Assert.IsType<AggregateException>(result);
            using var report = await LoadReportAsync(directory, cancellationToken);
            var cleanup = Assert.Single(report.RootElement.GetProperty("cleanupFailures").EnumerateArray());
            Assert.Equal("restoring-template-state", cleanup.GetProperty("stage").GetString());
            Assert.Equal(restoreFailure.GetType().FullName, cleanup.GetProperty("type").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Browser_disposal_failure_preserves_primary_failure_and_diagnostics()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = CreateDirectory();
        var diagnostics = CreateDiagnostics();
        diagnostics.SetStage("importing-items");
        diagnostics.CaptureFailureStage();
        var primary = new InvalidOperationException("item import failed");

        try
        {
            var result = await new ImportFailureFinalizer(diagnostics, directory).CompleteAsync(
                primary,
                restoreTemplateAsync: null,
                () => ValueTask.FromException(new IOException("browser close failed")));

            var aggregate = Assert.IsType<AggregateException>(result);
            Assert.Same(primary, aggregate.InnerExceptions[0]);
            using var report = await LoadReportAsync(directory, cancellationToken);
            Assert.Equal("importing-items", report.RootElement.GetProperty("stage").GetString());
            var cleanup = Assert.Single(report.RootElement.GetProperty("cleanupFailures").EnumerateArray());
            Assert.Equal("disposing-browser-session", cleanup.GetProperty("stage").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Cleanup_only_failure_is_returned_and_persisted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = CreateDirectory();
        var diagnostics = CreateDiagnostics();

        try
        {
            var cleanupFailure = new IOException("browser close failed");
            var result = await new ImportFailureFinalizer(diagnostics, directory).CompleteAsync(
                importFailure: null,
                restoreTemplateAsync: null,
                () => ValueTask.FromException(cleanupFailure));

            Assert.Same(cleanupFailure, result);
            Assert.True(File.Exists(Path.Combine(directory, ImportFailureDiagnostics.FileName)));
            using var report = await LoadReportAsync(directory, cancellationToken);
            Assert.Equal("disposing-browser-session", report.RootElement.GetProperty("stage").GetString());
            var thrown = Assert.Throws<IOException>(() =>
                ImportFailureFinalizer.ThrowIfCleanupOnlyFailure(
                    failureBeforeCleanup: null,
                    result));
            Assert.Same(cleanupFailure, thrown);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Progress_retains_only_the_latest_two_hundred_entries()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = CreateDirectory();
        var diagnostics = CreateDiagnostics();

        try
        {
            for (var index = 0; index < 205; index++)
            {
                diagnostics.RecordProgress($"progress-{index}");
            }

            await diagnostics.SaveFailureAsync(
                directory,
                new InvalidOperationException("failed"),
                cancellationToken);

            using var report = await LoadReportAsync(directory, cancellationToken);
            var progress = report.RootElement.GetProperty("progress").EnumerateArray().ToArray();
            Assert.Equal(200, progress.Length);
            Assert.Equal("progress-5", progress[0].GetProperty("message").GetString());
            Assert.Equal("progress-204", progress[^1].GetProperty("message").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Diagnostic_save_failure_returns_original_error_and_reports_warning()
    {
        var directory = CreateDirectory();
        var diagnostics = CreateDiagnostics();
        var messages = new List<string>();
        var primary = new InvalidOperationException("primary failure");

        try
        {
            var result = await new ImportFailureFinalizer(
                diagnostics,
                directory,
                messages.Add,
                (_, _) => Task.FromException<string>(
                    new UnauthorizedAccessException("diagnostic path is read-only"))).CompleteAsync(
                    primary,
                    restoreTemplateAsync: null,
                    disposeBrowserAsync: null);

            Assert.Same(primary, result);
            Assert.Contains(
                messages,
                message => message.Contains(
                    "warning: failed to write import-error.json: diagnostic path is read-only",
                    StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("aggregate")]
    [InlineData("cancellation")]
    public async Task Unhandled_primary_failure_is_not_replaced_by_cleanup_failure(string failureKind)
    {
        var directory = CreateDirectory();
        var diagnostics = CreateDiagnostics();
        diagnostics.SetStage("importing-items");
        diagnostics.CaptureFailureStage();
        Exception primary = failureKind == "aggregate"
            ? new AggregateException("field update and archive restoration failed")
            : new OperationCanceledException("cancelled");

        try
        {
            var result = await new ImportFailureFinalizer(diagnostics, directory).CompleteAsync(
                primary,
                restoreTemplateAsync: null,
                () => ValueTask.FromException(new IOException("browser close failed")));

            var aggregate = Assert.IsType<AggregateException>(result);
            Assert.Same(primary, aggregate.InnerExceptions[0]);
            ImportFailureFinalizer.ThrowIfCleanupOnlyFailure(primary, result);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Ambiguous_mutation_preserves_safe_recovery_fields_without_raw_detail()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = CreateDirectory();
        var diagnostics = CreateDiagnostics();
        var attemptedAt = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        var exception = new AmbiguousMutationResultException(
            "createProjectV2",
            "safe-client-mutation-id",
            attemptedAt,
            "organization target / project Roadmap",
            "raw server detail secret-value");

        try
        {
            await diagnostics.SaveFailureAsync(directory, exception, cancellationToken);

            var json = await File.ReadAllTextAsync(
                Path.Combine(directory, ImportFailureDiagnostics.FileName),
                cancellationToken);
            Assert.DoesNotContain("raw server detail", json, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-value", json, StringComparison.Ordinal);
            using var report = JsonDocument.Parse(json);
            var detail = Assert.Single(report.RootElement.GetProperty("exceptions").EnumerateArray());
            Assert.Equal("createProjectV2", detail.GetProperty("operationName").GetString());
            Assert.Equal("safe-client-mutation-id", detail.GetProperty("clientMutationId").GetString());
            Assert.Equal(attemptedAt, detail.GetProperty("attemptedAtUtc").GetDateTimeOffset());
            Assert.Equal(
                "organization target / project Roadmap",
                detail.GetProperty("target").GetString());
            Assert.Equal(
                AmbiguousMutationResultException.RecoveryHint,
                detail.GetProperty("recoveryHint").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Wrapped_graphql_response_is_removed_from_a_normal_exception_message()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = CreateDirectory();
        var diagnostics = CreateDiagnostics();
        var exception = new InvalidOperationException(
            "Team mapping preflight failed for target/platform: " +
            """GraphQL error: [{"type":"FORBIDDEN","message":"unique-sensitive-response"}]""");

        try
        {
            await diagnostics.SaveFailureAsync(directory, exception, cancellationToken);

            var json = await File.ReadAllTextAsync(
                Path.Combine(directory, ImportFailureDiagnostics.FileName),
                cancellationToken);
            Assert.DoesNotContain("unique-sensitive-response", json, StringComparison.Ordinal);
            Assert.DoesNotContain("[{\"type\":\"FORBIDDEN\"", json, StringComparison.Ordinal);
            using var report = JsonDocument.Parse(json);
            var detail = Assert.Single(report.RootElement.GetProperty("exceptions").EnumerateArray());
            Assert.Equal(
                "Team mapping preflight failed for target/platform: GitHub GraphQL request failed.",
                detail.GetProperty("message").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Nested_team_permission_failure_preserves_graphql_metadata_without_raw_response()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = CreateDirectory();
        var diagnostics = CreateDiagnostics();
        var graphQlFailure = new GitHubGraphQLException(
            """GraphQL error: [{"type":"FORBIDDEN","message":"unique-sensitive-response"}]""")
        {
            ErrorsJson = """[{"type":"FORBIDDEN","message":"unique-sensitive-response"}]""",
            ErrorType = "FORBIDDEN",
            StatusCode = System.Net.HttpStatusCode.Forbidden,
            RequestId = "ABCD:1234",
            FailureReason = "graphql-error",
            GraphQlErrors =
            [
                new()
                {
                    Type = "FORBIDDEN",
                    Message = "GitHub reported that the resource is not accessible.",
                    Path = ["organization", "team"],
                },
            ],
        };
        var exception = new InvalidOperationException(
            "Team mapping preflight failed before any project write " +
            "(permission: target Team 'target/platform' could not be read: " +
            "GitHub GraphQL request failed (FORBIDDEN)).",
            new AggregateException("One or more Team permission checks failed.", graphQlFailure));
        MigrationDiagnostics.Attach(graphQlFailure, new()
        {
            Element = new() { Kind = "Team", Name = "target/platform" },
            Operation = "preflight-linked-team",
        });

        try
        {
            await diagnostics.SaveFailureAsync(directory, exception, cancellationToken);

            var json = await File.ReadAllTextAsync(
                Path.Combine(directory, ImportFailureDiagnostics.FileName),
                cancellationToken);
            Assert.DoesNotContain("unique-sensitive-response", json, StringComparison.Ordinal);
            Assert.DoesNotContain("GraphQL error:", json, StringComparison.Ordinal);
            Assert.Contains("target/platform", json, StringComparison.Ordinal);
            using var report = JsonDocument.Parse(json);
            var graphQlDetail = Assert.Single(
                report.RootElement.GetProperty("exceptions").EnumerateArray(),
                detail => detail.GetProperty("type").GetString() ==
                    typeof(GitHubGraphQLException).FullName);
            Assert.Equal("FORBIDDEN", graphQlDetail.GetProperty("errorType").GetString());
            Assert.Equal("403 Forbidden", graphQlDetail.GetProperty("statusCode").GetString());
            Assert.Equal("ABCD:1234", graphQlDetail.GetProperty("requestId").GetString());
            Assert.Equal("graphql-error", graphQlDetail.GetProperty("failureReason").GetString());
            var error = Assert.Single(graphQlDetail.GetProperty("graphQlErrors").EnumerateArray());
            Assert.Equal("GitHub reported that the resource is not accessible.", error.GetProperty("message").GetString());
            Assert.Equal(
                ["organization", "team"],
                error.GetProperty("path").EnumerateArray().Select(segment => segment.GetString()));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(null, null, false, true)]
    [InlineData(null, null, true, false)]
    [InlineData("PVT_created", true, false, true)]
    [InlineData("PVT_created", false, false, false)]
    [InlineData("PVT_created", true, true, false)]
    public void Previous_failure_is_deleted_only_after_a_clean_complete_import(
        string? createdProjectId,
        bool? importCompleted,
        bool hasCurrentWarnings,
        bool expected)
    {
        var log = new ProjectImportLog
        {
            CreatedProjectId = createdProjectId,
            ImportCompleted = importCompleted,
            HasUnresolvedWarnings = hasCurrentWarnings ? true : false,
        };

        Assert.Equal(
            expected,
            ImportFailureDiagnostics.CanDeletePreviousFailure(log, hasCurrentWarnings));
    }

    private static ImportFailureDiagnostics CreateDiagnostics() =>
        new("target", "organization", requestedTargetProjectNumber: null, browserAutomationEnabled: true);

    private static string CreateDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "ghpmv-import-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task<JsonDocument> LoadReportAsync(
        string directory,
        CancellationToken cancellationToken) =>
        JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(directory, ImportFailureDiagnostics.FileName),
            cancellationToken));
}
