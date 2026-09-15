using System.Text.Json;
using Ghpmv.Cli;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;

namespace Ghpmv.Core.Tests;

public sealed class ImportFailureDiagnosticsTests
{
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
