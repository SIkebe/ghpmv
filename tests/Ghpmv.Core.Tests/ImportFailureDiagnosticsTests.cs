using System.Text.Json;
using Ghpmv.Cli;

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
