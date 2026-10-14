using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ghpmv.Core.Import;

namespace Ghpmv.Cli;

internal static class HtmlReportGenerator
{
    public static readonly string[] SupportedLanguages = ["en", "ja"];
    private static readonly string[] ItemStages = ["fieldValues", "position", "archive"];
    // JSON is displayed only inside HTML-encoded text, never inside script.
    private static readonly JsonSerializerOptions DisplayJsonOptions = new()
        { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // Generation is synchronous, so the selected language is scoped to the calling thread.
    [ThreadStatic] private static bool japanese;

    public static string Generate(string json, string sourceName, string language = "en")
    {
        ArgumentNullException.ThrowIfNull(json);
        if (!SupportedLanguages.Contains(language, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unsupported report language '{language}'. Use one of: {string.Join(", ", SupportedLanguages)}.", nameof(language));
        }
        JsonObject obj;
        try
        {
            obj = JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidDataException("The input JSON must be an object produced by ghpmv.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The input is not valid JSON: {exception.Message}", exception);
        }

        Action<Page, JsonObject> build =
            obj.ContainsKey("differences") && obj.ContainsKey("categories") ? BuildVerify
            : obj.ContainsKey("occurredAtUtc") && obj.ContainsKey("exceptions") ? BuildFailure
            : obj.ContainsKey("projectId") && (obj.ContainsKey("itemStates") || obj.ContainsKey("items")) ? BuildItems
            : obj.ContainsKey("pendingFields") || obj.ContainsKey("createdFields") || obj.ContainsKey("pendingViews") ? BuildProject
            : throw new InvalidDataException(
                "Unrecognized input. Supported files: verify --report-json output, import-error.json, import-log.json and project-import-log.json.");

        // Both languages are rendered into one file so the reader can switch instantly and offline.
        // The initial language is rendered first and carries the shared source JSON.
        var order = language == "ja" ? new[] { "ja", "en" } : ["en", "ja"];
        var previous = japanese;
        try
        {
            var html = new StringBuilder();
            var titles = new Dictionary<string, string>(StringComparer.Ordinal);
            var roots = new StringBuilder();
            foreach (var lang in order)
            {
                japanese = lang == "ja";
                var page = new Page(sourceName, obj);
                build(page, obj);
                titles[lang] = page.Title;
                page.RenderRoot(roots, lang, includeSource: lang == language);
            }
            japanese = language == "ja";
            html.Append("<!DOCTYPE html><html lang=\"").Append(language).Append("\"><head><meta charset=\"utf-8\">")
                .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'\">")
                .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>")
                .Append(Encode(titles[language])).Append(" - ghpmv</title><style>").Append(Style)
                .Append("</style></head><body>").Append(roots).Append("<script>").Append(Script).Append("</script></body></html>");
            return html.ToString();
        }
        finally
        {
            japanese = previous;
        }
    }

    private static void BuildVerify(Page page, JsonObject obj)
    {
        page.Title = T("Verification report");
        page.Purpose = T("Compare migration fidelity. Distinguish confirmed differences from verification gaps.");
        var differences = Array(obj, "differences", required: true);
        var categories = Array(obj, "categories", required: true);
        var errors = differences.Count(d => Text(d, "severity") == "Error");
        var warnings = differences.Count(d => Text(d, "severity") == "Warning");
        var gaps = categories.Count(c => Text(c, "status") is "NotVerified" or "PartialMatch");
        var notVerified = categories.Count(c => Text(c, "status") == "NotVerified");
        var mismatch = categories.Any(c => Text(c, "status") == "Mismatch");
        var unknown = categories.Count == 0 || categories.Any(c => !KnownVerifyStatus(Text(c, "status")))
            || differences.Any(d => Severity(Text(d, "severity")) == "unknown");
        var status = mismatch || errors > 0 || notVerified > 0 ? "error"
            : unknown ? "unknown"
            : gaps > 0 || warnings > 0 ? "warning" : "success";
        page.Outcome = status;
        page.Conclusion = T(status switch
        {
            "error" when notVerified > 0 && errors == 0 && !mismatch => "Verification is incomplete and fails the CLI check",
            "error" => "Migration differences need attention",
            "unknown" => "There is not enough evidence to confirm a match",
            "warning" => "Verification has warnings or coverage gaps",
            _ => "The reported categories match",
        });
        page.Explanation = Sentences(
            F("Errors: {0}. Warnings: {1}. Categories with partial or missing verification: {2}.", Count(errors), Count(warnings), Count(gaps)),
            T("This result applies only to the categories in this file; it does not establish that every migration stage succeeded."),
            errors > 0 || notVerified > 0
                ? F("The CLI verification check fails: errors or not-verified categories ({0}) cause a nonzero exit even without --fail-on-warning.", Count(notVerified))
                : warnings > 0 ? T("Warnings cause a nonzero CLI exit when --fail-on-warning is enabled.") : null);
        page.Actions.Add(T(errors > 0 || mismatch
            ? "Inspect error differences first and compare the affected source and target data before making changes."
            : "Review the category coverage before treating this verification as complete."));
        if (gaps > 0 || warnings > 0 || unknown)
        {
            page.Actions.Add(T("Review warnings and unverified categories separately. A missing check is not a successful check."));
        }

        var matched = categories.Count(c => Text(c, "status") is "Match" or "NotApplicable");
        page.Metric("Categories checked", categories.Count);
        page.Metric("Errors", errors, "error");
        page.Metric("Warnings", warnings, "warning");
        page.Metric("Information", differences.Count(d => Text(d, "severity") == "Info"), "info");
        page.Progress = (T("Categories matched or not applicable"), matched, categories.Count);
        page.Sections.Add(new Section("differences", T("Differences to review"),
            T("Errors first, then warnings and information. Messages are the verifier's recorded evidence, not inferred causes."),
            [T("Severity"), T("Category"), T("Recorded difference")],
            differences.OrderBy(d => Priority(Severity(Text(d, "severity"))))
                .Select(d => new Row(Severity(Text(d, "severity")),
                    [SeverityLabel(Text(d, "severity")), Text(d, "category") ?? T("Unknown"), Text(d, "message") ?? T("Not recorded")], d)).ToList(),
            T("No differences were recorded. Check category coverage below.")));
        page.Sections.Add(new Section("coverage", T("Category coverage"),
            T("Partial match and not verified are limitations, not passes. Not applicable is excluded from the match judgment."),
            [T("Category"), T("Outcome"), T("Errors"), T("Warnings"), T("Information")],
            categories.Select(c =>
            {
                var name = Text(c, "category");
                var related = differences.Where(d => Text(d, "category") == name).ToList();
                return new Row(VerifyLevel(Text(c, "status")), [name ?? T("Unknown"),
                    HumanStatus(Text(c, "status")),
                    Count(related.Count(d => Text(d, "severity") == "Error")),
                    Count(related.Count(d => Text(d, "severity") == "Warning")),
                    Count(related.Count(d => Text(d, "severity") == "Info"))], c);
            }).OrderBy(r => Priority(r.Level)).ToList(), T("No category coverage was recorded.")));
    }

    private static void BuildFailure(Page page, JsonObject obj)
    {
        page.Title = T("Import failure report");
        page.Purpose = T("Understand the failure, its recorded context and the checks needed before resuming.");
        var exceptions = Array(obj, "exceptions", required: true);
        var cleanup = Array(obj, "cleanupFailures");
        var progress = Array(obj, "progress");
        var rawStage = Text(obj, "stage");
        var stage = StageLabel(rawStage);
        page.Outcome = "error";
        page.Conclusion = F("Import stopped while {0}", StageVerb(rawStage));
        var concrete = exceptions.Where(e => Text(e, "type") is not "System.AggregateException" and not "AggregateException").ToList();
        var explanation = exceptions.Count > 0
            ? concrete.Count > 0 ? Text(concrete[0], "message") ?? T("The failure message was not recorded.")
                : T("Multiple related failures were recorded; no concrete nested failure message is available.")
            : T("No exception detail was recorded. The failure cause cannot be established from this file.");
        if (exceptions.Count > 0 && concrete.Count < exceptions.Count)
        {
            explanation = F("First concrete failure in recorded order: {0}", explanation);
        }
        page.Explanation = Sentences(StageMeaning(rawStage), exceptions.Count > 0 ? F("Recorded error: {0}", explanation) : explanation, cleanup.Count > 0
            ? F("Cleanup also failed ({0} records): {1}", Count(cleanup.Count),
                Join(cleanup.Select(c => $"{StageLabel(Text(c, "stage"))}: {Text(c, "message") ?? T("No message recorded")}").ToArray()))
            : null);
        var persistence = exceptions.Concat(cleanup.SelectMany(c => Array(c, "exceptions")))
            .Where(e => e["resumeLogSaveFailure"] is not null).ToList();
        if (persistence.Any(e => e["resumeLogSaveFailure"] is not JsonObject))
        {
            throw new InvalidDataException("'resumeLogSaveFailure' must be an object or null.");
        }
        if (persistence.Count > 0)
        {
            page.Conclusion = F("Import stopped while {0}; resume-log persistence failed", StageVerb(rawStage));
            page.Actions.Add(T("The resume log may be stale or incomplete: a target write can succeed without being saved locally. Inspect the target and reconcile actual state before any retry to avoid duplicate writes."));
            page.Sections.Add(new Section("persistence", T("Resume-log persistence failures"),
                T("These paths describe the failed save operation, not which process held a lock. Check storage access and saved target state before resuming."),
                [T("Log path"), T("Save stage"), T("Temporary / backup path"), T("Executing process")],
                persistence.Select(e =>
                {
                    var failure = (JsonObject)e["resumeLogSaveFailure"]!;
                    return new Row("error", [Value(failure, "logPath"), StageLabel(Text(failure, "stage")),
                        Join(Text(failure, "temporaryPath"), Text(failure, "backupPath")),
                        Value(failure, "executingProcessId")], failure);
                }).DistinctBy(r => string.Join("\n", r.Cells)).ToList(), T("No persistence failures recorded.")));
        }
        page.Actions.Add(T("Read the failure evidence and its recorded recovery hints. Do not assume the first exception is the root cause."));
        if (cleanup.Count > 0)
        {
            page.Actions.Add(T("Inspect cleanup failures before resuming; restoration or cleanup may not have completed."));
        }
        page.Actions.Add(T("Preserve the snapshot and resume logs. Inspect the target before repeating any operation with an uncertain result."));
        page.Metrics.Add(new Metric(T("Failed stage"), stage, "error"));
        page.Metric("Exception records", exceptions.Count, "error");
        page.Metric("Cleanup failures", cleanup.Count, "error");
        page.Metrics.Add(new Metric(T("Recorded time span"), TimeSpanLabel(progress), "neutral"));

        var failedStep = System.Array.FindIndex(ImportSteps, s => s.Code == rawStage);
        var cleanupStage = rawStage is not null && CleanupStages.Contains(rawStage);
        page.Sections.Add(new Section("steps", T("Where the import stopped"),
            T("ghpmv imports in this order. Steps before the failure finished in this run (or were not needed); later steps did not run. Some steps run only when the snapshot or options require them."),
            [T("Step"), T("Result in this run"), T("What this step does")],
            ImportSteps.Select((s, i) =>
            {
                var (level, result) = cleanupStage || failedStep >= 0 && i < failedStep
                    ? ("success", T("Finished or not needed"))
                    : i == failedStep ? ("error", T("Stopped here"))
                    : failedStep >= 0 ? ("info", T("Not started"))
                    : ("unknown", T("Unknown"));
                return new Row(level, [F("{0}. {1}", Count(i + 1), T(s.Label)), result, T(s.Meaning)]);
            }).ToList(), T("No import steps are defined.")));
        page.Sections.Add(new Section("failure", T("Failure evidence"),
            T("Outer and nested exceptions retain their depth. Expand evidence for context, attempt IDs, API details and stack traces."),
            [T("Exception / depth"), T("Recorded message"), T("Operation / response"), T("Recovery hint")],
            exceptions.Select(e => new Row("error", [
                F("{0} · depth {1}", Text(e, "type") ?? T("Unknown exception"), Value(e, "depth")),
                Text(e, "message") ?? T("Not recorded"),
                Join(Text(e, "operationName") ?? Text(e, "operationKind"),
                    Text(e, "statusCode") is { } code ? F("HTTP {0}", code) : null,
                    Text(e, "requestId") is { } id ? F("Request {0}", id) : null,
                    ContextText(e["context"])),
                Text(e, "recoveryHint") ?? T("No recovery hint recorded")], e)).ToList(),
            T("No exception details were recorded."), Entries: true));
        page.Sections.Add(new Section("patterns", T("Recorded failure patterns"),
            T("Exception records grouped by type and HTTP status. Nested exceptions can describe the same failure; these are not counts of failed requests."),
            [T("Failure type"), T("HTTP status"), T("Exception records")],
            exceptions.GroupBy(e => (Type: Text(e, "type") ?? T("Unknown"), Status: Text(e, "statusCode") ?? T("Not recorded")))
                .Select(g => new Row("error", [g.Key.Type, g.Key.Status, Count(g.Count())])).ToList(),
            T("No exception patterns can be calculated.")));
        page.Sections.Add(new Section("cleanup", T("Cleanup and restoration"),
            T("These failures are separate from the primary import failure and can require independent attention."),
            [T("Stage"), T("Failure type"), T("Recorded message")],
            cleanup.Select(c => new Row("error", [StageLabel(Text(c, "stage")),
                Text(c, "type") ?? T("Unknown"), Text(c, "message") ?? T("Not recorded")], c)).ToList(),
            T("No cleanup failures were recorded.")));
        page.Sections.Add(new Section("timeline", T("Progress timeline"),
            T("Recorded order is preserved. The final message precedes or accompanies the failure; it is not proof of its cause. Times are UTC."),
            [T("Time (UTC)"), T("Recorded progress")],
            progress.Select((p, i) => new Row(i == progress.Count - 1 ? "warning" : "info",
                [Text(p, "timestampUtc") ?? T("Not recorded"), Text(p, "message") ?? T("Not recorded")], p)).ToList(),
            T("No progress timeline was recorded.")));
        AddContext(page, obj, ["targetOwner", "ownerType", "targetProjectNumber", "targetProjectUrl",
            "occurredAtUtc", "runId", "browserAutomationEnabled", "application", "context",
            "sensitiveDiagnosticsState", "sensitiveDiagnosticsFile"]);
    }

    private static void BuildItems(Page page, JsonObject obj)
    {
        page.Title = T("Item import resume log");
        page.Purpose = T("Inspect durable item progress. This is a checkpoint, not a complete inventory of the source project.");
        if (obj["schemaVersion"] is not null
            && (obj["schemaVersion"] is not JsonValue version || !version.TryGetValue<int>(out var number)
                || number != ImportLog.CurrentSchemaVersion))
        {
            throw new InvalidDataException($"Unsupported item import-log schemaVersion; expected {ImportLog.CurrentSchemaVersion}. This report cannot safely interpret the checkpoint.");
        }
        var states = Dictionary(obj, "itemStates");
        var mappings = Dictionary(obj, "items", objectValues: false);
        var completed = states.Count(p => ItemDone(p.Value));
        var failed = states.Count(p => ItemError(p.Value));
        var pending = PendingRows(obj, [("pendingDrafts", "Draft"), ("pendingContents", "Issue / pull request"),
            ("pendingStatusUpdates", "Status update")]);
        var restoration = Boolean(obj, "templateRestorationRequired");
        var incomplete = states.Count - completed;
        page.Outcome = failed > 0 ? "error"
            : pending.Count > 0 || restoration == true || incomplete > 0 ? "warning"
            : states.Count == 0 || restoration is null ? "unknown" : "success";
        page.Conclusion = T(pending.Count > 0 ? "Unconfirmed operations need reconciliation"
            : restoration == true ? "Template restoration remains required"
            : incomplete > 0 || failed > 0 ? "Some recorded item work needs attention"
            : states.Count == 0 ? "No detailed item progress was recorded"
            : "Recorded item stages are complete");
        page.Explanation = Sentences(
            F("{0} of {1} tracked items have all three stages applied.", Count(completed), Count(states.Count)),
            F("Items with recorded errors: {0}.", Count(failed)),
            F("Unconfirmed operations: {0}.", Count(pending.Count)),
            T("Unlisted source items and other migration stages cannot be judged from this log."));
        if (pending.Count > 0)
        {
            page.Actions.Add(T("Reconcile pending operations against the target before any retry. Pending does not mean that creation failed; repeating it can create duplicates."));
        }
        if (restoration == true)
        {
            page.Actions.Add(T("Inspect the target template setting and complete the recorded restoration through the normal resume flow."));
        }
        if (incomplete > 0 || failed > 0)
        {
            page.Actions.Add(T("Inspect per-item errors and unapplied stages, then resume with the same snapshot and target log after resolving the cause."));
        }
        page.Actions.Add(T("Use project completion and verification reports to assess the whole migration. Do not delete this resume log."));
        if (obj["schemaVersion"] is null)
        {
            page.Actions.Add(T("Schema version is not recorded. This is a legacy or unversioned checkpoint; report readability does not establish that it can be resumed by the current CLI."));
        }
        page.Metric("Items with detailed state", states.Count);
        page.Metric("Fully applied", completed, states.Count > 0 && completed == states.Count ? "success" : "neutral");
        page.Metric("Incomplete", incomplete, "warning");
        page.Metric("Items with errors", failed, "error");
        page.Metric("Unconfirmed operations", pending.Count, "warning");
        if (states.Count > 0)
        {
            page.Progress = (T("Items with all stages applied"), completed, states.Count);
        }
        AddPending(page, pending);
        page.Sections.Add(new Section("stages", T("Remaining work by stage"),
            T("Counts are independent: one item can appear in several stages. Archive means the archive-state step was applied, not that every item is archived."),
            [T("Stage"), T("Applied"), T("Not applied"), T("Unknown"), T("Recorded errors")],
            ItemStages.Select(stage =>
            {
                var yes = states.Count(p => Boolean(p.Value, stage + "Applied") == true);
                var no = states.Count(p => Boolean(p.Value, stage + "Applied") == false);
                var missing = states.Count - yes - no;
                var errors = states.Count(p => !string.IsNullOrEmpty(Text(p.Value, stage + "Error"))
                    || stage == "fieldValues" && Array((JsonObject)p.Value!, "fieldValueFailures").Count > 0);
                return new Row(errors > 0 ? "error" : missing > 0 || states.Count == 0 ? "unknown" : no > 0 ? "warning" : "success",
                    [Label(stage), Count(yes), Count(no), Count(missing), Count(errors)]);
            }).ToList(), T("No item stages were recorded.")));
        page.Sections.Add(new Section("items", T("Item progress"),
            T("Content identity is used when present; source keys and target IDs remain available in expanded evidence. Items needing attention appear first."),
            [T("Item / content identity"), T("Field values"), T("Position"), T("Archive state"), T("Recorded errors")],
            states.Select(p => new Row(ItemError(p.Value) ? "error"
                : ItemStages.Any(s => Boolean(p.Value, s + "Applied") is null) ? "unknown"
                : ItemDone(p.Value) ? "success" : "warning",
                [Text(p.Value, "targetContentIdentity") ?? F("Source key: {0}", p.Key),
                    Applied(p.Value, "fieldValuesApplied"), Applied(p.Value, "positionApplied"), Applied(p.Value, "archiveApplied"),
                    ItemErrorText(p.Value)], p.Value, p.Key))
                .OrderBy(r => Priority(r.Level)).ToList(), T("No detailed item state was recorded.")));
        var fieldFailures = states.SelectMany(p => Array((JsonObject)p.Value!, "fieldValueFailures")
            .Select(context => new Row("error", [
                Text(p.Value, "targetContentIdentity") ?? F("Source key: {0}", p.Key),
                ContextText(context) ?? T("No field identity recorded"),
                T("Failure context only; the specific reason was not recorded here.")], context, p.Key))).ToList();
        page.Sections.Add(new Section("field-failures", T("Field-value failure context"),
            T("Each context identifies a failed field operation. These records do not contain detailed failure reasons; consult the import warnings for those."),
            [T("Item"), T("Affected field / operation"), T("Evidence limitation")], fieldFailures,
            T("No individual field-value failure contexts were recorded.")));
        if (mappings.Count > 0)
        {
            page.Sections.Add(new Section("mappings", T("Created item mappings"),
                T("These mappings record target IDs, not proof that all import stages completed."),
                [T("Source position"), T("Target item ID")], mappings.Select(p => new Row("info",
                    [p.Key, p.Value?.ToString() ?? T("Unknown")])).ToList(), T("No mappings recorded.")));
        }
        AddContext(page, obj, ["schemaVersion", "projectId", "sourceSnapshotFingerprint",
            "templateRestorationRequired", "statusUpdates"]);
    }

    private static void BuildProject(Page page, JsonObject obj)
    {
        page.Title = T("Project import resume log");
        page.Purpose = T("Inspect project-level completion and unconfirmed writes before continuing a migration.");
        var fields = Dictionary(obj, "createdFields", objectValues: false);
        var pending = PendingRows(obj, [("pendingFields", "Project field"), ("pendingIssueFields", "Issue field"),
            ("pendingIssueFieldLinks", "Issue field link"), ("pendingViews", "View")]);
        if (obj["pendingProject"] is JsonObject project)
        {
            pending.Insert(0, new Row("warning", [T("Project creation"), Text(project, "title") ?? T("Unknown title"),
                Text(project, "ownerLogin") ?? T("Unknown owner"), Text(project, "operationId") ?? T("Not recorded")], project));
        }
        else if (obj["pendingProject"] is not null)
        {
            throw new InvalidDataException("'pendingProject' must be an object or null.");
        }
        var deletion = Text(obj, "pendingProjectDeletionId");
        if (deletion is not null)
        {
            pending.Add(new Row("warning", [T("Project deletion"), deletion, T("Deletion not confirmed"), T("Not recorded")]));
        }
        var complete = Boolean(obj, "importCompleted");
        var warnings = Boolean(obj, "hasUnresolvedWarnings");
        page.Outcome = pending.Count > 0 || warnings == true || complete == false ? "warning"
            : complete is null || warnings is null ? "unknown" : "success";
        page.Conclusion = T(pending.Count > 0 ? "Unconfirmed project operations need reconciliation"
            : complete == true && warnings == false ? "The log records project import completion"
            : complete == false ? "Project import is not marked complete"
            : "Project completion cannot be confirmed");
        page.Explanation = Sentences(
            F("Completion: {0}. Unresolved warnings: {1}.", YesNo(complete), YesNo(warnings)),
            F("{0} operations remain unconfirmed.", Count(pending.Count)),
            T("A completion marker is not a source-to-target verification result."));
        if (pending.Count > 0)
        {
            page.Actions.Add(T("Inspect pending operations and their target evidence before retrying. A pending write may already have succeeded; do not clear the log or blindly recreate its target."));
        }
        if (warnings != false)
        {
            page.Actions.Add(T(warnings == true
                ? "Review the import's warnings and related failure report. This log flags warnings but does not contain their full explanations."
                : "Warning state was not recorded. Do not interpret an absent value as 'no warnings'."));
        }
        page.Actions.Add(T("Keep this log for the normal resume flow. Verify the destination separately after migration."));
        page.Metrics.Add(new Metric(T("Import completed"), YesNo(complete),
            complete switch { true => "success", false => "warning", _ => "unknown" }));
        page.Metrics.Add(new Metric(T("Unresolved warnings"), YesNo(warnings),
            warnings switch { true => "warning", false => "neutral", _ => "unknown" }));
        page.Metric("Created fields", fields.Count);
        page.Metric("Unconfirmed operations", pending.Count, "warning");
        AddPending(page, pending);
        page.Sections.Add(new Section("fields", T("Created fields"),
            T("Source field names and their saved target IDs. Creation does not imply that field values were applied to every item."),
            [T("Source field"), T("Target field ID")], fields.Select(p => new Row("info",
                [p.Key, p.Value?.ToString() ?? T("Unknown")])).ToList(), T("No created field mappings were recorded.")));
        AddContext(page, obj, ["createdProjectId", "importCompleted", "hasUnresolvedWarnings", "pendingProjectDeletionId"]);
    }

    private static List<Row> PendingRows(JsonObject obj, (string Property, string Kind)[] collections)
    {
        var rows = new List<Row>();
        foreach (var (property, kind) in collections)
        {
            foreach (var (key, entry) in Dictionary(obj, property))
            {
                rows.Add(new Row("warning", [T(kind),
                    Text(entry, "name") ?? Text(entry, "title") ?? Text(entry, "contentId") ?? F("Source key: {0}", key),
                    Join(Text(entry, "layout"), Text(entry, "dataType"),
                        Text(entry, "reconciledViewId") is { } id ? F("Reserved target: {0}", id) : null),
                    Text(entry, "operationId") ?? T("Not recorded")], entry, key));
            }
        }
        return rows;
    }

    private static void AddPending(Page page, List<Row> rows) => page.Sections.Add(new Section(
        "pending", T("Pending operations (unreconciled)"),
        T("These records mean the final outcome has not been confirmed in this log, not that the operation failed. Expand evidence for baseline IDs and reconciliation details."),
        [T("Operation kind"), T("Affected target"), T("Configuration"), T("Operation ID")], rows, T("No pending operations were recorded.")));

    private static void AddContext(Page page, JsonObject obj, string[] properties)
    {
        var rows = properties.Select(key => new Row("info", [Label(key), Value(obj, key)])).ToList();
        page.Sections.Add(new Section("context", T("Run and checkpoint context"),
            T("Recorded metadata only. Unknown means the value is absent or null."), [T("Property"), T("Recorded value")], rows,
            T("No context recorded.")));
    }

    private static List<JsonObject> Array(JsonObject obj, string key, bool required = false)
    {
        if (obj[key] is null && !required)
        {
            return [];
        }
        if (obj[key] is not JsonArray array || array.Any(n => n is not JsonObject))
        {
            throw new InvalidDataException($"'{key}' must be an array of objects.");
        }
        return array.Cast<JsonObject>().ToList();
    }

    private static List<KeyValuePair<string, JsonNode?>> Dictionary(JsonObject obj, string key, bool objectValues = true)
    {
        if (obj[key] is null)
        {
            return [];
        }
        if (obj[key] is not JsonObject dictionary || dictionary.Any(p =>
            objectValues ? p.Value is not JsonObject : p.Value is not JsonValue value || !value.TryGetValue<string>(out _)))
        {
            throw new InvalidDataException($"'{key}' must be an object containing {(objectValues ? "objects" : "strings")}.");
        }
        return [.. dictionary];
    }

    private static string? Text(JsonNode? node, string key)
    {
        var value = (node as JsonObject)?[key];
        if (value is null)
        {
            return null;
        }
        return value is JsonValue scalar && scalar.TryGetValue<string>(out var text)
            ? text : throw new InvalidDataException($"'{key}' must be a string or null.");
    }

    private static bool? Boolean(JsonNode? node, string key)
    {
        var value = (node as JsonObject)?[key];
        if (value is null)
        {
            return null;
        }
        return value is JsonValue scalar && scalar.TryGetValue<bool>(out var flag)
            ? flag : throw new InvalidDataException($"'{key}' must be a boolean or null.");
    }

    private static string Value(JsonObject obj, string key) => obj[key] switch
    {
        null => T("Unknown"),
        JsonValue value when value.TryGetValue<bool>(out var flag) => YesNo(flag),
        JsonObject value when key == "context" => ContextText(value) ?? T("Unknown"),
        JsonObject value when key == "application" => Join(value.Select(p => $"{Label(p.Key)}: {p.Value?.ToString() ?? T("Unknown")}").ToArray()),
        _ => obj[key]!.ToString(),
    };
    private static string? ContextText(JsonNode? value)
    {
        if (value is not JsonObject context)
        {
            return null;
        }
        var lines = new List<string>();
        foreach (var key in new[] { "source", "target", "item", "element" })
        {
            if (context[key] is not JsonObject identity)
            {
                continue;
            }
            var parts = new List<string>();
            foreach (var part in new[] { "kind", "name", "title", "owner", "repository", "number", "targetId" })
            {
                if (identity[part] is not null)
                {
                    parts.Add($"{Label(part)}: {Value(identity, part)}");
                }
            }
            lines.Add($"{Label(key)}: {string.Join(", ", parts)}");
        }
        if (Text(context, "operation") is { } operation)
        {
            lines.Add(F("Operation: {0}", operation));
        }
        return lines.Count > 0 ? string.Join("\n", lines) : null;
    }
    private static string YesNo(bool? value) => T(value switch { true => "Yes", false => "No", _ => "Unknown" });
    private static string Applied(JsonNode? obj, string key) => T(Boolean(obj, key) switch
        { true => "Applied", false => "Not applied", _ => "Unknown" });
    private static bool ItemDone(JsonNode? obj) => ItemStages.All(s => Boolean(obj, s + "Applied") == true);
    private static bool ItemError(JsonNode? obj) => ItemStages.Any(s => !string.IsNullOrEmpty(Text(obj, s + "Error")))
        || obj is JsonObject item && Array(item, "fieldValueFailures").Count > 0;
    private static string ItemErrorText(JsonNode? obj)
    {
        var messages = ItemStages.Select(s => Text(obj, s + "Error") is { Length: > 0 } error
            ? $"{Label(s)}: {error}" : null).ToList();
        if (obj is JsonObject item)
        {
            var failures = Array(item, "fieldValueFailures");
            if (failures.Count > 0)
            {
                messages.Add(F("Field-value failure contexts: {0}", Count(failures.Count)));
                messages.AddRange(failures.Select(c => ContextText(c) ?? T("Unknown field / operation")));
            }
        }
        return Join(messages.ToArray());
    }
    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Join(params string?[] values) => string.Join("\n", values.Where(v => !string.IsNullOrEmpty(v))) is { Length: > 0 } text
        ? text : T("Not recorded");
    // Japanese sentences need no separator, but recorded English messages still do.
    private static string Sentences(params string?[] parts)
    {
        var text = new StringBuilder();
        foreach (var part in parts.Where(p => !string.IsNullOrEmpty(p)))
        {
            if (text.Length > 0 && (!japanese || char.IsAscii(text[^1])))
            {
                text.Append(' ');
            }
            text.Append(part);
        }
        return text.ToString();
    }
    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
    private static string Severity(string? value) => value switch
        { "Error" => "error", "Warning" => "warning", "Info" => "info", _ => "unknown" };
    private static string SeverityLabel(string? value) => value switch
        { "Error" or "Warning" or "Info" => T(value), null => T("Unknown"), _ => value };
    private static string VerifyLevel(string? value) => value switch
        { "Mismatch" or "NotVerified" => "error", "PartialMatch" => "warning", "Match" => "success", "NotApplicable" => "info", _ => "unknown" };
    private static bool KnownVerifyStatus(string? value) => value is "Mismatch" or "PartialMatch" or "NotVerified" or "Match" or "NotApplicable";
    private static string HumanStatus(string? value) => value switch
    {
        "PartialMatch" => T("Partial match"), "NotVerified" => T("Not verified"), "NotApplicable" => T("Not applicable"),
        "Match" or "Mismatch" => T(value), null => T("Unknown"), _ => value,
    };
    private static int Priority(string level) => level switch { "error" => 0, "warning" => 1, "unknown" => 2, "info" => 3, _ => 4 };
    private static string LevelLabel(string level) => T(level switch
        { "error" => "Error", "warning" => "Warning", "success" => "OK", "info" => "Info", _ => "Unknown" });
    private static string OutcomeLabel(string level) => T(level switch
        { "error" => "Action required", "warning" => "Needs review", "success" => "No issues recorded", _ => "Insufficient evidence" });
    private static string Label(string value)
    {
        var result = new StringBuilder();
        foreach (var character in value)
        {
            if (char.IsUpper(character) && result.Length > 0)
            {
                result.Append(' ');
            }
            result.Append(character);
        }
        return T(char.ToUpperInvariant(result[0]) + result.ToString()[1..]);
    }

    // Stage codes written by ImportFailureDiagnostics.SetStage, in execution order (src/Ghpmv.Cli/Program.cs).
    private static readonly (string Code, string Label, string Verb, string Meaning)[] ImportSteps =
    [
        ("initializing", "Starting the import", "starting the import",
            "Reading command options and credentials. Nothing has been read from the snapshot or written to the target yet."),
        ("loading-snapshot", "Reading the exported snapshot", "reading the exported snapshot",
            "Loading snapshot.json from the input directory. Nothing has been written to the target yet."),
        ("preflight", "Checking permissions and prerequisites", "checking permissions and prerequisites",
            "Checking the token, repository and user mappings, organization permissions and, when enabled, the browser login. This run has not written to the target yet; an earlier interrupted run may have."),
        ("importing-project", "Creating or updating the project and its settings", "creating or updating the project and its settings",
            "Creating the target project (or updating an existing one) and applying its title, description, fields and options, collaborators, linked repositories and views. Items have not been imported yet."),
        ("importing-items", "Importing items (issues, pull requests and draft issues)", "importing items (issues, pull requests and draft issues)",
            "The project and its fields already exist. Adding issues and pull requests, creating draft issues, then applying field values, positions and archived state. Each finished item is recorded in import-log.json, so a rerun resumes from there."),
        ("importing-status-updates", "Importing status updates", "importing status updates",
            "Project settings and items are done. Copying the project's status updates."),
        ("importing-browser-enrichment", "Applying browser-only settings (views and workflows)", "applying browser-only settings (views and workflows)",
            "The API import is done. Using browser automation to replay settings the public API cannot write, such as View details and Workflows."),
        ("finalizing-template-state", "Finalizing the template setting", "finalizing the template setting",
            "All content is imported. Setting whether the target project is marked as a template, as the last step."),
    ];

    // Stage codes used for cleanup after the import has already ended (ImportFailureFinalizer).
    private static readonly (string Code, string Label, string Meaning)[] CleanupStageInfo =
    [
        ("restoring-template-state", "Restoring the template setting after the failure",
            "After the import ended, ghpmv tried to restore the target project's template setting, which is temporarily changed during import."),
        ("disposing-browser-session", "Closing the browser session",
            "After the import ended, ghpmv tried to close the automated browser."),
        ("disposing-api-diagnostics", "Closing the sensitive API diagnostics file",
            "After the import ended, ghpmv tried to close the raw API diagnostics file enabled by --allow-sensitive-diagnostics."),
    ];

    private static readonly HashSet<string> CleanupStages = CleanupStageInfo.Select(s => s.Code).ToHashSet(StringComparer.Ordinal);

    // Steps of a resume-log save (ResumeLogFile).
    private static readonly Dictionary<string, string> SaveStages = new(StringComparer.Ordinal)
    {
        ["creating-directory"] = "Creating the log directory",
        ["opening-temporary-file"] = "Opening the temporary file",
        ["writing-temporary-file"] = "Writing the temporary file",
        ["flushing-temporary-file"] = "Flushing the temporary file to disk",
        ["replacing-log"] = "Replacing the log with the new version",
        ["moving-log"] = "Moving the new log into place",
    };

    private static string StageLabel(string? code)
    {
        if (code is null)
        {
            return T("Unknown stage");
        }
        var label = ImportSteps.FirstOrDefault(s => s.Code == code).Label
            ?? CleanupStageInfo.FirstOrDefault(s => s.Code == code).Label
            ?? (SaveStages.TryGetValue(code, out var save) ? save : null);
        return label is null ? code : F("{0} ({1})", T(label), code);
    }

    private static string StageVerb(string? code) =>
        ImportSteps.FirstOrDefault(s => s.Code == code).Verb is { } verb ? T(verb)
        : CleanupStageInfo.FirstOrDefault(s => s.Code == code).Label is { } cleanup ? T(cleanup).ToLowerInvariantFirst(japanese)
        : code is null ? T("at an unrecorded stage") : F("at stage \"{0}\"", code);

    private static string StageMeaning(string? code)
    {
        var meaning = ImportSteps.FirstOrDefault(s => s.Code == code).Meaning
            ?? CleanupStageInfo.FirstOrDefault(s => s.Code == code).Meaning;
        return meaning is not null
            ? F("What this stage does: {0}", T(meaning))
            : code is null
                ? T("The failure stage was not recorded, so it is unknown how far the import progressed.")
                : F("This report does not recognize the stage name \"{0}\"; it may come from a different ghpmv version.", code);
    }

    private static string ToLowerInvariantFirst(this string value, bool keep) =>
        keep || value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];

    private static string TimeSpanLabel(List<JsonObject> progress)
    {
        if (progress.Count < 2
            || !DateTimeOffset.TryParse(Text(progress[0], "timestampUtc"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !DateTimeOffset.TryParse(Text(progress[^1], "timestampUtc"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
            || end < start)
        {
            return T("Unknown");
        }
        return (end - start).ToString("c", CultureInfo.InvariantCulture);
    }

    private static string T(string english) => japanese && Japanese.TryGetValue(english, out var translated) ? translated : english;
    private static string F(string english, params object?[] args) => string.Format(CultureInfo.InvariantCulture, T(english), args);

    private sealed record Row(string Level, string[] Cells, JsonNode? Evidence = null, string? SourceKey = null);
    private sealed record Section(string Id, string Title, string Description, string[] Headers, List<Row> Rows, string Empty, bool Entries = false);
    private sealed record Metric(string Label, string Value, string Level);

    private sealed class Page(string sourceName, JsonObject source)
    {
        public string Title { get; set; } = "";
        public string Purpose { get; set; } = "";
        public string Outcome { get; set; } = "unknown";
        public string Conclusion { get; set; } = "";
        public string Explanation { get; set; } = "";
        public List<string> Actions { get; } = [];
        public List<Metric> Metrics { get; } = [];
        public (string Label, int Done, int Total)? Progress { get; set; }
        public List<Section> Sections { get; } = [];

        // A count metric is highlighted with its level only when nonzero.
        public void Metric(string label, int value, string level = "neutral") =>
            Metrics.Add(new Metric(T(label), Count(value), value > 0 ? level : "neutral"));

        public void RenderRoot(StringBuilder html, string lang, bool includeSource)
        {
            var p = lang == "en" ? "" : lang + "-";
            html.Append("<div class=\"lang-root\" data-l=\"").Append(lang).Append("\" lang=\"").Append(lang)
                .Append("\" data-title=\"").Append(Encode(Title + " - ghpmv"))
                .Append("\" data-section-count=\"").Append(Encode(T("{0} of {1} rows shown")))
                .Append("\" data-total-count=\"").Append(Encode(T("{0} of {1} detail rows shown"))).Append("\">")
                .Append("<a class=\"skip\" href=\"#").Append(p).Append("analysis\">").Append(Encode(T("Skip to analysis"))).Append("</a><div class=\"shell\">");
            RenderSidebar(html, p);
            html.Append("<main><header class=\"top\"><div class=\"top-row\"><p class=\"eyebrow\">ghpmv · ").Append(Encode(sourceName)).Append("</p>");
            LanguageSwitch(html, lang);
            html.Append("</div><h1>")
                .Append(Encode(Title)).Append("</h1><p class=\"purpose\">").Append(Encode(Purpose)).Append("</p><ul class=\"meta\"><li>")
                .Append(Encode(F("Source: {0}", sourceName))).Append("</li><li>").Append(Encode(T("Offline report"))).Append("</li><li>")
                .Append(Encode(T("Recorded evidence only"))).Append("</li></ul></header>")
                .Append("<section class=\"verdict ").Append(Outcome).Append("\" aria-labelledby=\"").Append(p).Append("conclusion\"><div class=\"verdict-main\"><span class=\"pill ")
                .Append(Outcome).Append("\">").Append(Encode(OutcomeLabel(Outcome))).Append("</span><h2 id=\"").Append(p).Append("conclusion\">").Append(Encode(Conclusion))
                .Append("</h2><p>").Append(Encode(Explanation)).Append("</p></div><div class=\"verdict-actions\"><h3>")
                .Append(Encode(T("Before you proceed"))).Append("</h3><ol>");
            foreach (var action in Actions)
            {
                html.Append("<li>").Append(Encode(action)).Append("</li>");
            }
            html.Append("</ol></div></section><dl class=\"metrics\">");
            foreach (var metric in Metrics)
            {
                html.Append("<div class=\"metric ").Append(metric.Level).Append("\"><dt>").Append(Encode(metric.Label)).Append("</dt><dd>")
                    .Append(Encode(metric.Value)).Append("</dd></div>");
            }
            html.Append("</dl>");
            if (Progress is { } progress)
            {
                var percent = progress.Total == 0 ? 0 : progress.Done * 100 / progress.Total;
                html.Append("<div class=\"progress\"><p><span>").Append(Encode(progress.Label)).Append("</span><span>")
                    .Append(Encode(F("{0} of {1}", Count(progress.Done), Count(progress.Total)))).Append("</span></p>")
                    .Append("<div class=\"bar\" aria-hidden=\"true\"><span style=\"width:")
                    .Append(percent.ToString(CultureInfo.InvariantCulture)).Append("%\"></span></div></div>");
            }
            html.Append("<div id=\"").Append(p).Append("analysis\"><div id=\"").Append(p).Append("sections\" class=\"sections\">");
            foreach (var section in Sections)
            {
                RenderSection(html, section, p);
            }
            html.Append("</div></div>");
            if (includeSource)
            {
                // Shared by both language roots; the script moves it into the visible root.
                html.Append("<section id=\"source\"><h2>");
                Both(html, "Source JSON");
                html.Append("</h2><p class=\"section-description\">");
                Both(html, "The complete input is included for inspection. It may contain business data; review before sharing. Referenced files are not loaded.");
                html.Append("</p><details><summary>");
                Both(html, "Inspect the complete source JSON");
                html.Append("</summary><pre>").Append(Encode(source.ToJsonString(DisplayJsonOptions))).Append("</pre></details></section>");
            }
            html.Append("<footer>").Append(Encode(T("ghpmv · This report does not modify the target or resume logs. No external resources or API calls.")))
                .Append("</footer></main></div></div>");
        }

        // Emits text in both languages; CSS shows only the active one.
        private static void Both(StringBuilder html, string english)
        {
            var current = japanese;
            japanese = false;
            html.Append("<span data-l=\"en\">").Append(Encode(T(english))).Append("</span>");
            japanese = true;
            html.Append("<span data-l=\"ja\" lang=\"ja\">").Append(Encode(T(english))).Append("</span>");
            japanese = current;
        }

        private static void LanguageSwitch(StringBuilder html, string lang)
        {
            html.Append("<div class=\"lang-switch\" role=\"group\" aria-label=\"").Append(Encode(T("Report language"))).Append("\">");
            foreach (var (value, label) in new[] { ("en", "English"), ("ja", "日本語") })
            {
                html.Append("<button type=\"button\" lang=\"").Append(value).Append("\" data-set-lang=\"").Append(value).Append("\" aria-pressed=\"")
                    .Append(value == lang ? "true" : "false").Append("\">").Append(label).Append("</button>");
            }
            html.Append("</div>");
        }

        private void RenderSidebar(StringBuilder html, string p)
        {
            html.Append("<aside class=\"sidebar\"><p class=\"brand\"><span class=\"mark ").Append(Outcome).Append("\" aria-hidden=\"true\"></span>")
                .Append(Encode(T("ghpmv report"))).Append("</p><nav aria-label=\"").Append(Encode(T("Report sections")))
                .Append("\"><p class=\"side-heading\">").Append(Encode(T("Sections"))).Append("</p><ul>");
            foreach (var section in Sections)
            {
                var level = section.Rows.Count == 0 ? "none" : section.Rows.MinBy(r => Priority(r.Level))!.Level;
                html.Append("<li><a href=\"#").Append(p).Append(section.Id).Append("\"><span class=\"dot ").Append(level)
                    .Append("\" aria-hidden=\"true\"></span><span class=\"nav-title\">").Append(Encode(section.Title))
                    .Append("</span><span class=\"nav-count\" data-count-for=\"").Append(p).Append(section.Id).Append("\">")
                    .Append(Count(section.Rows.Count)).Append("</span></a></li>");
            }
            html.Append("<li><a href=\"#source\"><span class=\"dot none\" aria-hidden=\"true\"></span><span class=\"nav-title\">")
                .Append(Encode(T("Source JSON"))).Append("</span></a></li></ul></nav><div class=\"controls\"><p class=\"side-heading\">")
                .Append(Encode(T("Filters"))).Append("</p><label>").Append(Encode(T("Search all detail rows")))
                .Append("<input id=\"").Append(p).Append("q\" class=\"filter-q\" type=\"search\" placeholder=\"").Append(Encode(T("Name, message, ID or evidence")))
                .Append("\" aria-controls=\"").Append(p).Append("sections\"></label><label>").Append(Encode(T("Show")))
                .Append("<select id=\"").Append(p).Append("level\" class=\"filter-level\" aria-controls=\"").Append(p).Append("sections\">");
            foreach (var (value, label) in new[] { ("all", "All states"), ("attention", "Needs attention (errors, warnings, unknown)"),
                ("error", "Errors"), ("warning", "Warnings / incomplete"), ("unknown", "Unknown"), ("success", "Applied / match"), ("info", "Information") })
            {
                html.Append("<option value=\"").Append(value).Append("\">").Append(Encode(T(label))).Append("</option>");
            }
            html.Append("</select></label><button id=\"").Append(p).Append("reset\" class=\"filter-reset\" type=\"button\">").Append(Encode(T("Reset filters")))
                .Append("</button><p id=\"").Append(p).Append("count\" class=\"filter-count\" role=\"status\" aria-live=\"polite\"></p><p class=\"scope\">")
                .Append(Encode(T("Filters apply to every detail table and expanded evidence. The summary above always describes the complete file.")))
                .Append("</p></div></aside>");
        }

        private static void RenderSection(StringBuilder html, Section section, string p)
        {
            html.Append("<section id=\"").Append(p).Append(section.Id).Append("\" class=\"report-section card\"><div class=\"section-head\"><h2>")
                .Append(Encode(section.Title)).Append("</h2>");
            if (section.Rows.Count > 0)
            {
                html.Append("<p class=\"section-count\"></p>");
            }
            html.Append("</div><p class=\"section-description\">").Append(Encode(section.Description)).Append("</p>");
            if (section.Rows.Count == 0)
            {
                html.Append("<p class=\"empty\">").Append(Encode(section.Empty)).Append("</p></section>");
                return;
            }
            var noResults = "<p class=\"no-results\" hidden>" + Encode(T("No rows match these filters. Change the search or reset filters.")) + "</p></section>";
            if (section.Entries)
            {
                html.Append("<div class=\"detail-content\">");
                foreach (var row in section.Rows)
                {
                    html.Append("<article class=\"detail-row exception ").Append(row.Level).Append("\" data-status=\"").Append(row.Level)
                        .Append("\"><h3>");
                    Badge(html, row.Level);
                    html.Append("<span>").Append(Encode(row.Cells[0])).Append("</span></h3><dl>");
                    for (var i = 1; i < row.Cells.Length; i++)
                    {
                        html.Append("<div><dt>").Append(Encode(section.Headers[i])).Append("</dt><dd class=\"cell\">")
                            .Append(Encode(row.Cells[i])).Append("</dd></div>");
                    }
                    html.Append("</dl>");
                    Evidence(html, row);
                    html.Append("</article>");
                }
                html.Append("</div>").Append(noResults);
                return;
            }
            html.Append("<div class=\"table-scroll detail-content\" tabindex=\"0\" role=\"region\" aria-label=\"")
                .Append(Encode(section.Title)).Append("\"><table><thead><tr>");
            foreach (var heading in section.Headers)
            {
                html.Append("<th scope=\"col\">").Append(Encode(heading)).Append("</th>");
            }
            html.Append("</tr></thead><tbody>");
            foreach (var row in section.Rows)
            {
                html.Append("<tr class=\"detail-row ").Append(row.Level).Append("\" data-status=\"").Append(row.Level).Append("\">");
                for (var i = 0; i < row.Cells.Length; i++)
                {
                    html.Append("<td>");
                    if (i == 0 && row.Cells[0] != LevelLabel(row.Level))
                    {
                        Badge(html, row.Level);
                    }
                    html.Append("<div class=\"cell\">").Append(Encode(row.Cells[i])).Append("</div>");
                    if (i == row.Cells.Length - 1)
                    {
                        Evidence(html, row);
                    }
                    html.Append("</td>");
                }
                html.Append("</tr>");
            }
            html.Append("</tbody></table></div>").Append(noResults);
        }

        private static void Badge(StringBuilder html, string level) =>
            html.Append("<span class=\"badge ").Append(level).Append("\">").Append(Encode(LevelLabel(level))).Append("</span>");

        private static void Evidence(StringBuilder html, Row row)
        {
            if (row.Evidence is null)
            {
                return;
            }
            if (Text(row.Evidence, "stackTrace") is { } trace)
            {
                html.Append("<details><summary>").Append(Encode(T("Stack trace"))).Append("</summary><pre>").Append(Encode(trace)).Append("</pre></details>");
            }
            html.Append("<details><summary>").Append(Encode(T("Recorded evidence"))).Append("</summary>");
            if (row.SourceKey is not null)
            {
                html.Append("<p>").Append(Encode(F("Source key: {0}", row.SourceKey))).Append("</p>");
            }
            html.Append("<pre>").Append(Encode(row.Evidence.ToJsonString(DisplayJsonOptions))).Append("</pre></details>");
        }
    }

    private const string Style = """
        :root{color-scheme:light;--ink:#14212f;--muted:#5a6878;--line:#dde3ea;--bg:#f4f6f8;--paper:#fff;--soft:#f7f9fb;--accent:#1f5fae;
        --error:#b42318;--error-bg:#fef1f0;--warning:#935300;--warning-bg:#fff5df;--unknown:#6941a5;--unknown-bg:#f4effc;
        --success:#18743a;--success-bg:#ebf7ef;--info:#2b5d8a;--info-bg:#eef4fa;--neutral:#5a6878;--neutral-bg:#f1f3f5}
        *{box-sizing:border-box}html{scroll-behavior:smooth}
        body{margin:0;background:var(--bg);color:var(--ink);font:14.5px/1.6 "Segoe UI","Yu Gothic UI","Meiryo",system-ui,sans-serif;font-variant-numeric:tabular-nums}
        .shell{display:grid;grid-template-columns:272px minmax(0,1fr);min-height:100vh}
        .sidebar{position:sticky;top:0;height:100vh;overflow:auto;background:var(--paper);border-right:1px solid var(--line);padding:22px 18px}
        .brand{display:flex;align-items:center;gap:10px;font-weight:700;margin:0 0 22px;font-size:.95rem}
        .mark{width:12px;height:12px;border-radius:50%;background:var(--neutral)}
        .side-heading{font-size:.7rem;font-weight:700;letter-spacing:.08em;text-transform:uppercase;color:var(--muted);margin:0 0 8px}
        nav ul{list-style:none;margin:0 0 24px;padding:0}nav a{display:flex;align-items:center;gap:9px;padding:7px 9px;border-radius:6px;color:var(--ink);text-decoration:none;font-size:.88rem}
        nav a:hover{background:var(--neutral-bg)}.nav-title{flex:1;line-height:1.35}
        .nav-count{font-size:.75rem;color:var(--muted);background:var(--neutral-bg);border-radius:999px;padding:0 8px;min-width:26px;text-align:center}
        .dot{width:8px;height:8px;border-radius:50%;flex:none;background:var(--line)}
        .controls{border-top:1px solid var(--line);padding-top:18px}.controls label{display:block;font-size:.8rem;font-weight:600;margin-bottom:12px}
        input,select,button{font:inherit;color:var(--ink);border:1px solid #b8c3ce;border-radius:6px;background:white;padding:7px 10px;min-height:36px}
        input,select{display:block;margin-top:5px;width:100%}button{cursor:pointer;width:100%;background:var(--soft)}button:hover{background:#e7edf3}
        #count{font-size:.8rem;margin:12px 0 6px;color:var(--muted)}.scope{font-size:.75rem;color:var(--muted);margin:0}
        main{padding:30px 40px 48px;max-width:1240px;width:100%}
        .eyebrow{font-size:.78rem;color:var(--muted);margin:0 0 4px;overflow-wrap:anywhere}
        h1{font-size:1.85rem;line-height:1.2;margin:0 0 6px;letter-spacing:-.02em}h2{font-size:1.15rem;line-height:1.35;margin:0}
        p{margin:0 0 10px}.purpose{color:var(--muted);max-width:90ch;margin-bottom:10px}
        .meta{display:flex;flex-wrap:wrap;gap:6px;list-style:none;margin:0 0 20px;padding:0}
        .meta li{font-size:.75rem;color:var(--muted);background:var(--paper);border:1px solid var(--line);border-radius:999px;padding:1px 10px;overflow-wrap:anywhere}
        .verdict{display:grid;grid-template-columns:minmax(0,1.15fr) minmax(0,1fr);gap:28px;background:var(--paper);border:1px solid var(--line);
        border-left:6px solid var(--neutral);border-radius:10px;padding:22px 26px;margin-bottom:16px;box-shadow:0 1px 2px rgb(20 33 47/.05)}
        .verdict h2{font-size:1.45rem;margin:12px 0 10px;line-height:1.3}.verdict p{margin:0;color:#2c3a49}
        .verdict-actions{background:var(--soft);border-radius:8px;padding:16px 18px}.verdict h3{font-size:.85rem;margin:0 0 10px}
        .verdict ol{margin:0;padding-left:20px;font-size:.88rem}.verdict li+li{margin-top:7px}
        .pill{display:inline-flex;align-items:center;gap:7px;font-size:.78rem;font-weight:700;color:white;background:var(--neutral);border-radius:999px;padding:3px 12px}
        .pill:before{content:"";width:7px;height:7px;border-radius:50%;background:currentColor;opacity:.85}
        .metrics{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px;margin:0 0 16px}
        .metric{background:var(--paper);border:1px solid var(--line);border-top:3px solid var(--line);border-radius:8px;padding:12px 16px}
        .metric dt{color:var(--muted);font-size:.78rem}.metric dd{font-size:1.55rem;font-weight:700;margin:2px 0 0;line-height:1.25;overflow-wrap:anywhere}
        .progress{background:var(--paper);border:1px solid var(--line);border-radius:8px;padding:12px 16px;margin-bottom:16px}
        .progress p{display:flex;justify-content:space-between;font-size:.82rem;color:var(--muted);margin:0 0 8px}
        .bar{height:8px;border-radius:999px;background:var(--neutral-bg);overflow:hidden}.bar span{display:block;height:100%;background:var(--success);border-radius:999px}
        .card,#source{background:var(--paper);border:1px solid var(--line);border-radius:10px;padding:20px 24px;margin:0 0 16px;scroll-margin-top:16px}
        .section-head{display:flex;align-items:baseline;justify-content:space-between;gap:16px;margin-bottom:4px}
        .section-count{font-size:.78rem;color:var(--muted);margin:0;white-space:nowrap}.section-description{color:var(--muted);font-size:.85rem;max-width:100ch;margin-bottom:14px}
        .table-scroll{overflow:auto;border:1px solid var(--line);border-radius:8px}table{border-collapse:collapse;width:100%;font-size:.88rem}
        th{text-align:left;background:var(--soft);color:var(--muted);font-size:.75rem;font-weight:700;letter-spacing:.02em;white-space:nowrap}
        th,td{padding:10px 14px;border-bottom:1px solid var(--line);vertical-align:top}tbody tr:last-child td{border-bottom:0}
        tbody tr:hover td{background:#fafbfc}td:first-child{box-shadow:inset 3px 0 0 var(--line)}
        .cell{white-space:pre-wrap;overflow-wrap:anywhere;min-width:60px;max-width:70ch}
        .badge{display:inline-flex;align-items:center;gap:5px;font-size:.7rem;font-weight:700;border-radius:999px;padding:1px 9px;margin:0 0 5px;background:var(--neutral-bg);color:var(--neutral)}
        .badge:before{content:"";width:6px;height:6px;border-radius:50%;background:currentColor}
        details{margin-top:8px}summary{cursor:pointer;color:var(--accent);font-size:.8rem}
        .exception{border:1px solid var(--line);border-left:4px solid var(--line);border-radius:8px;padding:16px 18px;margin-bottom:12px}
        .exception h3{display:flex;align-items:center;gap:10px;margin:0 0 10px;font-size:.98rem;overflow-wrap:anywhere}.exception h3 .badge{margin:0}
        .exception dl{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:10px 24px;margin:0}
        .exception dt{color:var(--muted);font-size:.75rem;font-weight:600}.exception dd{margin:2px 0 0;max-width:none;font-size:.88rem}
        pre{font:12px/1.55 ui-monospace,"Cascadia Mono",Consolas,monospace;white-space:pre-wrap;overflow-wrap:anywhere;max-height:420px;overflow:auto;background:var(--soft);border:1px solid var(--line);border-radius:6px;padding:12px}
        .empty,.no-results{background:var(--soft);border-radius:8px;padding:12px 14px;color:var(--muted);margin:0;font-size:.85rem}
        footer{color:var(--muted);font-size:.75rem;padding-top:8px}
        .error{--tone:var(--error);--tone-bg:var(--error-bg)}.warning{--tone:var(--warning);--tone-bg:var(--warning-bg)}
        .unknown{--tone:var(--unknown);--tone-bg:var(--unknown-bg)}.success{--tone:var(--success);--tone-bg:var(--success-bg)}.info{--tone:var(--info);--tone-bg:var(--info-bg)}
        .verdict.error,.verdict.warning,.verdict.unknown,.verdict.success{border-left-color:var(--tone)}
        .pill.error,.pill.warning,.pill.unknown,.pill.success,.mark.error,.mark.warning,.mark.unknown,.mark.success,.dot.error,.dot.warning,.dot.unknown,.dot.success,.dot.info{background:var(--tone)}
        .pill:before{background:white}
        .badge.error,.badge.warning,.badge.unknown,.badge.success,.badge.info{color:var(--tone);background:var(--tone-bg)}
        .metric.error,.metric.warning,.metric.unknown,.metric.success,.metric.info{border-top-color:var(--tone)}
        .metric.error dd,.metric.warning dd,.metric.unknown dd,.metric.success dd{color:var(--tone)}
        tr.error td:first-child,tr.warning td:first-child,tr.unknown td:first-child,tr.success td:first-child,tr.info td:first-child{box-shadow:inset 3px 0 0 var(--tone)}
        .exception.error,.exception.warning,.exception.unknown{border-left-color:var(--tone)}
        :focus-visible{outline:3px solid #2f6fbf;outline-offset:2px}.skip{position:absolute;left:12px;top:-80px;padding:10px;background:white;z-index:2}.skip:focus{top:12px}
        [hidden]{display:none!important}
        html[lang=en] [data-l=ja],html[lang=ja] [data-l=en]{display:none!important}
        .top-row{display:flex;align-items:center;justify-content:space-between;gap:16px;margin-bottom:4px}.top-row .eyebrow{margin:0}
        .lang-switch{display:inline-flex;flex:none;border:1px solid #b8c3ce;border-radius:8px;overflow:hidden;background:var(--paper)}
        .lang-switch button{width:auto;min-height:32px;border:0;border-radius:0;background:transparent;padding:4px 14px;font-size:.82rem;font-weight:600;color:var(--muted)}
        .lang-switch button+button{border-left:1px solid #b8c3ce}.lang-switch button:hover{background:var(--neutral-bg)}
        .lang-switch button[aria-pressed=true]{background:var(--accent);color:white}
        @media(max-width:959px){.shell{display:block}.sidebar{position:static;height:auto;border-right:0;border-bottom:1px solid var(--line)}main{padding:24px}.verdict{grid-template-columns:1fr}}
        @media print{body{background:white}.sidebar,.skip,.lang-switch{display:none}.shell{display:block}main{padding:0}.table-scroll,pre{overflow:visible;max-height:none}tr,.exception{break-inside:avoid}}
        """;

    private const string Script = """
        (() => {
          const roots = [...document.querySelectorAll('.lang-root')];
          const source = document.getElementById('source');
          const format = (template, shown, total) => template.replace('{0}', shown).replace('{1}', total);
          function setup(root) {
            const q = root.querySelector('.filter-q'), level = root.querySelector('.filter-level');
            const sectionTemplate = root.dataset.sectionCount, totalTemplate = root.dataset.totalCount;
            const sections = [...root.querySelectorAll('.report-section')];
            const rows = [...root.querySelectorAll('.detail-row')];
            const texts = new Map(rows.map(row => [row, row.textContent.toLowerCase()]));
            function filter() {
              const query = q.value.trim().toLowerCase();
              let shown = 0;
              for (const section of sections) {
                const entries = [...section.querySelectorAll('.detail-row')];
                if (!entries.length) continue;
                let visible = 0;
                for (const row of entries) {
                  const state = row.dataset.status;
                  const matches = level.value === 'all' || level.value === state ||
                    (level.value === 'attention' && ['error', 'warning', 'unknown'].includes(state));
                  row.hidden = !matches || !texts.get(row).includes(query);
                  if (!row.hidden) visible++;
                }
                shown += visible;
                section.querySelector('.section-count').textContent = format(sectionTemplate, visible, entries.length);
                const navCount = root.querySelector(`[data-count-for="${section.id}"]`);
                if (navCount) navCount.textContent = visible === entries.length ? `${entries.length}` : `${visible}/${entries.length}`;
                section.querySelector('.detail-content').hidden = visible === 0;
                section.querySelector('.no-results').hidden = visible !== 0;
              }
              root.querySelector('.filter-count').textContent = format(totalTemplate, shown, rows.length);
            }
            q.addEventListener('input', filter);
            level.addEventListener('change', filter);
            root.querySelector('.filter-reset').addEventListener('click', () => {q.value = ''; level.value = 'all'; filter(); q.focus();});
            return {root, q, level, filter};
          }
          const views = new Map(roots.map(root => [root.dataset.l, setup(root)]));
          function show(lang, focus) {
            const to = views.get(lang);
            if (!to) return;
            const from = views.get(document.documentElement.lang);
            if (from && from !== to) {to.q.value = from.q.value; to.level.value = from.level.value;}
            document.documentElement.lang = lang;
            document.title = to.root.dataset.title;
            if (source) to.root.querySelector('main > footer').before(source);
            for (const button of to.root.querySelectorAll('[data-set-lang]')) button.setAttribute('aria-pressed', String(button.dataset.setLang === lang));
            to.filter();
            if (focus) to.root.querySelector(`[data-set-lang="${lang}"]`).focus();
          }
          document.addEventListener('click', event => {
            const button = event.target.closest('[data-set-lang]');
            if (button) show(button.dataset.setLang, true);
          });
          show(document.documentElement.lang, false);
        })();
        """;

    private static readonly Dictionary<string, string> Japanese = new(StringComparer.Ordinal)
    {
        // Page chrome
        ["Skip to analysis"] = "分析へ移動",
        ["ghpmv report"] = "ghpmv レポート",
        ["Report sections"] = "レポートのセクション",
        ["Sections"] = "セクション",
        ["Filters"] = "フィルター",
        ["Source JSON"] = "元の JSON",
        ["Search all detail rows"] = "すべての詳細行を検索",
        ["Name, message, ID or evidence"] = "名前、メッセージ、ID、根拠",
        ["Show"] = "表示",
        ["All states"] = "すべての状態",
        ["Needs attention (errors, warnings, unknown)"] = "要対応（エラー・警告・不明）",
        ["Errors"] = "エラー",
        ["Warnings / incomplete"] = "警告 / 未完了",
        ["Unknown"] = "不明",
        ["Applied / match"] = "適用済み / 一致",
        ["Information"] = "情報",
        ["Reset filters"] = "フィルターをリセット",
        ["Filters apply to every detail table and expanded evidence. The summary above always describes the complete file."] =
            "フィルターはすべての詳細表と展開した根拠に適用されます。概要は常にファイル全体を対象とします。",
        ["No rows match these filters. Change the search or reset filters."] = "条件に一致する行はありません。検索条件を変更するか、フィルターをリセットしてください。",
        ["The complete input is included for inspection. It may contain business data; review before sharing. Referenced files are not loaded."] =
            "確認用に入力全体を含めています。業務データが含まれる場合があるため、共有前に確認してください。参照先のファイルは読み込みません。",
        ["Inspect the complete source JSON"] = "元の JSON 全体を表示",
        ["ghpmv · This report does not modify the target or resume logs. No external resources or API calls."] =
            "ghpmv · このレポートは移行先や再開ログを変更しません。外部リソースや API 呼び出しは使用しません。",
        ["Stack trace"] = "スタックトレース",
        ["Recorded evidence"] = "記録された根拠",
        ["{0} of {1} rows shown"] = "{1} 件中 {0} 件を表示",
        ["{0} of {1} detail rows shown"] = "詳細行 {1} 件中 {0} 件を表示",
        ["{0} of {1}"] = "{1} 件中 {0} 件",
        ["Source: {0}"] = "入力: {0}",
        ["Offline report"] = "オフライン レポート",
        ["Recorded evidence only"] = "記録された根拠のみ",
        ["Before you proceed"] = "続行する前に",
        ["Error"] = "エラー",
        ["Warning"] = "警告",
        ["Info"] = "情報",
        ["OK"] = "正常",
        ["Action required"] = "要対応",
        ["Needs review"] = "要確認",
        ["No issues recorded"] = "問題の記録なし",
        ["Insufficient evidence"] = "根拠不足",
        ["Yes"] = "はい",
        ["No"] = "いいえ",
        ["Not recorded"] = "記録なし",
        ["Applied"] = "適用済み",
        ["Not applied"] = "未適用",
        ["Source key: {0}"] = "ソースキー: {0}",
        ["Operation: {0}"] = "操作: {0}",

        // Verification
        ["Verification report"] = "検証レポート",
        ["Compare migration fidelity. Distinguish confirmed differences from verification gaps."] =
            "移行結果の忠実度を確認します。確認済みの差異と、検証できていない範囲を区別します。",
        ["Verification is incomplete and fails the CLI check"] = "検証が完了しておらず、CLI の検証チェックは失敗します",
        ["Migration differences need attention"] = "対応が必要な移行差異があります",
        ["There is not enough evidence to confirm a match"] = "一致を確認するための根拠が不足しています",
        ["Verification has warnings or coverage gaps"] = "警告または検証できていない範囲があります",
        ["The reported categories match"] = "記録されたカテゴリはすべて一致しています",
        ["Errors: {0}. Warnings: {1}. Categories with partial or missing verification: {2}."] =
            "エラー: {0} 件。警告: {1} 件。部分一致または未検証のカテゴリ: {2} 件。",
        ["This result applies only to the categories in this file; it does not establish that every migration stage succeeded."] =
            "この結果はファイルに含まれるカテゴリのみが対象で、すべての移行段階が成功したことを示すものではありません。",
        ["The CLI verification check fails: errors or not-verified categories ({0}) cause a nonzero exit even without --fail-on-warning."] =
            "CLI の検証チェックは失敗します。エラーまたは未検証カテゴリ（未検証 {0} 件）があると、--fail-on-warning を指定しなくても終了コードが 0 以外になります。",
        ["Warnings cause a nonzero CLI exit when --fail-on-warning is enabled."] =
            "--fail-on-warning を指定した場合、警告によって CLI の終了コードが 0 以外になります。",
        ["Inspect error differences first and compare the affected source and target data before making changes."] =
            "まずエラーの差異を確認し、変更を加える前に該当する移行元と移行先のデータを比較してください。",
        ["Review the category coverage before treating this verification as complete."] =
            "この検証を完了とみなす前に、カテゴリ別の検証範囲を確認してください。",
        ["Review warnings and unverified categories separately. A missing check is not a successful check."] =
            "警告と未検証カテゴリは個別に確認してください。検証されていないことは、検証に成功したことを意味しません。",
        ["Categories checked"] = "検証カテゴリ",
        ["Warnings"] = "警告",
        ["Categories matched or not applicable"] = "一致または対象外のカテゴリ",
        ["Differences to review"] = "確認が必要な差異",
        ["Errors first, then warnings and information. Messages are the verifier's recorded evidence, not inferred causes."] =
            "エラー、警告、情報の順に表示します。メッセージは検証時に記録された根拠であり、推定された原因ではありません。",
        ["Severity"] = "重要度",
        ["Category"] = "カテゴリ",
        ["Recorded difference"] = "記録された差異",
        ["No differences were recorded. Check category coverage below."] = "差異は記録されていません。カテゴリ別の検証範囲を確認してください。",
        ["Category coverage"] = "カテゴリ別の検証範囲",
        ["Partial match and not verified are limitations, not passes. Not applicable is excluded from the match judgment."] =
            "部分一致と未検証は制約事項であり、合格ではありません。対象外は一致判定から除外されます。",
        ["Outcome"] = "結果",
        ["Partial match"] = "部分一致",
        ["Not verified"] = "未検証",
        ["Not applicable"] = "対象外",
        ["Match"] = "一致",
        ["Mismatch"] = "不一致",
        ["No category coverage was recorded."] = "カテゴリ別の検証範囲は記録されていません。",

        // Import failure
        ["Import failure report"] = "インポート失敗レポート",
        ["Understand the failure, its recorded context and the checks needed before resuming."] =
            "失敗内容と記録されたコンテキスト、再開前に必要な確認事項を把握します。",
        ["unknown stage"] = "不明な段階",
        ["Import stopped while {0}"] = "{0}中にインポートが停止しました",
        ["Import stopped while {0}; resume-log persistence failed"] = "{0}中にインポートが停止し、再開ログの保存にも失敗しました",
        ["Recorded error: {0}"] = "記録されたエラー: {0}",
        ["What this stage does: {0}"] = "この段階の内容: {0}",
        ["at an unrecorded stage"] = "記録されていない段階の処理",
        ["at stage \"{0}\""] = "段階「{0}」の処理",
        ["The failure stage was not recorded, so it is unknown how far the import progressed."] = "失敗した段階が記録されていないため、インポートがどこまで進んだかは分かりません。",
        ["This report does not recognize the stage name \"{0}\"; it may come from a different ghpmv version."] = "段階名「{0}」はこのレポートでは認識できません。別のバージョンの ghpmv で記録された可能性があります。",
        ["{0} ({1})"] = "{0}（{1}）",
        ["{0}. {1}"] = "{0}. {1}",
        ["Where the import stopped"] = "インポートが停止した位置",
        ["ghpmv imports in this order. Steps before the failure finished in this run (or were not needed); later steps did not run. Some steps run only when the snapshot or options require them."] =
            "ghpmv は次の順でインポートします。停止した段階より前は今回の実行で完了済み（または不要）で、後の段階は実行されていません。一部の段階はスナップショットやオプションで必要な場合にだけ実行されます。",
        ["Step"] = "段階",
        ["Result in this run"] = "今回の結果",
        ["What this step does"] = "この段階で行うこと",
        ["Finished or not needed"] = "完了または不要",
        ["Stopped here"] = "ここで停止",
        ["Not started"] = "未実行",
        ["No import steps are defined."] = "インポートの段階が定義されていません。",
        ["Starting the import"] = "インポートの開始",
        ["starting the import"] = "インポートの開始",
        ["Reading command options and credentials. Nothing has been read from the snapshot or written to the target yet."] =
            "コマンドのオプションと認証情報を読み込んでいます。スナップショットはまだ読まれておらず、移行先への書き込みもまだありません。",
        ["Reading the exported snapshot"] = "エクスポートしたスナップショットの読み込み",
        ["reading the exported snapshot"] = "エクスポートしたスナップショットの読み込み",
        ["Loading snapshot.json from the input directory. Nothing has been written to the target yet."] =
            "入力ディレクトリの snapshot.json を読み込んでいます。移行先への書き込みはまだありません。",
        ["Checking permissions and prerequisites"] = "権限と前提条件の確認",
        ["checking permissions and prerequisites"] = "権限と前提条件の確認",
        ["Checking the token, repository and user mappings, organization permissions and, when enabled, the browser login. This run has not written to the target yet; an earlier interrupted run may have."] =
            "トークン、リポジトリ・ユーザーの対応表、Organization の権限、（有効な場合は）ブラウザーのログインを確認しています。今回の実行ではまだ移行先に書き込んでいませんが、以前に中断した実行が書き込んでいる可能性はあります。",
        ["Creating or updating the project and its settings"] = "プロジェクト本体と設定の作成・更新",
        ["creating or updating the project and its settings"] = "プロジェクト本体と設定の作成・更新",
        ["Creating the target project (or updating an existing one) and applying its title, description, fields and options, collaborators, linked repositories and views. Items have not been imported yet."] =
            "移行先プロジェクトを作成（既存なら更新）し、タイトル、説明、フィールドと選択肢、コラボレーター、リンクされたリポジトリ、ビューを反映しています。アイテムはまだ移行されていません。",
        ["Importing items (issues, pull requests and draft issues)"] = "アイテム（Issue・プルリクエスト・ドラフト Issue）の移行",
        ["importing items (issues, pull requests and draft issues)"] = "アイテム（Issue・プルリクエスト・ドラフト Issue）の移行",
        ["The project and its fields already exist. Adding issues and pull requests, creating draft issues, then applying field values, positions and archived state. Each finished item is recorded in import-log.json, so a rerun resumes from there."] =
            "プロジェクトとフィールドは作成済みです。Issue とプルリクエストを追加し、ドラフト Issue を作成して、フィールド値・並び順・アーカイブ状態を反映しています。完了したアイテムは import-log.json に記録されるため、再実行するとそこから再開します。",
        ["Importing status updates"] = "ステータス更新の移行",
        ["importing status updates"] = "ステータス更新の移行",
        ["Project settings and items are done. Copying the project's status updates."] =
            "プロジェクトの設定とアイテムは完了しています。プロジェクトのステータス更新をコピーしています。",
        ["Applying browser-only settings (views and workflows)"] = "ブラウザーでしか設定できない項目（ビュー・ワークフロー）の反映",
        ["applying browser-only settings (views and workflows)"] = "ブラウザーでしか設定できない項目（ビュー・ワークフロー）の反映",
        ["The API import is done. Using browser automation to replay settings the public API cannot write, such as View details and Workflows."] =
            "API による移行は完了しています。公開 API では書き込めないビューの詳細やワークフローを、ブラウザー自動操作で再現しています。",
        ["Finalizing the template setting"] = "テンプレート設定の最終反映",
        ["finalizing the template setting"] = "テンプレート設定の最終反映",
        ["All content is imported. Setting whether the target project is marked as a template, as the last step."] =
            "すべての内容は移行済みです。最後の手順として、移行先プロジェクトをテンプレートにするかどうかを設定しています。",
        ["Restoring the template setting after the failure"] = "失敗後のテンプレート設定の復元",
        ["After the import ended, ghpmv tried to restore the target project's template setting, which is temporarily changed during import."] =
            "インポート終了後、移行中に一時的に変更した移行先プロジェクトのテンプレート設定を元に戻そうとしました。",
        ["Closing the browser session"] = "ブラウザーセッションの終了",
        ["After the import ended, ghpmv tried to close the automated browser."] = "インポート終了後、自動操作していたブラウザーを閉じようとしました。",
        ["Closing the sensitive API diagnostics file"] = "機密 API 診断ファイルのクローズ",
        ["After the import ended, ghpmv tried to close the raw API diagnostics file enabled by --allow-sensitive-diagnostics."] =
            "インポート終了後、--allow-sensitive-diagnostics で有効にした API 診断ファイルを閉じようとしました。",
        ["Creating the log directory"] = "ログ用ディレクトリの作成",
        ["Opening the temporary file"] = "一時ファイルのオープン",
        ["Writing the temporary file"] = "一時ファイルへの書き込み",
        ["Flushing the temporary file to disk"] = "一時ファイルのディスクへの書き出し",
        ["Replacing the log with the new version"] = "ログの新しい版への置き換え",
        ["Moving the new log into place"] = "新しいログの配置",
        ["Report language"] = "レポートの言語",
        ["The failure message was not recorded."] = "失敗メッセージは記録されていません。",
        ["Multiple related failures were recorded; no concrete nested failure message is available."] =
            "関連する複数の失敗が記録されていますが、具体的な入れ子の失敗メッセージはありません。",
        ["No exception detail was recorded. The failure cause cannot be established from this file."] =
            "例外の詳細は記録されていません。このファイルから失敗原因は特定できません。",
        ["First concrete failure in recorded order: {0}"] = "記録順で最初の具体的な失敗: {0}",
        ["Cleanup also failed ({0} records): {1}"] = "クリーンアップも失敗しました（{0} 件）: {1}",
        ["Unknown stage"] = "不明な段階",
        ["No message recorded"] = "メッセージなし",
        ["The resume log may be stale or incomplete: a target write can succeed without being saved locally. Inspect the target and reconcile actual state before any retry to avoid duplicate writes."] =
            "再開ログが古いか不完全な可能性があります。移行先への書き込みが成功していても、ローカルに保存されていない場合があります。重複書き込みを避けるため、再試行の前に移行先を確認し、実際の状態と照合してください。",
        ["Resume-log persistence failures"] = "再開ログの保存失敗",
        ["These paths describe the failed save operation, not which process held a lock. Check storage access and saved target state before resuming."] =
            "これらのパスは失敗した保存操作を示すもので、ロックを保持していたプロセスを示すものではありません。再開前にストレージへのアクセスと、移行先の保存状態を確認してください。",
        ["Log path"] = "ログのパス",
        ["Save stage"] = "保存段階",
        ["Temporary / backup path"] = "一時 / バックアップのパス",
        ["Executing process"] = "実行プロセス",
        ["No persistence failures recorded."] = "保存失敗は記録されていません。",
        ["Read the failure evidence and its recorded recovery hints. Do not assume the first exception is the root cause."] =
            "失敗の根拠と記録された復旧ヒントを確認してください。最初の例外が根本原因とは限りません。",
        ["Inspect cleanup failures before resuming; restoration or cleanup may not have completed."] =
            "再開前にクリーンアップの失敗を確認してください。復元やクリーンアップが完了していない可能性があります。",
        ["Preserve the snapshot and resume logs. Inspect the target before repeating any operation with an uncertain result."] =
            "スナップショットと再開ログは保持してください。結果が不確かな操作を繰り返す前に、移行先を確認してください。",
        ["Failed stage"] = "失敗した段階",
        ["Exception records"] = "例外レコード",
        ["Cleanup failures"] = "クリーンアップ失敗",
        ["Recorded time span"] = "記録された期間",
        ["Failure evidence"] = "失敗の根拠",
        ["Outer and nested exceptions retain their depth. Expand evidence for context, attempt IDs, API details and stack traces."] =
            "外側と入れ子の例外を深さ付きで表示します。コンテキスト、試行 ID、API の詳細、スタックトレースは根拠を展開して確認できます。",
        ["Exception / depth"] = "例外 / 深さ",
        ["Recorded message"] = "記録されたメッセージ",
        ["Operation / response"] = "操作 / 応答",
        ["Recovery hint"] = "復旧ヒント",
        ["{0} · depth {1}"] = "{0} · 深さ {1}",
        ["Unknown exception"] = "不明な例外",
        ["Request {0}"] = "リクエスト {0}",
        ["No recovery hint recorded"] = "復旧ヒントは記録されていません",
        ["No exception details were recorded."] = "例外の詳細は記録されていません。",
        ["Recorded failure patterns"] = "記録された失敗パターン",
        ["Exception records grouped by type and HTTP status. Nested exceptions can describe the same failure; these are not counts of failed requests."] =
            "例外レコードを種類と HTTP ステータスで集計します。入れ子の例外は同じ失敗を表す場合があるため、失敗したリクエスト数ではありません。",
        ["Failure type"] = "失敗の種類",
        ["HTTP status"] = "HTTP ステータス",
        ["No exception patterns can be calculated."] = "例外パターンを集計できません。",
        ["Cleanup and restoration"] = "クリーンアップと復元",
        ["These failures are separate from the primary import failure and can require independent attention."] =
            "これらは主なインポート失敗とは別の失敗で、個別の対応が必要になる場合があります。",
        ["Stage"] = "段階",
        ["No cleanup failures were recorded."] = "クリーンアップの失敗は記録されていません。",
        ["Progress timeline"] = "進行状況の時系列",
        ["Recorded order is preserved. The final message precedes or accompanies the failure; it is not proof of its cause. Times are UTC."] =
            "記録順のまま表示します。最後のメッセージは失敗の直前または同時のものであり、原因の証明ではありません。時刻は UTC です。",
        ["Time (UTC)"] = "時刻 (UTC)",
        ["Recorded progress"] = "記録された進行状況",
        ["No progress timeline was recorded."] = "進行状況の時系列は記録されていません。",

        // Item resume log
        ["Item import resume log"] = "アイテム インポート再開ログ",
        ["Inspect durable item progress. This is a checkpoint, not a complete inventory of the source project."] =
            "保存済みのアイテム進行状況を確認します。これはチェックポイントであり、移行元プロジェクトの完全な一覧ではありません。",
        ["Unconfirmed operations need reconciliation"] = "未確認の操作を照合する必要があります",
        ["Template restoration remains required"] = "テンプレート設定の復元が未完了です",
        ["Some recorded item work needs attention"] = "対応が必要なアイテム処理があります",
        ["No detailed item progress was recorded"] = "アイテムの詳細な進行状況は記録されていません",
        ["Recorded item stages are complete"] = "記録されたアイテムの全段階が完了しています",
        ["{0} of {1} tracked items have all three stages applied."] = "追跡中の {1} 件のうち {0} 件で 3 段階すべてが適用済みです。",
        ["Items with recorded errors: {0}."] = "エラーが記録されたアイテム: {0} 件。",
        ["Unconfirmed operations: {0}."] = "未確認の操作: {0} 件。",
        ["Unlisted source items and other migration stages cannot be judged from this log."] =
            "記載されていない移行元アイテムや他の移行段階は、このログからは判断できません。",
        ["Reconcile pending operations against the target before any retry. Pending does not mean that creation failed; repeating it can create duplicates."] =
            "再試行の前に、保留中の操作を移行先と照合してください。保留中は作成失敗を意味しません。繰り返すと重複が作成される可能性があります。",
        ["Inspect the target template setting and complete the recorded restoration through the normal resume flow."] =
            "移行先のテンプレート設定を確認し、通常の再開手順で記録された復元を完了してください。",
        ["Inspect per-item errors and unapplied stages, then resume with the same snapshot and target log after resolving the cause."] =
            "アイテムごとのエラーと未適用の段階を確認し、原因を解消してから同じスナップショットと移行先ログで再開してください。",
        ["Use project completion and verification reports to assess the whole migration. Do not delete this resume log."] =
            "移行全体の評価には、プロジェクトの完了状態と検証レポートを使用してください。この再開ログは削除しないでください。",
        ["Schema version is not recorded. This is a legacy or unversioned checkpoint; report readability does not establish that it can be resumed by the current CLI."] =
            "スキーマ バージョンが記録されていません。旧形式またはバージョンなしのチェックポイントです。レポートとして読めても、現在の CLI で再開できるとは限りません。",
        ["Items with detailed state"] = "詳細状態のあるアイテム",
        ["Fully applied"] = "全段階適用済み",
        ["Incomplete"] = "未完了",
        ["Items with errors"] = "エラーのあるアイテム",
        ["Unconfirmed operations"] = "未確認の操作",
        ["Items with all stages applied"] = "全段階が適用済みのアイテム",
        ["Remaining work by stage"] = "段階別の残作業",
        ["Counts are independent: one item can appear in several stages. Archive means the archive-state step was applied, not that every item is archived."] =
            "各段階の件数は独立しており、1 件のアイテムが複数の段階に含まれる場合があります。アーカイブは状態の適用手順が実行されたことを示し、すべてのアイテムがアーカイブされたことではありません。",
        ["Recorded errors"] = "記録されたエラー",
        ["Field values"] = "フィールド値",
        ["Position"] = "位置",
        ["Archive"] = "アーカイブ",
        ["No item stages were recorded."] = "アイテムの段階は記録されていません。",
        ["Item progress"] = "アイテム別の進行状況",
        ["Content identity is used when present; source keys and target IDs remain available in expanded evidence. Items needing attention appear first."] =
            "コンテンツ識別子がある場合はそれを表示します。ソースキーと移行先 ID は根拠を展開して確認できます。対応が必要なアイテムを先に表示します。",
        ["Item / content identity"] = "アイテム / コンテンツ識別子",
        ["Archive state"] = "アーカイブ状態",
        ["No detailed item state was recorded."] = "アイテムの詳細状態は記録されていません。",
        ["No field identity recorded"] = "フィールドの識別情報は記録されていません",
        ["Failure context only; the specific reason was not recorded here."] = "失敗のコンテキストのみです。具体的な理由はここには記録されていません。",
        ["Field-value failure context"] = "フィールド値の失敗コンテキスト",
        ["Each context identifies a failed field operation. These records do not contain detailed failure reasons; consult the import warnings for those."] =
            "各コンテキストは失敗したフィールド操作を示します。詳細な失敗理由は含まれていないため、インポート時の警告を確認してください。",
        ["Item"] = "アイテム",
        ["Affected field / operation"] = "対象フィールド / 操作",
        ["Evidence limitation"] = "根拠の制約",
        ["No individual field-value failure contexts were recorded."] = "個別のフィールド値の失敗コンテキストは記録されていません。",
        ["Created item mappings"] = "作成済みアイテムの対応",
        ["These mappings record target IDs, not proof that all import stages completed."] =
            "これらは移行先 ID の対応記録であり、すべてのインポート段階が完了した証明ではありません。",
        ["Source position"] = "移行元の位置",
        ["Target item ID"] = "移行先アイテム ID",
        ["No mappings recorded."] = "対応は記録されていません。",
        ["Field-value failure contexts: {0}"] = "フィールド値の失敗コンテキスト: {0} 件",
        ["Unknown field / operation"] = "不明なフィールド / 操作",
        ["Draft"] = "ドラフト",
        ["Issue / pull request"] = "Issue / Pull Request",
        ["Status update"] = "ステータス更新",

        // Project resume log
        ["Project import resume log"] = "プロジェクト インポート再開ログ",
        ["Inspect project-level completion and unconfirmed writes before continuing a migration."] =
            "移行を続行する前に、プロジェクト単位の完了状態と未確認の書き込みを確認します。",
        ["Project field"] = "プロジェクト フィールド",
        ["Issue field"] = "Issue フィールド",
        ["Issue field link"] = "Issue フィールドのリンク",
        ["View"] = "ビュー",
        ["Project creation"] = "プロジェクト作成",
        ["Project deletion"] = "プロジェクト削除",
        ["Unknown title"] = "不明なタイトル",
        ["Unknown owner"] = "不明なオーナー",
        ["Deletion not confirmed"] = "削除は未確認",
        ["Unconfirmed project operations need reconciliation"] = "未確認のプロジェクト操作を照合する必要があります",
        ["The log records project import completion"] = "プロジェクトのインポート完了が記録されています",
        ["Project import is not marked complete"] = "プロジェクトのインポートは完了として記録されていません",
        ["Project completion cannot be confirmed"] = "プロジェクトの完了を確認できません",
        ["Completion: {0}. Unresolved warnings: {1}."] = "完了: {0}。未解決の警告: {1}。",
        ["{0} operations remain unconfirmed."] = "未確認の操作が {0} 件あります。",
        ["A completion marker is not a source-to-target verification result."] = "完了マーカーは、移行元と移行先の検証結果ではありません。",
        ["Inspect pending operations and their target evidence before retrying. A pending write may already have succeeded; do not clear the log or blindly recreate its target."] =
            "再試行の前に、保留中の操作と移行先の根拠を確認してください。保留中の書き込みはすでに成功している可能性があります。ログを消去したり、対象をむやみに再作成したりしないでください。",
        ["Review the import's warnings and related failure report. This log flags warnings but does not contain their full explanations."] =
            "インポート時の警告と関連する失敗レポートを確認してください。このログは警告の有無を示しますが、詳しい説明は含みません。",
        ["Warning state was not recorded. Do not interpret an absent value as 'no warnings'."] =
            "警告の状態が記録されていません。値がないことを「警告なし」と解釈しないでください。",
        ["Keep this log for the normal resume flow. Verify the destination separately after migration."] =
            "通常の再開手順のため、このログは保持してください。移行後は移行先を別途検証してください。",
        ["Import completed"] = "インポート完了",
        ["Unresolved warnings"] = "未解決の警告",
        ["Created fields"] = "作成済みフィールド",
        ["Source field names and their saved target IDs. Creation does not imply that field values were applied to every item."] =
            "移行元のフィールド名と保存された移行先 ID です。作成済みでも、すべてのアイテムにフィールド値が適用されたとは限りません。",
        ["Source field"] = "移行元フィールド",
        ["Target field ID"] = "移行先フィールド ID",
        ["No created field mappings were recorded."] = "作成済みフィールドの対応は記録されていません。",
        ["Pending operations (unreconciled)"] = "保留中の操作（未照合）",
        ["These records mean the final outcome has not been confirmed in this log, not that the operation failed. Expand evidence for baseline IDs and reconciliation details."] =
            "これらは最終結果がこのログで確認されていないことを示し、操作が失敗したことを意味しません。ベースライン ID や照合の詳細は根拠を展開して確認できます。",
        ["Operation kind"] = "操作の種類",
        ["Affected target"] = "対象",
        ["Configuration"] = "構成",
        ["Operation ID"] = "操作 ID",
        ["Reserved target: {0}"] = "予約済みの移行先: {0}",
        ["No pending operations were recorded."] = "保留中の操作は記録されていません。",

        // Context and property labels
        ["Run and checkpoint context"] = "実行とチェックポイントのコンテキスト",
        ["Recorded metadata only. Unknown means the value is absent or null."] = "記録されたメタデータのみです。「不明」は値がないか null であることを示します。",
        ["Property"] = "項目",
        ["Recorded value"] = "記録された値",
        ["No context recorded."] = "コンテキストは記録されていません。",
        ["Target owner"] = "移行先オーナー",
        ["Owner type"] = "オーナー種別",
        ["Target project number"] = "移行先プロジェクト番号",
        ["Target project url"] = "移行先プロジェクト URL",
        ["Occurred at utc"] = "発生日時 (UTC)",
        ["Run id"] = "実行 ID",
        ["Browser automation enabled"] = "ブラウザー自動化",
        ["Application"] = "アプリケーション",
        ["Context"] = "コンテキスト",
        ["Sensitive diagnostics state"] = "機密診断の状態",
        ["Sensitive diagnostics file"] = "機密診断ファイル",
        ["Schema version"] = "スキーマ バージョン",
        ["Project id"] = "プロジェクト ID",
        ["Source snapshot fingerprint"] = "移行元スナップショットのフィンガープリント",
        ["Template restoration required"] = "テンプレート復元が必要",
        ["Status updates"] = "ステータス更新",
        ["Created project id"] = "作成済みプロジェクト ID",
        ["Has unresolved warnings"] = "未解決の警告あり",
        ["Pending project deletion id"] = "保留中のプロジェクト削除 ID",
        ["Version"] = "バージョン",
        ["Source"] = "移行元",
        ["Target"] = "移行先",
        ["Element"] = "要素",
        ["Kind"] = "種類",
        ["Name"] = "名前",
        ["Title"] = "タイトル",
        ["Owner"] = "オーナー",
        ["Repository"] = "リポジトリ",
        ["Number"] = "番号",
        ["Target id"] = "移行先 ID",
    };
}
