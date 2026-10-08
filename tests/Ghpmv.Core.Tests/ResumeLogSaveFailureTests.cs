using System.Text;
using System.Text.Json;
using Ghpmv.Cli;
using Ghpmv.Core.Import;

namespace Ghpmv.Core.Tests;

public sealed class ResumeLogSaveFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Windows_locked_destination_preserves_original_log_and_records_save_stage(bool projectLog)
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "File.Replace and rename sharing restrictions are Windows-specific.");
        var token = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-save-locked-").FullName;
        var path = Path.Combine(directory, projectLog ? ProjectImportLog.FileName : ImportLog.FileName);
        try
        {
            var itemLog = new ImportLog { ProjectId = "PVT_target", SourceSnapshotFingerprint = "original" };
            var projectImportLog = new ProjectImportLog { CreatedProjectId = "PVT_original" };
            if (projectLog)
                await projectImportLog.SaveAsync(directory, token);
            else
                await itemLog.SaveAsync(directory, token);
            var original = await File.ReadAllTextAsync(path, token);
            Exception? exception;
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                exception = await Record.ExceptionAsync(() => projectLog
                    ? (projectImportLog with { CreatedProjectId = "PVT_replacement" }).SaveAsync(directory, token)
                    : (itemLog with { SourceSnapshotFingerprint = "replacement" }).SaveAsync(directory, token));
            }
            Assert.NotNull(exception);
            Assert.True(exception is IOException or UnauthorizedAccessException);
            var failure = Assert.IsType<ResumeLogSaveFailure>(ResumeLogSaveFailure.Get(exception));
            Assert.Equal(projectLog ? "moving-log" : "replacing-log", failure.Stage);
            Assert.Equal(path, failure.LogPath);
            Assert.Equal(projectLog ? null : Path.Combine(directory, ImportLog.BackupFileName), failure.BackupPath);
            Assert.Equal(Environment.ProcessId, failure.ExecutingProcessId);
            Assert.Equal(original, await File.ReadAllTextAsync(path, token));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Temporary_file_open_failure_is_distinguished_from_directory_creation()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-save-open-").FullName;
        var fileName = Path.Combine("missing-directory", ImportLog.FileName);
        try
        {
            var exception = await Assert.ThrowsAsync<DirectoryNotFoundException>(() => ResumeLogFile.SaveAsync(
                directory, fileName, null, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken));
            var failure = Assert.IsType<ResumeLogSaveFailure>(ResumeLogSaveFailure.Get(exception));
            Assert.Equal("opening-temporary-file", failure.Stage);
            Assert.Equal(Path.Combine(directory, fileName), failure.LogPath);
            Assert.NotNull(failure.TemporaryPath);
            Assert.Null(failure.DirectoryPath);
            Assert.Null(failure.BackupPath);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Replace_failure_records_all_operation_paths_in_the_real_error_report()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-save-replace-").FullName;
        using var diagnostics = new ImportFailureDiagnostics("target", "organization", null, false);
        diagnostics.SetStage("importing-items");
        try
        {
            var original = new ImportLog { ProjectId = "PVT_target", SourceSnapshotFingerprint = "original" };
            await original.SaveAsync(directory, token);
            var path = Path.Combine(directory, ImportLog.FileName);
            var backupPath = Path.Combine(directory, ImportLog.BackupFileName);
            Directory.CreateDirectory(backupPath);

            var exception = await Record.ExceptionAsync(() =>
                (original with { SourceSnapshotFingerprint = "replacement" }).SaveAsync(directory, token));
            Assert.NotNull(exception);
            Assert.True(exception is IOException or UnauthorizedAccessException);
            var saveFailure = Assert.IsType<ResumeLogSaveFailure>(ResumeLogSaveFailure.Get(exception));
            Assert.Equal("replacing-log", saveFailure.Stage);
            Assert.Equal(path, saveFailure.LogPath);
            Assert.Equal(backupPath, saveFailure.BackupPath);
            Assert.StartsWith(path + ".", saveFailure.TemporaryPath, StringComparison.Ordinal);
            Assert.EndsWith(".tmp", saveFailure.TemporaryPath, StringComparison.Ordinal);
            Assert.Null(saveFailure.DirectoryPath);
            Assert.Equal(Environment.ProcessId, saveFailure.ExecutingProcessId);
            Assert.Equal("original", (await ImportLog.LoadAsync(directory, token))!.SourceSnapshotFingerprint);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));

            var result = await new ImportFailureFinalizer(diagnostics, directory, _ => { })
                .CompleteAsync(exception, null, null);
            Assert.Same(exception, result);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(directory, ImportFailureDiagnostics.FileName), token));
            Assert.Equal("importing-items", report.RootElement.GetProperty("stage").GetString());
            Assert.False(string.IsNullOrEmpty(report.RootElement.GetProperty("runId").GetString()));
            var detail = Assert.Single(report.RootElement.GetProperty("exceptions").EnumerateArray());
            Assert.Equal($"0x{exception.HResult:X8}", detail.GetProperty("hResult").GetString());
            var context = detail.GetProperty("resumeLogSaveFailure");
            Assert.Equal(saveFailure.Stage, context.GetProperty("stage").GetString());
            Assert.Equal(path, context.GetProperty("logPath").GetString());
            Assert.Equal(backupPath, context.GetProperty("backupPath").GetString());
            Assert.Equal(saveFailure.TemporaryPath, context.GetProperty("temporaryPath").GetString());
            Assert.Equal(Environment.ProcessId, context.GetProperty("executingProcessId").GetInt32());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Move_failure_does_not_claim_a_backup_was_involved(bool projectLog)
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-save-move-").FullName;
        var fileName = projectLog ? ProjectImportLog.FileName : ImportLog.FileName;
        var path = Path.Combine(directory, fileName);
        try
        {
            Directory.CreateDirectory(path);
            var exception = await Record.ExceptionAsync(() => projectLog
                ? new ProjectImportLog().SaveAsync(directory, token)
                : new ImportLog { ProjectId = "PVT_target" }.SaveAsync(directory, token));
            Assert.NotNull(exception);
            Assert.True(exception is IOException or UnauthorizedAccessException);
            var failure = Assert.IsType<ResumeLogSaveFailure>(ResumeLogSaveFailure.Get(exception));
            Assert.Equal("moving-log", failure.Stage);
            Assert.Equal(path, failure.LogPath);
            Assert.Null(failure.BackupPath);
            Assert.NotNull(failure.TemporaryPath);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Directory_creation_failure_has_no_temporary_or_backup_operation()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-save-directory-").FullName;
        var occupiedPath = Path.Combine(directory, "occupied");
        try
        {
            await File.WriteAllTextAsync(occupiedPath, "existing file", token);
            var exception = await Assert.ThrowsAsync<IOException>(() =>
                new ProjectImportLog().SaveAsync(occupiedPath, token));
            var failure = Assert.IsType<ResumeLogSaveFailure>(ResumeLogSaveFailure.Get(exception));
            Assert.Equal("creating-directory", failure.Stage);
            Assert.Equal(occupiedPath, failure.DirectoryPath);
            Assert.Equal(Path.Combine(occupiedPath, ProjectImportLog.FileName), failure.LogPath);
            Assert.Null(failure.TemporaryPath);
            Assert.Null(failure.BackupPath);
            Assert.Equal("existing file", await File.ReadAllTextAsync(occupiedPath, token));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Write_and_delete_failures_keep_original_exception_first_and_report_cleanup_separately()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-save-cleanup-").FullName;
        var primary = new IOException("synthetic write failure", unchecked((int)0x80070020));
        var cleanup = new UnauthorizedAccessException("synthetic cleanup failure");
        using var diagnostics = new ImportFailureDiagnostics("target", "organization", null, false);
        try
        {
            var exception = await Assert.ThrowsAsync<AggregateException>(() => ResumeLogFile.SaveAsync(
                directory, ImportLog.FileName, ImportLog.BackupFileName,
                (_, _) => Task.FromException(primary),
                TestContext.Current.CancellationToken,
                _ => throw cleanup));
            Assert.Equal([primary, cleanup], exception.InnerExceptions);
            Assert.Equal("writing-temporary-file", ResumeLogSaveFailure.Get(primary)!.Stage);
            Assert.Equal("deleting-temporary-file", ResumeLogSaveFailure.Get(cleanup)!.Stage);
            Assert.Null(ResumeLogSaveFailure.Get(cleanup)!.BackupPath);
            Assert.False(File.Exists(Path.Combine(directory, ImportLog.FileName)));
            Assert.Single(Directory.GetFiles(directory, "*.tmp"));

            await diagnostics.SaveFailureAsync(directory, exception, TestContext.Current.CancellationToken);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(directory, ImportFailureDiagnostics.FileName), TestContext.Current.CancellationToken));
            var details = report.RootElement.GetProperty("exceptions").EnumerateArray().ToArray();
            Assert.Equal(3, details.Length);
            Assert.Equal(1, details[1].GetProperty("depth").GetInt32());
            Assert.Equal("0x80070020", details[1].GetProperty("hResult").GetString());
            Assert.Equal("writing-temporary-file",
                details[1].GetProperty("resumeLogSaveFailure").GetProperty("stage").GetString());
            Assert.Equal(typeof(UnauthorizedAccessException).FullName, details[2].GetProperty("type").GetString());
            Assert.Equal(1, details[2].GetProperty("depth").GetInt32());
            Assert.Equal($"0x{cleanup.HResult:X8}", details[2].GetProperty("hResult").GetString());
            Assert.Equal("deleting-temporary-file",
                details[2].GetProperty("resumeLogSaveFailure").GetProperty("stage").GetString());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Cleanup_only_failure_is_not_reported_as_success()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-save-cleanup-only-").FullName;
        var cleanup = new IOException("synthetic cleanup failure");
        try
        {
            var exception = await Assert.ThrowsAsync<IOException>(() => ResumeLogFile.SaveAsync(
                directory, ProjectImportLog.FileName, null,
                (stream, token) => stream.WriteAsync(Encoding.UTF8.GetBytes("{}"), token).AsTask(),
                TestContext.Current.CancellationToken,
                _ => throw cleanup));
            Assert.Same(cleanup, exception);
            Assert.Equal("deleting-temporary-file", ResumeLogSaveFailure.Get(exception)!.Stage);
            Assert.Equal("{}", await File.ReadAllTextAsync(
                Path.Combine(directory, ProjectImportLog.FileName), TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Cancellation_preserves_type_token_and_original_log()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-save-cancel-").FullName;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            var original = new ImportLog { ProjectId = "PVT_target", SourceSnapshotFingerprint = "original" };
            await original.SaveAsync(directory, cancellation.Token);
            cancellation.Cancel();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                (original with { SourceSnapshotFingerprint = "replacement" }).SaveAsync(directory, cancellation.Token));
            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.Equal("writing-temporary-file", ResumeLogSaveFailure.Get(exception)!.Stage);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            Assert.Equal("original", (await ImportLog.LoadAsync(directory, TestContext.Current.CancellationToken))!.SourceSnapshotFingerprint);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Flush_cancellation_is_distinguished_from_writing_and_does_not_commit()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-save-flush-").FullName;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ResumeLogFile.SaveAsync(
                directory, ProjectImportLog.FileName, null,
                async (stream, token) =>
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes("{}"), token);
                    cancellation.Cancel();
                },
                cancellation.Token));
            Assert.Equal("flushing-temporary-file", ResumeLogSaveFailure.Get(exception)!.Stage);
            Assert.False(File.Exists(Path.Combine(directory, ProjectImportLog.FileName)));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
