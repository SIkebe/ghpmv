using System.Diagnostics;
using System.Text.Json;
using Ghpmv.Cli;
using Ghpmv.Core.Import;
using Ghpmv.Core.Verify;

namespace Ghpmv.Core.Tests;

public sealed class HtmlReportTests
{
    private const string VerifyJson = """
        {"differences":[{"severity":"Error","category":"Item","message":"<script>alert(1)</script> missing"},
        {"severity":"Info","category":"Project","message":"title changed"}],
        "categories":[{"category":"Item","status":"Mismatch"},{"category":"Project","status":"Match"}],
        "status":"Mismatch"}
        """;

    [Fact]
    public void Verify_report_is_summarized_and_values_are_escaped()
    {
        var html = HtmlReportGenerator.Generate(VerifyJson, "verify.json");

        Assert.Contains("Verification report", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; missing", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert(1)", html, StringComparison.Ordinal);
        Assert.Contains("data-status=\"error\"", html, StringComparison.Ordinal);
        Assert.Contains("Migration differences need attention", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_error_report_lists_exceptions_progress_and_stack_traces()
    {
        const string json = """
            {"occurredAtUtc":"2026-01-01T00:00:00Z","command":"import","stage":"importing-items","progress":[{"timestampUtc":"2026-01-01T00:00:00Z","message":"started"}],
            "cleanupFailures":[],"exceptions":[{"depth":0,"type":"X<Y>","message":"boom","stackTrace":"at <a>"}]}
            """;

        var html = HtmlReportGenerator.Generate(json, "import-error.json");

        Assert.Contains("Import failure report", html, StringComparison.Ordinal);
        Assert.Contains("X&lt;Y&gt;", html, StringComparison.Ordinal);
        Assert.Contains("at &lt;a&gt;", html, StringComparison.Ordinal);
        Assert.Contains("Import stopped while importing items (issues, pull requests and draft issues)", html, StringComparison.Ordinal);
        Assert.Contains("started", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Failure_stage_is_explained_in_plain_language_with_step_positions()
    {
        const string json = """
            {"occurredAtUtc":"2026-01-01T00:00:00Z","stage":"importing-items","exceptions":[{"depth":0,"type":"IOException","message":"boom"}]}
            """;

        var html = HtmlReportGenerator.Generate(json, "import-error.json");
        var english = html[..html.IndexOf("<section id=\"source\">", StringComparison.Ordinal)];
        var summary = english[..english.IndexOf("<dl class=\"metrics\">", StringComparison.Ordinal)];

        Assert.Contains("What this stage does: The project and its fields already exist.", summary, StringComparison.Ordinal);
        Assert.Contains("Recorded error: boom", summary, StringComparison.Ordinal);
        Assert.Contains("Importing items (issues, pull requests and draft issues) (importing-items)", english, StringComparison.Ordinal);
        Assert.Contains("Where the import stopped", english, StringComparison.Ordinal);
        Assert.Contains("3. Checking permissions and prerequisites</div></td><td><div class=\"cell\">Finished or not needed", english, StringComparison.Ordinal);
        Assert.Contains("5. Importing items (issues, pull requests and draft issues)</div></td><td><div class=\"cell\">Stopped here", english, StringComparison.Ordinal);
        Assert.Contains("6. Importing status updates</div></td><td><div class=\"cell\">Not started", english, StringComparison.Ordinal);

        var japanese = HtmlReportGenerator.Generate(json, "import-error.json", "ja");
        Assert.Contains("アイテム（Issue・プルリクエスト・ドラフト Issue）の移行中にインポートが停止しました", japanese, StringComparison.Ordinal);
        Assert.Contains("ここで停止", japanese, StringComparison.Ordinal);
    }

    [Fact]
    public void Unrecognized_failure_stage_is_shown_verbatim_without_guessing_progress()
    {
        const string json = """
            {"occurredAtUtc":"2026-01-01T00:00:00Z","stage":"future-stage","exceptions":[]}
            """;

        var html = HtmlReportGenerator.Generate(json, "import-error.json");

        Assert.Contains("Import stopped while at stage &quot;future-stage&quot;", html, StringComparison.Ordinal);
        Assert.Contains("does not recognize the stage name &quot;future-stage&quot;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Stopped here", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Finished or not needed", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public void Report_contains_both_languages_with_a_toggle_and_starts_in_the_selected_language(string language)
    {
        var html = HtmlReportGenerator.Generate(VerifyJson, "verify.json", language);
        var other = language == "en" ? "ja" : "en";

        Assert.Contains($"<html lang=\"{language}\">", html, StringComparison.Ordinal);
        Assert.True(html.IndexOf($"<div class=\"lang-root\" data-l=\"{language}\"", StringComparison.Ordinal)
            < html.IndexOf($"<div class=\"lang-root\" data-l=\"{other}\"", StringComparison.Ordinal));
        Assert.Contains("data-set-lang=\"en\"", html, StringComparison.Ordinal);
        Assert.Contains("data-set-lang=\"ja\"", html, StringComparison.Ordinal);
        Assert.Contains("Migration differences need attention", html, StringComparison.Ordinal);
        Assert.Contains("対応が必要な移行差異があります", html, StringComparison.Ordinal);
        Assert.Contains("id=\"q\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"ja-q\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"ja-differences\"", html, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<section id=\"source\">"));
        Assert.Contains("html[lang=en] [data-l=ja]", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_log_flags_incomplete_items_and_pending_operations()
    {
        const string json = """
            {"schemaVersion":2,"projectId":"PVT_1","items":{},"itemStates":{
              "a":{"targetItemId":"1","fieldValuesApplied":true,"positionApplied":true,"archiveApplied":true},
              "b":{"targetItemId":"2","fieldValuesApplied":true,"positionApplied":false,"archiveApplied":false,"positionError":"nope"}},
            "pendingDrafts":{"c":{"operationId":"op1"}},"pendingContents":{},"pendingStatusUpdates":{},"statusUpdates":{}}
            """;

        var html = HtmlReportGenerator.Generate(json, "import-log.json");

        Assert.Contains("Item import resume log", html, StringComparison.Ordinal);
        Assert.Contains("nope", html, StringComparison.Ordinal);
        Assert.Contains("Pending operations (unreconciled)", html, StringComparison.Ordinal);
        Assert.Contains("op1", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_import_log_is_recognized()
    {
        const string json = """{"createdProjectId":"PVT_1","importCompleted":false,"pendingFields":{"Status":{}},"createdFields":{"A":"F1"},"pendingViews":{}}""";

        var html = HtmlReportGenerator.Generate(json, "project-import-log.json");

        Assert.Contains("Project import resume log", html, StringComparison.Ordinal);
        Assert.Contains("Status", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"foo":1}""")]
    [InlineData("""{"categories":null,"differences":[]}""")]
    [InlineData("""{"categories":[],"differences":[null]}""")]
    [InlineData("""{"projectId":"P","itemStates":[]}""")]
    [InlineData("""{"pendingFields":{"a":"not an object"}}""")]
    [InlineData("""{"pendingViews":{},"importCompleted":"true"}""")]
    [InlineData("""{"projectId":"P","itemStates":{"a":{"fieldValuesApplied":1}}}""")]
    public void Invalid_or_unknown_input_is_rejected(string json)
    {
        Assert.Throws<InvalidDataException>(() => HtmlReportGenerator.Generate(json, "x.json"));
    }

    [Fact]
    public void Warnings_and_verification_gaps_are_not_rendered_as_success()
    {
        const string json = """
            {"categories":[{"category":"View","status":"PartialMatch"}],
            "differences":[{"severity":"Warning","category":"View","message":"Needs browser verification"}]}
            """;
        var html = HtmlReportGenerator.Generate(json, "verify.json");
        var analysis = html[..html.IndexOf("<section id=\"source\">", StringComparison.Ordinal)];

        Assert.Contains("Verification has warnings or coverage gaps", analysis, StringComparison.Ordinal);
        Assert.Contains("Partial match", analysis, StringComparison.Ordinal);
        Assert.DoesNotContain("data-status=\"success\"", analysis, StringComparison.Ordinal);
    }

    [Fact]
    public void Not_verified_is_a_failure_even_without_error_differences()
    {
        var report = new VerifyReport
        {
            Categories = [new VerifyCategoryResult { Category = "Workflow", Status = VerifyStatus.NotVerified }],
            Differences = [],
        };
        Assert.True(report.ShouldFail(failOnWarning: false));
        var html = HtmlReportGenerator.Generate(
            JsonSerializer.Serialize(report, VerifyReportJsonContext.Default.VerifyReport), "verify.json");
        Assert.Contains("Verification is incomplete and fails the CLI check", html, StringComparison.Ordinal);
        Assert.Contains("nonzero exit even without --fail-on-warning", html, StringComparison.Ordinal);
        Assert.Contains("data-status=\"error\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Aggregate_failure_summary_exposes_concrete_evidence_and_cleanup_without_claiming_root_cause()
    {
        const string json = """
            {"occurredAtUtc":"2026-01-01T00:00:00Z","stage":"items",
            "exceptions":[
              {"depth":0,"type":"System.AggregateException","message":"Multiple related failures occurred."},
              {"depth":1,"type":"System.AggregateException","message":"Multiple related failures occurred."},
              {"depth":2,"type":"GitHubGraphQLException","message":"Primary recorded API failure"},
              {"depth":1,"type":"IOException","message":"Cleanup could not save state"}],
            "cleanupFailures":[{"stage":"template-restoration","message":"Template restoration failed"}]}
            """;
        var html = HtmlReportGenerator.Generate(json, "import-error.json");
        var summary = html[..html.IndexOf("<dl class=\"metrics\">", StringComparison.Ordinal)];
        Assert.Contains("First concrete failure in recorded order: Primary recorded API failure", summary, StringComparison.Ordinal);
        Assert.Contains("template-restoration: Template restoration failed", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Multiple related failures occurred.", summary, StringComparison.Ordinal);
        Assert.Contains("Do not assume the first exception is the root cause", summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Persistence_failures_in_primary_or_cleanup_exceptions_get_prominent_retry_warning(bool cleanupOnly)
    {
        const string failure = """
            {"depth":1,"type":"IOException","message":"Write failed","resumeLogSaveFailure":{
              "stage":"commit","logPath":"demo/import-log.json","temporaryPath":"demo/temp",
              "backupPath":"demo/backup","executingProcessId":123}}
            """;
        var json = cleanupOnly
            ? $$"""{"occurredAtUtc":"2026-01-01T00:00:00Z","stage":"items","exceptions":[],"cleanupFailures":[{"stage":"restore","exceptions":[{{failure}}]}]}"""
            : $$"""{"occurredAtUtc":"2026-01-01T00:00:00Z","stage":"items","exceptions":[{{failure}}]}""";
        var html = HtmlReportGenerator.Generate(json, "import-error.json");
        var summary = html[..html.IndexOf("<dl class=\"metrics\">", StringComparison.Ordinal)];
        Assert.Contains("resume-log persistence failed", summary, StringComparison.Ordinal);
        Assert.Contains("resume log may be stale or incomplete", summary, StringComparison.Ordinal);
        Assert.Contains("before any retry to avoid duplicate writes", summary, StringComparison.Ordinal);
        Assert.Contains("Resume-log persistence failures", html, StringComparison.Ordinal);
        Assert.Contains("demo/import-log.json", html, StringComparison.Ordinal);
        Assert.Contains("not which process held a lock", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Field_failures_surface_names_and_operations_without_inventing_reasons()
    {
        var log = new ImportLog
        {
            ProjectId = "P",
            ItemStates = new()
            {
                ["0"] = new ImportItemState
                {
                    TargetItemId = "I", TargetContentIdentity = "demo/repo#42",
                    FieldValueFailures =
                    [
                        new MigrationDiagnosticContext
                        {
                            Element = new MigrationElementIdentity { Kind = "Field", Name = "Priority" },
                            Operation = "apply-field-value",
                        },
                    ],
                },
            },
        };
        var html = HtmlReportGenerator.Generate(
            JsonSerializer.Serialize(log, ImportLogJsonContext.Default.ImportLog), "import-log.json");
        var normal = html[..html.IndexOf("<section id=\"source\">", StringComparison.Ordinal)];
        Assert.Contains("Name: Priority", normal, StringComparison.Ordinal);
        Assert.Contains("Operation: apply-field-value", normal, StringComparison.Ordinal);
        Assert.Contains("Field-value failure contexts: 1", normal, StringComparison.Ordinal);
        Assert.Contains("<dt>Items with errors</dt><dd>1</dd>", normal, StringComparison.Ordinal);
        Assert.Contains("specific reason was not recorded here", normal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(-1)]
    public void Unsupported_item_schema_versions_are_rejected_without_analyzing_future_shapes(int version)
    {
        var json = $$$"""{"schemaVersion":{{{version}}},"projectId":"P","itemStates":{}}""";
        var exception = Assert.Throws<InvalidDataException>(() => HtmlReportGenerator.Generate(json, "import-log.json"));
        Assert.Contains("Unsupported item import-log schemaVersion", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"categories":[],"differences":[]}""")]
    [InlineData("""{"categories":[{"category":"View","status":"FutureStatus"}],"differences":[]}""")]
    [InlineData("""{"categories":[{"category":"View","status":"Match"}],"differences":[{"severity":"FutureSeverity"}]}""")]
    public void Missing_or_unknown_verification_evidence_cannot_establish_a_match(string json)
    {
        var html = HtmlReportGenerator.Generate(json, "verify.json");
        Assert.Contains("There is not enough evidence to confirm a match", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"pendingFields":{},"createdFields":{}}""")]
    [InlineData("""{"pendingViews":{},"importCompleted":null,"hasUnresolvedWarnings":null}""")]
    public void Legacy_project_completion_and_warning_states_remain_unknown(string json)
    {
        var html = HtmlReportGenerator.Generate(json, "project-import-log.json");
        Assert.Contains("Completion: Unknown. Unresolved warnings: Unknown.", html, StringComparison.Ordinal);
        Assert.Contains("Project completion cannot be confirmed", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Pending_operations_use_names_and_include_project_deletion_and_evidence()
    {
        const string json = """
            {"pendingProject":{"title":"Migration copy","ownerLogin":"demo","operationId":"create-op"},
            "pendingProjectDeletionId":"PVT_DELETE",
            "pendingFields":{},"pendingViews":{"0":{"name":"Sprint board","layout":"BOARD",
            "operationId":"view-op","existingViewIds":["BASELINE"],"reconciledViewId":"RESERVED"}}}
            """;
        var html = HtmlReportGenerator.Generate(json, "project-import-log.json");
        Assert.Contains("Sprint board", html, StringComparison.Ordinal);
        Assert.Contains("Migration copy", html, StringComparison.Ordinal);
        Assert.Contains("Project deletion", html, StringComparison.Ordinal);
        Assert.Contains("Reserved target: RESERVED", html, StringComparison.Ordinal);
        Assert.Contains("BASELINE", html, StringComparison.Ordinal);
        Assert.Contains("do not clear the log", html, StringComparison.Ordinal);
        Assert.Contains("<dt>Unconfirmed operations</dt><dd>3</dd>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Stage_analysis_uses_independent_counts_and_item_identity()
    {
        const string json = """
            {"projectId":"P","itemStates":{
            "0":{"targetContentIdentity":"demo/repo#42","fieldValuesApplied":true,"positionApplied":false,"archiveApplied":false},
            "1":{"fieldValuesApplied":true,"positionApplied":true,"archiveApplied":true},
            "2":{"fieldValuesApplied":null}},"templateRestorationRequired":true}
            """;
        var html = HtmlReportGenerator.Generate(json, "import-log.json");
        Assert.Contains("demo/repo#42", html, StringComparison.Ordinal);
        Assert.Contains("Remaining work by stage", html, StringComparison.Ordinal);
        Assert.Contains("Template restoration remains required", html, StringComparison.Ordinal);
        Assert.Contains("<dt>Fully applied</dt><dd>1</dd>", html, StringComparison.Ordinal);
        Assert.Contains("<dt>Incomplete</dt><dd>2</dd>", html, StringComparison.Ordinal);
        Assert.Contains("data-status=\"unknown\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_item_mappings_are_visible_without_claiming_completion()
    {
        var html = HtmlReportGenerator.Generate("""{"projectId":"P","items":{"0":"TARGET"}}""", "import-log.json");
        Assert.Contains("No detailed item progress was recorded", html, StringComparison.Ordinal);
        Assert.Contains("TARGET", html, StringComparison.Ordinal);
        Assert.Contains("not proof that all import stages completed", html, StringComparison.Ordinal);
        Assert.Contains("legacy or unversioned checkpoint", html, StringComparison.Ordinal);
    }

    [Fact]
    public void All_detail_sections_are_searchable_and_source_data_cannot_escape_markup()
    {
        var html = HtmlReportGenerator.Generate(
            """{"occurredAtUtc":"2026-01-01T00:00:00Z","exceptions":[{"type":"Error","stackTrace":"</pre></details><script>probe()</script>"}],"context":{"element":"<img src=x onerror=probe()>"}}""",
            "<script>filename()</script>.json");
        Assert.DoesNotContain("<script>probe()", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>filename()", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img src=x", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;filename()", html, StringComparison.Ordinal);
        Assert.Contains("aria-live=\"polite\"", html, StringComparison.Ordinal);
        Assert.Contains("Reset filters", html, StringComparison.Ordinal);
        Assert.Contains("Filters apply to every detail table", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Japanese_report_localizes_analysis_but_preserves_recorded_values_and_filter_contract()
    {
        var html = HtmlReportGenerator.Generate(VerifyJson, "verify.json", "ja");
        var analysis = html[..html.IndexOf("<section id=\"source\">", StringComparison.Ordinal)];

        Assert.Contains("<html lang=\"ja\">", html, StringComparison.Ordinal);
        Assert.Contains("検証レポート", analysis, StringComparison.Ordinal);
        Assert.Contains("対応が必要な移行差異があります", analysis, StringComparison.Ordinal);
        Assert.Contains("<dt>エラー</dt><dd>1</dd>", analysis, StringComparison.Ordinal);
        Assert.Contains("フィルターをリセット", analysis, StringComparison.Ordinal);
        Assert.Contains("data-section-count=\"{1} 件中 {0} 件を表示\"", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; missing", analysis, StringComparison.Ordinal);
        Assert.Contains("data-status=\"error\"", analysis, StringComparison.Ordinal);
        Assert.DoesNotContain("Migration differences need attention", analysis, StringComparison.Ordinal);
        Assert.DoesNotContain("Reset filters", analysis, StringComparison.Ordinal);
        Assert.Contains("Migration differences need attention", HtmlReportGenerator.Generate(VerifyJson, "verify.json"), StringComparison.Ordinal);
    }

    [Fact]
    public void Unsupported_report_language_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => HtmlReportGenerator.Generate(VerifyJson, "verify.json", "fr"));
    }

    [Fact]
    public void Production_serialized_logs_render_with_the_same_contract()
    {
        var report = new VerifyReport
        {
            Categories = [new VerifyCategoryResult { Category = "Item", Status = VerifyStatus.Match }],
            Differences = [],
        };
        var verify = JsonSerializer.Serialize(report, VerifyReportJsonContext.Default.VerifyReport);
        Assert.Contains("The reported categories match", HtmlReportGenerator.Generate(verify, "verify.json"), StringComparison.Ordinal);
        var items = new ImportLog
        {
            ProjectId = "P",
            ItemStates = new() { ["0"] = new ImportItemState
                { TargetItemId = "I", FieldValuesApplied = true, PositionApplied = true, ArchiveApplied = true } },
        };
        Assert.Contains("Recorded item stages are complete",
            HtmlReportGenerator.Generate(JsonSerializer.Serialize(items, ImportLogJsonContext.Default.ImportLog), "import-log.json"), StringComparison.Ordinal);
        var project = new ProjectImportLog { CreatedProjectId = "P", ImportCompleted = true, HasUnresolvedWarnings = false };
        Assert.Contains("The log records project import completion",
            HtmlReportGenerator.Generate(JsonSerializer.Serialize(project, ProjectImportLogJsonContext.Default.ProjectImportLog), "project-import-log.json"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Report_command_writes_html_and_rejects_same_path_output()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, "verify.json");
            await File.WriteAllTextAsync(input, VerifyJson, TestContext.Current.CancellationToken);

            var ok = await RunCliAsync(directory, ["report", "--input", input]);
            Assert.Equal(0, ok.ExitCode);
            Assert.True(File.Exists(Path.Combine(directory, "verify.html")));
            Assert.Contains("report=", ok.Output, StringComparison.Ordinal);

            var same = await RunCliAsync(directory, ["report", "--input", input, "--output", input]);
            Assert.Equal(1, same.ExitCode);
            Assert.Contains("error:", same.Error, StringComparison.Ordinal);
            Assert.Contains("\"differences\"", await File.ReadAllTextAsync(input, TestContext.Current.CancellationToken), StringComparison.Ordinal);

            var custom = Path.Combine(directory, "nested", "review.html");
            var customResult = await RunCliAsync(directory, ["report", "--input", input, "--output", custom]);
            Assert.Equal(0, customResult.ExitCode);
            Assert.True(File.Exists(custom));

            await File.WriteAllTextAsync(custom, "Existing report to preserve", TestContext.Current.CancellationToken);
            var refused = await RunCliAsync(directory, ["report", "--input", input, "--output", custom]);
            Assert.Equal(1, refused.ExitCode);
            Assert.Contains("--overwrite", refused.Error, StringComparison.Ordinal);
            Assert.Equal("Existing report to preserve", await File.ReadAllTextAsync(custom, TestContext.Current.CancellationToken));

            var overwrite = await RunCliAsync(directory, ["report", "--input", input, "--output", custom, "--overwrite"]);
            Assert.Equal(0, overwrite.ExitCode);
            Assert.Contains("Verification report", await File.ReadAllTextAsync(custom, TestContext.Current.CancellationToken), StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(custom)!, ".ghpmv-report-*.tmp"));

            var japanese = await RunCliAsync(directory, ["report", "--input", input, "--output", custom, "--overwrite", "--language", "ja"]);
            Assert.Equal(0, japanese.ExitCode);
            Assert.Contains("検証レポート", await File.ReadAllTextAsync(custom, TestContext.Current.CancellationToken), StringComparison.Ordinal);
            var unsupported = await RunCliAsync(directory, ["report", "--input", input, "--output", custom, "--overwrite", "--language", "fr"]);
            Assert.NotEqual(0, unsupported.ExitCode);
            Assert.Contains("検証レポート", await File.ReadAllTextAsync(custom, TestContext.Current.CancellationToken), StringComparison.Ordinal);

            var sameOverwrite = await RunCliAsync(directory, ["report", "--input", input, "--output", input, "--overwrite"]);
            Assert.Equal(1, sameOverwrite.ExitCode);
            Assert.Equal(VerifyJson, await File.ReadAllTextAsync(input, TestContext.Current.CancellationToken));

            await File.WriteAllTextAsync(input, """{"categories":"wrong","differences":[]}""", TestContext.Current.CancellationToken);
            var previous = await File.ReadAllTextAsync(custom, TestContext.Current.CancellationToken);
            var invalid = await RunCliAsync(directory, ["report", "--input", input, "--output", custom]);
            Assert.Equal(1, invalid.ExitCode);
            Assert.Contains("error:", invalid.Error, StringComparison.Ordinal);
            Assert.Equal(previous, await File.ReadAllTextAsync(custom, TestContext.Current.CancellationToken));
            var invalidOverwrite = await RunCliAsync(directory, ["report", "--input", input, "--output", custom, "--overwrite"]);
            Assert.Equal(1, invalidOverwrite.ExitCode);
            Assert.Equal(previous, await File.ReadAllTextAsync(custom, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Failed_import_writes_html_beside_failure_and_resume_logs_only_when_enabled()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "auto-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var items = new ImportLog
            {
                ProjectId = "P",
                ItemStates = new() { ["0"] = new ImportItemState { TargetItemId = "I" } },
            };
            await File.WriteAllTextAsync(Path.Combine(directory, ImportLog.FileName),
                JsonSerializer.Serialize(items, ImportLogJsonContext.Default.ImportLog), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory, ProjectImportLog.FileName), "{\"createdProjectId\":[]}",
                TestContext.Current.CancellationToken);

            using (var disabled = new ImportFailureDiagnostics("target-org", "organization", null, false))
            {
                var lines = new List<string>();
                var failure = new InvalidOperationException("disabled");
                Assert.Same(failure, await new ImportFailureFinalizer(disabled, directory, lines.Add).CompleteAsync(failure, null, null));
                Assert.Empty(Directory.GetFiles(directory, "*.html"));
                Assert.DoesNotContain(lines, line => line.StartsWith("HTML report:", StringComparison.Ordinal));
            }

            using (var enabled = new ImportFailureDiagnostics("target-org", "organization", null, false) { WriteHtmlReports = true })
            {
                var lines = new List<string>();
                var failure = new InvalidOperationException("AUTO-HTML-FAILURE");
                Assert.Same(failure, await new ImportFailureFinalizer(enabled, directory, lines.Add).CompleteAsync(failure, null, null));
                Assert.Contains("AUTO-HTML-FAILURE", await File.ReadAllTextAsync(Path.Combine(directory, "import-error.html"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
                Assert.True(File.Exists(Path.Combine(directory, "import-log.html")));
                Assert.False(File.Exists(Path.Combine(directory, "project-import-log.html")));
                Assert.Contains(lines, line => line.StartsWith("warning: failed to write HTML report for project-import-log.json", StringComparison.Ordinal));
                Assert.Equal(2, lines.Count(line => line.StartsWith("HTML report:", StringComparison.Ordinal)));
                Assert.Empty(Directory.GetFiles(directory, ".ghpmv-report-*.tmp"));

                enabled.DeletePreviousFailure(directory);
                Assert.False(File.Exists(Path.Combine(directory, ImportFailureDiagnostics.FileName)));
                Assert.False(File.Exists(Path.Combine(directory, "import-error.html")));
                Assert.True(File.Exists(Path.Combine(directory, "import-log.html")));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_command_writes_failure_html_unless_disabled()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "auto-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string[] arguments = ["import", "--org", "target-org", "--in", directory, "--no-update-check"];
            var disabled = await RunCliAsync(directory, [.. arguments, "--no-html-report"], clearTokens: true);
            Assert.Equal(1, disabled.ExitCode);
            Assert.True(File.Exists(Path.Combine(directory, ImportFailureDiagnostics.FileName)));
            Assert.False(File.Exists(Path.Combine(directory, "import-error.html")));
            Assert.Empty(disabled.Output);

            var enabled = await RunCliAsync(directory, arguments, clearTokens: true);
            Assert.Equal(1, enabled.ExitCode);
            Assert.Contains("no token provided", await File.ReadAllTextAsync(Path.Combine(directory, "import-error.html"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
            Assert.Contains("HTML report:", enabled.Error, StringComparison.Ordinal);
            Assert.Empty(enabled.Output);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunCliAsync(
        string directory,
        IEnumerable<string> arguments,
        bool clearTokens = false)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (clearTokens)
        {
            startInfo.Environment.Remove("GITHUB_TOKEN");
            startInfo.Environment.Remove("GHPMV_TOKEN");
        }

        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ghpmv.dll"));
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start CLI.");
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await output, await error);
    }
}
