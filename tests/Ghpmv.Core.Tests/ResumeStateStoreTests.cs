using System.Diagnostics;
using System.Text.Json;
using Ghpmv.Cli;
using Ghpmv.Core.Import;
using Microsoft.Data.Sqlite;

namespace Ghpmv.Core.Tests;

public sealed class ResumeStateStoreTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("ghpmv-sqlite-").FullName;
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static ImportLog Log => new() { ProjectId = "PVT_target", SourceSnapshotFingerprint = "original" };

    [Fact]
    public async Task Migrates_both_legacy_logs_atomically_and_ignores_later_json_edits()
    {
        await File.WriteAllTextAsync(Path.Combine(directory, ImportLog.FileName),
            JsonSerializer.Serialize(Log, ImportLogJsonContext.Default.ImportLog), Token);
        await File.WriteAllTextAsync(Path.Combine(directory, ProjectImportLog.FileName),
            """{"pendingFields":null}""", Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => ImportLog.LoadAsync(directory, Token));
        Assert.False(File.Exists(Path.Combine(directory, ResumeStateStore.FileName)));
        await File.WriteAllTextAsync(Path.Combine(directory, ProjectImportLog.FileName),
            """{"createdProjectId":"PVT_created","pendingViews":{}}""", Token);

        Assert.Equal("original", (await ImportLog.LoadAsync(directory, Token))!.SourceSnapshotFingerprint);
        Assert.Equal("PVT_created", (await ProjectImportLog.LoadAsync(directory, Token)).CreatedProjectId);
        Assert.Equal(2, ResumeStateStore.ReadSnapshots(directory, Token).Count);
        await File.WriteAllTextAsync(Path.Combine(directory, ImportLog.FileName), "corrupt stale export", Token);
        await File.WriteAllTextAsync(Path.Combine(directory, ProjectImportLog.FileName), "corrupt stale export", Token);
        Assert.Equal("original", (await ImportLog.LoadAsync(directory, Token))!.SourceSnapshotFingerprint);
        Assert.Equal("PVT_created", (await ProjectImportLog.LoadAsync(directory, Token)).CreatedProjectId);
    }

    [Fact]
    public async Task Missing_row_is_not_reimported_from_a_late_json_file()
    {
        await new ProjectImportLog().SaveAsync(directory, Token);
        await File.WriteAllTextAsync(Path.Combine(directory, ImportLog.FileName),
            JsonSerializer.Serialize(Log, ImportLogJsonContext.Default.ImportLog), Token);
        Assert.Null(await ImportLog.LoadAsync(directory, Token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a database")]
    public async Task Invalid_database_never_falls_back_to_valid_json(string database)
    {
        await File.WriteAllTextAsync(Path.Combine(directory, ImportLog.FileName),
            JsonSerializer.Serialize(Log, ImportLogJsonContext.Default.ImportLog), Token);
        await File.WriteAllTextAsync(Path.Combine(directory, ResumeStateStore.FileName), database, Token);
        var failure = await Record.ExceptionAsync(() => ImportLog.LoadAsync(directory, Token));
        Assert.True(failure is IOException or InvalidDataException);
    }

    [Fact]
    public async Task Diagnostic_export_cannot_be_adopted_if_database_is_lost()
    {
        await Log.SaveAsync(directory, Token);
        await HtmlReportFile.WriteResumeReportsAsync(directory, false, _ => { }, Token);
        File.Delete(Path.Combine(directory, ResumeStateStore.FileName));
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => ImportLog.LoadAsync(directory, Token));
        Assert.Contains("diagnostic export", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(directory, ResumeStateStore.FileName)));
    }

    [Fact]
    public async Task Finalizer_does_not_hide_primary_failure_or_use_stale_json_when_database_is_invalid()
    {
        await Log.SaveAsync(directory, Token);
        await HtmlReportFile.WriteResumeReportsAsync(directory, true, _ => { }, Token);
        var htmlPath = Path.Combine(directory, "import-log.html");
        var previous = await File.ReadAllTextAsync(htmlPath, Token);
        await File.WriteAllTextAsync(Path.Combine(directory, ResumeStateStore.FileName), "", Token);
        using var diagnostics = new ImportFailureDiagnostics("target", "organization", null, false) { WriteHtmlReports = true };
        var failure = new InvalidOperationException("primary failure");
        var messages = new List<string>();
        Assert.Same(failure, await new ImportFailureFinalizer(diagnostics, directory, messages.Add)
            .CompleteAsync(failure, null, null));
        Assert.Contains(messages, message => message.Contains("may be stale", StringComparison.Ordinal));
        Assert.Equal(previous, await File.ReadAllTextAsync(htmlPath, Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Html_output_failure_is_warning_only_and_json_has_current_revision(bool writeHtml)
    {
        await Log.SaveAsync(directory, Token);
        Directory.CreateDirectory(Path.Combine(directory, "import-log.html"));
        var messages = new List<string>();
        await HtmlReportFile.WriteResumeReportsAsync(directory, writeHtml, messages.Add, Token);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, ImportLog.FileName), Token));
        var provenance = json.RootElement.GetProperty("resumeState");
        Assert.Equal(1, provenance.GetProperty("revision").GetInt64());
        Assert.True(provenance.GetProperty("diagnosticOnly").GetBoolean());
        Assert.Equal(ResumeStateStore.FileName, provenance.GetProperty("source").GetString());
        Assert.NotEmpty(provenance.GetProperty("generatedAt").GetString()!);
        Assert.Equal(writeHtml, messages.Any(message => message.Contains("may be stale", StringComparison.Ordinal)));
        Assert.Equal("original", (await ImportLog.LoadAsync(directory, Token))!.SourceSnapshotFingerprint);
    }

    [Fact]
    public async Task Orphan_backup_is_not_treated_as_a_fresh_import()
    {
        await File.WriteAllTextAsync(Path.Combine(directory, ImportLog.BackupFileName), "old backup", Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => ImportLog.LoadAsync(directory, Token));
    }

    [Fact]
    public async Task Busy_writer_fails_with_diagnostics_and_preserves_committed_revision()
    {
        await Log.SaveAsync(directory, Token);
        using var connection = OpenDatabase();
        using (var transaction = connection.BeginTransaction())
        {
            var stopwatch = Stopwatch.StartNew();
            var exception = await Assert.ThrowsAsync<IOException>(() =>
                (Log with { SourceSnapshotFingerprint = "replacement" }).SaveAsync(directory, Token));
            Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
            var sqlite = Assert.IsType<SqliteException>(exception.InnerException);
            Assert.Equal(5, sqlite.SqliteErrorCode);
            var context = Assert.IsType<ResumeLogSaveFailure>(ResumeLogSaveFailure.Get(exception));
            Assert.Equal("committing-resume-database", context.Stage);
            Assert.Equal(Path.Combine(directory, ResumeStateStore.FileName), context.LogPath);
            Assert.Null(context.BackupPath);
            using var diagnostics = new ImportFailureDiagnostics("target", "organization", null, false);
            await diagnostics.SaveFailureAsync(directory, exception, Token);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(directory, ImportFailureDiagnostics.FileName), Token));
            Assert.Equal(context.Stage, report.RootElement.GetProperty("exceptions")[0]
                .GetProperty("resumeLogSaveFailure").GetProperty("stage").GetString());
        }
        Assert.Equal("original", (await ImportLog.LoadAsync(directory, Token))!.SourceSnapshotFingerprint);
        Assert.Equal(1, Assert.Single(ResumeStateStore.ReadSnapshots(directory, Token)).Revision);
        await (Log with { SourceSnapshotFingerprint = "replacement" }).SaveAsync(directory, Token);
        Assert.Equal(2, Assert.Single(ResumeStateStore.ReadSnapshots(directory, Token)).Revision);
    }

    [Fact]
    public async Task Rollback_does_not_expose_uncommitted_payload()
    {
        await Log.SaveAsync(directory, Token);
        using (var connection = OpenDatabase())
        using (var transaction = connection.BeginTransaction())
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE resume_logs SET payload = 'uncommitted invalid JSON', revision = 99;";
            command.ExecuteNonQuery();
            Assert.Equal("original", (await ImportLog.LoadAsync(directory, Token))!.SourceSnapshotFingerprint);
        }
        Assert.Equal(1, Assert.Single(ResumeStateStore.ReadSnapshots(directory, Token)).Revision);
    }

    [Fact]
    public async Task Import_load_recovers_hot_journal_without_accepting_uncommitted_changes()
    {
        var log = Log;
        log.PendingDrafts["0"] = new()
        {
            OperationId = "pending",
            AttemptedAt = DateTimeOffset.UtcNow,
            Title = "draft",
            Body = new string('x', 256 * 1024),
            AssigneeIds = [],
            ExistingItemIds = [],
        };
        await log.SaveAsync(directory, Token);
        var recovery = Path.Combine(directory, "crash-image");
        Directory.CreateDirectory(recovery);
        using (var connection = OpenDatabase())
        {
            using var settings = connection.CreateCommand();
            settings.CommandText = "PRAGMA cache_size=1; PRAGMA cache_spill=ON; PRAGMA synchronous=FULL;";
            settings.ExecuteNonQuery();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE resume_logs SET payload=replace(payload, 'original', 'uncommitted'), revision=99;";
            command.ExecuteNonQuery();
            // Capture the on-disk crash image after cache spill, before rollback/connection cleanup.
            // No SQLite calls run between these copies, so the pair represents one interrupted write.
            File.Copy(Path.Combine(directory, ResumeStateStore.FileName), Path.Combine(recovery, ResumeStateStore.FileName));
            File.Copy(Path.Combine(directory, ResumeStateStore.FileName + "-journal"),
                Path.Combine(recovery, ResumeStateStore.FileName + "-journal"));
        }
        var journal = await File.ReadAllBytesAsync(Path.Combine(recovery, ResumeStateStore.FileName + "-journal"), Token);
        Assert.Equal(new byte[] { 0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7 }, journal[..8]);
        Assert.Throws<IOException>(() => ResumeStateStore.ReadSnapshots(recovery, Token));
        var restored = await ImportLog.LoadAsync(recovery, Token);
        Assert.Equal("original", restored!.SourceSnapshotFingerprint);
        Assert.Equal(log.PendingDrafts["0"].Body, restored.PendingDrafts["0"].Body);
        Assert.Equal(1, Assert.Single(ResumeStateStore.ReadSnapshots(recovery, Token)).Revision);
    }

    [Fact]
    public async Task Cancellation_keeps_token_and_previous_state()
    {
        await Log.SaveAsync(directory, Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            (Log with { SourceSnapshotFingerprint = "replacement" }).SaveAsync(directory, cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal("original", (await ImportLog.LoadAsync(directory, Token))!.SourceSnapshotFingerprint);
        Assert.Equal(1, Assert.Single(ResumeStateStore.ReadSnapshots(directory, Token)).Revision);
    }

    [Fact]
    public async Task Secondary_persistence_failure_keeps_original_exception_first()
    {
        var primary = new InvalidOperationException("remote failure");
        var secondary = new IOException("disk failure");
        var exception = await Assert.ThrowsAsync<AggregateException>(() =>
            ResumeStateStore.PersistFailureAsync(primary, () => Task.FromException(secondary)));
        Assert.Equal([primary, secondary], exception.InnerExceptions);
    }

    [Fact]
    public void Operation_lease_excludes_a_second_writer_until_disposed()
    {
        using (ResumeStateStore.AcquireOperationLease(directory))
        {
            Assert.Throws<IOException>(() => ResumeStateStore.AcquireOperationLease(directory));
        }
        using var next = ResumeStateStore.AcquireOperationLease(directory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Json_and_backup_locks_do_not_block_commits_or_fresh_html(bool importFails)
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Windows file sharing restrictions.");
        await Log.SaveAsync(directory, Token);
        var messages = new List<string>();
        await HtmlReportFile.WriteResumeReportsAsync(directory, true, messages.Add, Token);
        var jsonPath = Path.Combine(directory, ImportLog.FileName);
        var oldJson = await File.ReadAllTextAsync(jsonPath, Token);
        await File.WriteAllTextAsync(Path.Combine(directory, ImportLog.BackupFileName), "old", Token);
        using var jsonLock = new FileStream(jsonPath, FileMode.Open, FileAccess.Read, FileShare.None);
        using var backupLock = new FileStream(Path.Combine(directory, ImportLog.BackupFileName),
            FileMode.Open, FileAccess.Read, FileShare.None);
        await (Log with { SourceSnapshotFingerprint = "replacement" }).SaveAsync(directory, Token);
        using var diagnostics = new ImportFailureDiagnostics("target", "organization", null, false) { WriteHtmlReports = true };
        var primary = importFails ? new InvalidOperationException("import failure") : null;
        Assert.Same(primary, await new ImportFailureFinalizer(diagnostics, directory, messages.Add)
            .CompleteAsync(primary, null, null));
        Assert.Contains(messages, message => message.Contains("may be stale", StringComparison.Ordinal));
        var html = await File.ReadAllTextAsync(Path.Combine(directory, "import-log.html"), Token);
        Assert.Contains("replacement", html, StringComparison.Ordinal);
        Assert.Contains("resume-state.db", html, StringComparison.Ordinal);
        Assert.DoesNotContain("replacement", oldJson, StringComparison.Ordinal);
        Assert.Equal("replacement", (await ImportLog.LoadAsync(directory, Token))!.SourceSnapshotFingerprint);
    }

    [Fact]
    public async Task Offline_report_reads_database_without_json_and_cannot_overwrite_it()
    {
        await Log.SaveAsync(directory, Token);
        await new ProjectImportLog { CreatedProjectId = "PVT_report" }.SaveAsync(directory, Token);
        var database = Path.Combine(directory, ResumeStateStore.FileName);
        var bytes = await File.ReadAllBytesAsync(database, Token);
        var output = await HtmlReportFile.WriteAsync(database, null, "ja", false, _ => { }, Token,
            ProjectImportLog.FileName);
        Assert.Contains("PVT_report", await File.ReadAllTextAsync(output, Token), StringComparison.Ordinal);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(database, Token));
        Assert.False(File.Exists(Path.Combine(directory, ProjectImportLog.FileName)));
        await Assert.ThrowsAsync<IOException>(() =>
            HtmlReportFile.WriteAsync(database, database, "en", true, _ => { }, Token));
    }

    [Theory]
    [InlineData("{ invalid JSON")]
    [InlineData("""{"schemaVersion":999,"projectId":"PVT_future"}""")]
    public async Task Diagnostic_reads_isolate_invalid_log_while_import_loads_fail_closed(string invalidJson)
    {
        await Log.SaveAsync(directory, Token);
        await new ProjectImportLog { CreatedProjectId = "PVT_healthy" }.SaveAsync(directory, Token);
        using (var connection = OpenDatabase())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE resume_logs SET payload=$payload WHERE name=$name;";
            command.Parameters.AddWithValue("$payload", invalidJson);
            command.Parameters.AddWithValue("$name", ImportLog.FileName);
            command.ExecuteNonQuery();
        }
        var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, ResumeStateStore.FileName), Token);
        var importFailure = await Record.ExceptionAsync(() => ProjectImportLog.LoadAsync(directory, Token));
        Assert.True(importFailure is JsonException or InvalidDataException);
        var output = await HtmlReportFile.WriteAsync(Path.Combine(directory, ResumeStateStore.FileName),
            null, "en", true, _ => { }, Token, ProjectImportLog.FileName);
        Assert.Contains("PVT_healthy", await File.ReadAllTextAsync(output, Token), StringComparison.Ordinal);
        using var diagnostics = new ImportFailureDiagnostics("target", "organization", null, false) { WriteHtmlReports = true };
        var primary = new InvalidOperationException("primary");
        var messages = new List<string>();
        Assert.Same(primary, await new ImportFailureFinalizer(diagnostics, directory, messages.Add)
            .CompleteAsync(primary, null, null));
        Assert.Contains(messages, message => message.Contains(ImportLog.FileName, StringComparison.Ordinal)
            && message.Contains("may be stale", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(directory, ProjectImportLog.FileName)));
        Assert.False(File.Exists(Path.Combine(directory, ImportLog.FileName)));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(directory, ResumeStateStore.FileName), Token));
    }

    [Fact]
    public async Task Duplicate_legacy_properties_cannot_mask_failure_during_projection()
    {
        var json = JsonSerializer.Serialize(Log, ImportLogJsonContext.Default.ImportLog);
        json = json.Insert(1, "\"projectId\":\"duplicate\",");
        await File.WriteAllTextAsync(Path.Combine(directory, ImportLog.FileName), json, Token);
        Assert.NotNull(await ImportLog.LoadAsync(directory, Token));
        await new ProjectImportLog { CreatedProjectId = "PVT_healthy" }.SaveAsync(directory, Token);
        using var diagnostics = new ImportFailureDiagnostics("target", "organization", null, false) { WriteHtmlReports = true };
        var primary = new InvalidOperationException("primary");
        var messages = new List<string>();
        Assert.Same(primary, await new ImportFailureFinalizer(diagnostics, directory, messages.Add)
            .CompleteAsync(primary, null, null));
        Assert.Contains(messages, message => message.Contains("cannot be projected", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(directory, "project-import-log.html")));
    }

    [Theory]
    [InlineData("resume-state.db")]
    [InlineData("resume-state.db-journal")]
    [InlineData("resume-state.db.0123456789abcdef.tmp")]
    [InlineData("RESUME-STATE.DB.0123456789abcdef.tmp-journal")]
    [InlineData("resume-state.lock")]
    public async Task Report_output_preserves_database_and_recovery_artifacts(string fileName)
    {
        await Log.SaveAsync(directory, Token);
        var input = Path.Combine(directory, "input.json");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(Log, ImportLogJsonContext.Default.ImportLog), Token);
        var output = Path.Combine(directory, fileName);
        if (fileName != ResumeStateStore.FileName)
        {
            await File.WriteAllTextAsync(output, "recovery evidence", Token);
        }
        var original = await File.ReadAllBytesAsync(output, Token);
        await Assert.ThrowsAsync<IOException>(() => HtmlReportFile.WriteAsync(input, output, "en", true, _ => { }, Token));
        Assert.Equal(original, await File.ReadAllBytesAsync(output, Token));
        Assert.Empty(Directory.GetFiles(directory, ".ghpmv-report-*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nested_duplicate_properties_warn_without_changing_import_result_or_checkpoint(bool importFails)
    {
        const string legacy = """
            {"createdFields":{"Text":"PVTF_first","Text":"PVTF_last"}}
            """;
        await File.WriteAllTextAsync(Path.Combine(directory, ProjectImportLog.FileName), legacy, Token);
        Assert.Equal("PVTF_last", (await ProjectImportLog.LoadAsync(directory, Token)).CreatedFields["Text"]);
        await Log.SaveAsync(directory, Token);
        var snapshot = ResumeStateStore.ReadSnapshot(directory, ProjectImportLog.FileName, Token)!;
        var json = snapshot.ToDiagnosticJson();
        Assert.Contains("\"createdFields\"", json, StringComparison.Ordinal);
        var renderFailure = Assert.Throws<InvalidDataException>(() =>
            HtmlReportGenerator.Generate(json, ResumeStateStore.FileName));
        Assert.IsType<ArgumentException>(renderFailure.InnerException);
        var previousHtml = Path.Combine(directory, "project-import-log.html");
        await File.WriteAllTextAsync(previousHtml, "previous report", Token);
        var database = Path.Combine(directory, ResumeStateStore.FileName);
        var bytes = await File.ReadAllBytesAsync(database, Token);
        using var diagnostics = new ImportFailureDiagnostics("target", "organization", null, false) { WriteHtmlReports = true };
        var primary = importFails ? new InvalidOperationException("primary") : null;
        var messages = new List<string>();

        Assert.Same(primary, await new ImportFailureFinalizer(diagnostics, directory, messages.Add)
            .CompleteAsync(primary, null, null));

        Assert.Contains(messages, message => message.StartsWith(
            $"warning: failed to refresh HTML for {ProjectImportLog.FileName}", StringComparison.Ordinal));
        Assert.Equal("previous report", await File.ReadAllTextAsync(previousHtml, Token));
        Assert.True(File.Exists(Path.Combine(directory, "import-log.html")));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(database, Token));
    }

    private SqliteConnection OpenDatabase()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, ResumeStateStore.FileName),
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
