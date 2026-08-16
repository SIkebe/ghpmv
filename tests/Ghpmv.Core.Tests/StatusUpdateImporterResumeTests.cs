using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Tests;

/// <summary>
/// Crash-resume and reconciliation tests for <see cref="StatusUpdateImporter"/> (issue #46).
/// GitHub exposes no idempotency key for <c>createProjectV2StatusUpdate</c> and no way to
/// look an update up by content, so resume is entirely driven by the target node ids
/// persisted in <c>import-log.json</c>: a pending record is written *before* the mutation
/// leaves the process, and an ambiguous result is reconciled by diffing the target history
/// against the recorded baseline instead of re-sending the create.
/// </summary>
public class StatusUpdateImporterResumeTests
{
    [Fact]
    public async Task Pending_status_update_is_persisted_before_the_mutation_is_sent()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-status-resume-").FullName;
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var handler = new ResumeHandler(directory) { Ambiguous = true };
            handler.TargetStatusUpdateIds.Add("PVTSU_existing");
            using var client = CreateClient(handler);

            await Assert.ThrowsAsync<AmbiguousMutationResultException>(
                () => new StatusUpdateImporter(client).ImportAsync(
                    CreateSnapshot(Update("Only")),
                    Target,
                    directory,
                    cancellationToken));

            // The log was already on disk when the create was observed, so a process
            // crash between send and response is still resumable.
            Assert.True(handler.PendingWasPresentAtMutation);
            Assert.NotNull(handler.PendingBaselineAtMutation);
            Assert.Equal(["PVTSU_existing"], handler.PendingBaselineAtMutation);

            var log = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(log);
            var pending = Assert.Single(log.PendingStatusUpdates);
            Assert.Equal("0", pending.Key);
            Assert.Equal(handler.ClientMutationId, pending.Value.OperationId);
            Assert.Equal(Target.ProjectId, pending.Value.ProjectId);
            Assert.Equal(["PVTSU_existing"], pending.Value.ExistingStatusUpdateIds);
            Assert.Empty(log.StatusUpdates);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Ambiguous_create_survives_in_the_log_and_is_reconciled_by_target_id_without_resending()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-status-resume-").FullName;
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var handler = new ResumeHandler(directory) { Ambiguous = true };
            using var client = CreateClient(handler);
            var snapshot = CreateSnapshot(Update("Only"));
            var importer = new StatusUpdateImporter(client);
            var progress = new List<string>();

            await Assert.ThrowsAsync<AmbiguousMutationResultException>(
                () => importer.ImportAsync(snapshot, Target, directory, cancellationToken));

            var pendingLog = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(pendingLog);
            var operationId = Assert.Single(pendingLog.PendingStatusUpdates).Value.OperationId;
            Assert.Equal(handler.ClientMutationId, operationId);

            // The create did reach GitHub; the response did not reach us.
            handler.Resume = true;
            var resumingImporter = new StatusUpdateImporter(client) { OnProgress = progress.Add };
            var result = await resumingImporter.ImportAsync(snapshot, Target, directory, cancellationToken);

            Assert.Equal(1, handler.CreateMutationCount);
            Assert.Equal(0, result.Created);
            Assert.Equal(1, result.Resumed);
            Assert.Equal(0, result.AlreadyComplete);

            var log = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(log);
            Assert.Equal("PVTSU_ambiguous_1", log.StatusUpdates["0"]);
            Assert.Empty(log.PendingStatusUpdates);
            Assert.Contains(
                "[1/1] Reconciled status update at snapshot sequence 0 to target 'PVTSU_ambiguous_1'.",
                progress);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Reconciliation_rejects_multiple_new_candidates()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-status-resume-").FullName;
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var handler = new ResumeHandler(directory)
            {
                Ambiguous = true,
                AmbiguousCandidates = 2,
            };
            using var client = CreateClient(handler);
            var snapshot = CreateSnapshot(Update("Only"));
            var importer = new StatusUpdateImporter(client);

            await Assert.ThrowsAsync<AmbiguousMutationResultException>(
                () => importer.ImportAsync(snapshot, Target, directory, cancellationToken));
            var operationId = handler.ClientMutationId;
            handler.Resume = true;

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => importer.ImportAsync(snapshot, Target, directory, cancellationToken));

            Assert.Equal(
                $"Pending status update operation '{operationId}' matches multiple new target updates. Reconcile the target manually.",
                exception.Message);

            // Refusing beats guessing: nothing was created and nothing was mapped.
            Assert.Equal(1, handler.CreateMutationCount);
            var log = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(log);
            Assert.Empty(log.StatusUpdates);
            Assert.Single(log.PendingStatusUpdates);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Reconciliation_that_finds_nothing_refuses_to_create_a_possible_duplicate()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-status-resume-").FullName;
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var handler = new ResumeHandler(directory)
            {
                Ambiguous = true,
                AmbiguousCandidates = 0,
            };
            using var client = CreateClient(handler);
            var snapshot = CreateSnapshot(Update("Only"));
            var importer = new StatusUpdateImporter(client);

            await Assert.ThrowsAsync<AmbiguousMutationResultException>(
                () => importer.ImportAsync(snapshot, Target, directory, cancellationToken));
            var operationId = handler.ClientMutationId;
            var fetchesAfterFirstRun = handler.FetchCount;
            handler.Resume = true;

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => importer.ImportAsync(snapshot, Target, directory, cancellationToken));

            Assert.Equal(
                $"Pending status update operation '{operationId}' could not be reconciled by target id. Refusing to create a possible duplicate.",
                exception.Message);

            // Three fetch attempts, and crucially no second create: an unreconciled
            // pending record must never turn into a duplicate status update.
            Assert.Equal(3, handler.FetchCount - fetchesAfterFirstRun);
            Assert.Equal(1, handler.CreateMutationCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Reconciliation_baseline_excludes_ids_already_mapped_in_the_log()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-status-resume-").FullName;
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var handler = new ResumeHandler(directory)
            {
                HideCreatedIdsUntilResume = true,
                AmbiguousAtCreate = 2,
            };
            using var client = CreateClient(handler);
            var snapshot = CreateSnapshot(
                Update("Newest", createdAt: "2026-01-03T09:00:00Z"),
                Update("Oldest", createdAt: "2026-01-01T09:00:00Z"));
            var importer = new StatusUpdateImporter(client);

            await Assert.ThrowsAsync<AmbiguousMutationResultException>(
                () => importer.ImportAsync(snapshot, Target, directory, cancellationToken));

            var pendingLog = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(pendingLog);
            Assert.Equal("PVTSU_created_1", pendingLog.StatusUpdates["1"]);

            // The first created id is missing from the pending baseline (the target
            // history had not caught up yet), so only the union with the already-mapped
            // ids keeps it out of the candidate set.
            Assert.DoesNotContain(
                "PVTSU_created_1",
                Assert.Single(pendingLog.PendingStatusUpdates).Value.ExistingStatusUpdateIds);

            handler.Resume = true;
            var result = await importer.ImportAsync(snapshot, Target, directory, cancellationToken);

            Assert.Equal(0, result.Created);
            Assert.Equal(1, result.Resumed);
            Assert.Equal(1, result.AlreadyComplete);
            var log = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(log);
            Assert.Equal("PVTSU_created_1", log.StatusUpdates["1"]);
            Assert.Equal("PVTSU_ambiguous_1", log.StatusUpdates["0"]);
            Assert.Empty(log.PendingStatusUpdates);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Definitive_mutation_failure_clears_the_pending_operation()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-status-resume-").FullName;
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var handler = new ResumeHandler(directory) { FailDefinitively = true };
            using var client = CreateClient(handler);

            var exception = await Assert.ThrowsAsync<GitHubGraphQLException>(
                () => new StatusUpdateImporter(client).ImportAsync(
                    CreateSnapshot(Update("Only")),
                    Target,
                    directory,
                    cancellationToken));

            Assert.Equal("BAD_USER_INPUT", exception.ErrorType);
            Assert.Equal(1, handler.CreateMutationCount);

            // The create definitely did not happen, so leaving a pending record behind
            // would strand the next run in reconciliation forever.
            var log = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(log);
            Assert.Empty(log.PendingStatusUpdates);
            Assert.Empty(log.StatusUpdates);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Failure_before_the_mutation_clears_the_pending_operation()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-status-resume-").FullName;
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var handler = new ResumeHandler(directory) { FailFetchAt = 2 };
            using var client = CreateClient(handler);
            var snapshot = CreateSnapshot(
                Update("Newest", createdAt: "2026-01-03T09:00:00Z"),
                Update("Oldest", createdAt: "2026-01-01T09:00:00Z"));

            await Assert.ThrowsAsync<GitHubGraphQLException>(
                () => new StatusUpdateImporter(client).ImportAsync(
                    snapshot,
                    Target,
                    directory,
                    cancellationToken));

            // The baseline read for the second update failed before anything was sent:
            // the first update stays mapped and no pending record is left behind.
            Assert.Equal(1, handler.CreateMutationCount);
            var log = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(log);
            Assert.Equal("PVTSU_created_1", Assert.Single(log.StatusUpdates).Value);
            Assert.Empty(log.PendingStatusUpdates);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Second_run_with_the_log_reports_already_complete_without_sending_mutations()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-status-resume-").FullName;
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var handler = new ResumeHandler(directory);
            using var client = CreateClient(handler);
            var snapshot = CreateSnapshot(
                Update("Newest", createdAt: "2026-01-05T09:00:00Z"),
                Update("Middle", createdAt: "2026-01-03T09:00:00Z"),
                Update("Oldest", createdAt: "2026-01-01T09:00:00Z"));
            var progress = new List<string>();
            var importer = new StatusUpdateImporter(client);

            var first = await importer.ImportAsync(snapshot, Target, directory, cancellationToken);
            Assert.Equal(3, first.Created);
            var mutationsAfterFirstRun = handler.CreateMutationCount;

            var second = await new StatusUpdateImporter(client) { OnProgress = progress.Add }
                .ImportAsync(snapshot, Target, directory, cancellationToken);

            Assert.Equal(mutationsAfterFirstRun, handler.CreateMutationCount);
            Assert.Equal(0, second.Created);
            Assert.Equal(0, second.Resumed);
            Assert.Equal(3, second.AlreadyComplete);
            Assert.Equal(
                [
                    "[1/3] Status update at snapshot sequence 2: already complete.",
                    "[2/3] Status update at snapshot sequence 1: already complete.",
                    "[3/3] Status update at snapshot sequence 0: already complete.",
                    "Status update import finished: 0 created, 0 resumed, 3 already complete.",
                ],
                progress);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Second_run_without_the_log_creates_duplicates_because_there_is_no_content_dedupe()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-status-resume-").FullName;
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var handler = new ResumeHandler(directory);
            using var client = CreateClient(handler);
            var snapshot = CreateSnapshot(
                Update("Newest", createdAt: "2026-01-03T09:00:00Z"),
                Update("Oldest", createdAt: "2026-01-01T09:00:00Z"));
            var importer = new StatusUpdateImporter(client);

            var first = await importer.ImportAsync(snapshot, Target, directory, cancellationToken);
            Assert.Equal(2, first.Created);

            File.Delete(Path.Combine(directory, ImportLog.FileName));
            File.Delete(Path.Combine(directory, ImportLog.BackupFileName));

            var second = await importer.ImportAsync(snapshot, Target, directory, cancellationToken);

            // Resume is by persisted target id only. Bodies are never compared, so
            // losing the log means the identical history is created a second time.
            Assert.Equal(2, second.Created);
            Assert.Equal(0, second.Resumed);
            Assert.Equal(0, second.AlreadyComplete);
            Assert.Equal(4, handler.CreateMutationCount);
            Assert.Equal(4, handler.TargetStatusUpdateIds.Count);
            Assert.Equal(
                2,
                handler.CreateBodies.Count(body => body.EndsWith("Oldest", StringComparison.Ordinal)));

            var log = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(log);
            Assert.Equal(["PVTSU_created_3", "PVTSU_created_4"], log.StatusUpdates.Values.Order(StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Pending_operation_for_a_different_project_is_rejected()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-status-resume-").FullName;
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var handler = new ResumeHandler(directory);
            using var client = CreateClient(handler);
            var snapshot = CreateSnapshot(Update("Only"));
            var log = new ImportLog
            {
                ProjectId = Target.ProjectId,
                SourceSnapshotFingerprint = ImportLog.ComputeSnapshotFingerprint(snapshot),
            };
            log.PendingStatusUpdates["0"] = new PendingStatusUpdateOperation
            {
                OperationId = "operation-from-another-run",
                ProjectId = "PVT_other_project",
                ExistingStatusUpdateIds = [],
            };
            await log.SaveAsync(directory, cancellationToken);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new StatusUpdateImporter(client).ImportAsync(
                    snapshot,
                    Target,
                    directory,
                    cancellationToken));

            // The cross-project guard fires during log validation, before the loop is
            // ever entered, so no request is issued against the wrong project.
            Assert.Equal(
                "import-log.json contains status update state that does not match the selected snapshot and target project.",
                exception.Message);
            Assert.Empty(handler.RequestBodies);
            Assert.Equal(0, handler.CreateMutationCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Existing_target_status_updates_are_left_untouched()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-status-resume-").FullName;
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var handler = new ResumeHandler(directory);
            handler.TargetStatusUpdateIds.AddRange(["PVTSU_pre_existing_1", "PVTSU_pre_existing_2"]);
            using var client = CreateClient(handler);

            var result = await new StatusUpdateImporter(client).ImportAsync(
                CreateSnapshot(
                    Update("Newest", createdAt: "2026-01-03T09:00:00Z"),
                    Update("Oldest", createdAt: "2026-01-01T09:00:00Z")),
                Target,
                directory,
                cancellationToken);

            Assert.Equal(2, result.Created);
            Assert.Equal(
                ["createProjectV2StatusUpdate", "createProjectV2StatusUpdate"],
                handler.MutationOperations);
            Assert.DoesNotContain(
                handler.RequestBodies,
                body => body.Contains("updateProjectV2StatusUpdate", StringComparison.Ordinal)
                    || body.Contains("deleteProjectV2StatusUpdate", StringComparison.Ordinal));

            var log = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(log);
            Assert.Equal(["PVTSU_created_1", "PVTSU_created_2"], log.StatusUpdates.Values.Order(StringComparer.Ordinal));
            Assert.DoesNotContain("PVTSU_pre_existing_1", log.StatusUpdates.Values);
            Assert.DoesNotContain("PVTSU_pre_existing_2", log.StatusUpdates.Values);
            Assert.Equal(4, handler.TargetStatusUpdateIds.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Status_update_id_fetch_paginates_the_target_history()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-status-resume-").FullName;
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var handler = new ResumeHandler(directory) { PageSize = 1, Ambiguous = true };
            handler.TargetStatusUpdateIds.AddRange(["PVTSU_page_one", "PVTSU_page_two"]);
            using var client = CreateClient(handler);

            await Assert.ThrowsAsync<AmbiguousMutationResultException>(
                () => new StatusUpdateImporter(client).ImportAsync(
                    CreateSnapshot(Update("Only")),
                    Target,
                    directory,
                    cancellationToken));

            // A truncated baseline would make an already-known update look "new" and
            // let reconciliation adopt the wrong node.
            Assert.Equal(2, handler.FetchCount);
            Assert.NotNull(handler.PendingBaselineAtMutation);
            Assert.Equal(
                ["PVTSU_page_one", "PVTSU_page_two"],
                handler.PendingBaselineAtMutation);
            Assert.Contains(
                handler.RequestBodies,
                body => body.Contains("\"after\":\"cursor-1\"", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static StatusUpdateSnapshot Update(
        string body,
        string status = "ON_TRACK",
        string createdAt = "2026-01-01T09:00:00Z") => new()
        {
            Body = body,
            Status = status,
            StartDate = "2026-01-01",
            TargetDate = "2026-04-15",
            Creator = "octocat",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };

    private static ProjectSnapshot CreateSnapshot(params StatusUpdateSnapshot[] updates) => new()
    {
        SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
        Project = new ProjectInfoSnapshot { Title = "Roadmap", Public = false, Closed = false },
        Fields = [],
        Views = [],
        Workflows = [],
        Items = [],
        StatusUpdates = updates,
    };

    private static GitHubGraphQLClient CreateClient(HttpMessageHandler handler) =>
        new("token", baseUrl: null, handler, static (_, _) => Task.CompletedTask);

    private static readonly ImportResult Target = new()
    {
        ProjectId = "PVT_target",
        ProjectNumber = 42,
        Url = "https://github.com/orgs/target/projects/42",
        Outcome = ProjectImportOutcome.Updated,
        FieldIds = new Dictionary<string, string>(StringComparer.Ordinal),
        OptionIds = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal),
        IterationIds = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal),
    };

    /// <summary>
    /// Models the target project's status-update history: the id fetch reports
    /// <see cref="TargetStatusUpdateIds"/> (optionally paged), and the create mutation
    /// appends to it. <see cref="Ambiguous"/> simulates "the side effect happened but the
    /// response was lost" — the new id only becomes visible once <see cref="Resume"/> is set.
    /// </summary>
    private sealed class ResumeHandler(string logDirectory) : HttpMessageHandler
    {
        /// <summary>Makes the create mutation fail ambiguously (transport failure after send).</summary>
        public bool Ambiguous { get; set; }

        /// <summary>1-based create-mutation ordinal that fails ambiguously; 0 uses <see cref="Ambiguous"/>.</summary>
        public int AmbiguousAtCreate { get; init; }

        /// <summary>Reveals the ambiguous create's side effect, as a later run would see it.</summary>
        public bool Resume { get; set; }

        /// <summary>How many new ids the ambiguous create left behind on the target.</summary>
        public int AmbiguousCandidates { get; init; } = 1;

        /// <summary>Fails the create with a definitive, pre-side-effect GraphQL error.</summary>
        public bool FailDefinitively { get; init; }

        /// <summary>1-based id-fetch ordinal that fails; 0 never fails.</summary>
        public int FailFetchAt { get; init; }

        /// <summary>Withholds ids created in this process from the fetch until <see cref="Resume"/>.</summary>
        public bool HideCreatedIdsUntilResume { get; init; }

        /// <summary>Ids per page of the target-history fetch.</summary>
        public int PageSize { get; init; } = 100;

        public List<string> TargetStatusUpdateIds { get; } = [];

        public List<string> RequestBodies { get; } = [];

        public List<string> MutationOperations { get; } = [];

        public List<string> CreateBodies { get; } = [];

        public int CreateMutationCount { get; private set; }

        public int FetchCount { get; private set; }

        public string? ClientMutationId { get; private set; }

        public bool PendingWasPresentAtMutation { get; private set; }

        public string[]? PendingBaselineAtMutation { get; private set; }

        private readonly HashSet<string> _createdIds = new(StringComparer.Ordinal);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(body);
            using var document = JsonDocument.Parse(body);
            var query = document.RootElement.GetProperty("query").GetString() ?? string.Empty;
            var variables = document.RootElement.GetProperty("variables");

            if (query.Contains("statusUpdates(first: $first", StringComparison.Ordinal))
            {
                FetchCount++;
                if (FailFetchAt == FetchCount)
                {
                    return Json("""{"data":null,"errors":[{"type":"FORBIDDEN","message":"Baseline read failed"}]}""");
                }

                return Json(FetchPage(variables));
            }

            if (query.Contains("createProjectV2StatusUpdate", StringComparison.Ordinal))
            {
                CreateMutationCount++;
                MutationOperations.Add("createProjectV2StatusUpdate");
                CreateBodies.Add(variables.GetProperty("body").GetString() ?? string.Empty);
                ClientMutationId = variables.GetProperty("clientMutationId").GetString();

                var log = await ImportLog.LoadAsync(logDirectory, cancellationToken);
                PendingWasPresentAtMutation = log?.PendingStatusUpdates.Count == 1;
                PendingBaselineAtMutation = log?.PendingStatusUpdates.Values.FirstOrDefault()?.ExistingStatusUpdateIds;

                if (FailDefinitively)
                {
                    return Json("""{"data":null,"errors":[{"type":"BAD_USER_INPUT","message":"Invalid input"}]}""");
                }

                if (Ambiguous || AmbiguousAtCreate == CreateMutationCount)
                {
                    throw new HttpRequestException("Response ended prematurely.");
                }

                var id = "PVTSU_created_" + CreateMutationCount.ToString(CultureInfo.InvariantCulture);
                TargetStatusUpdateIds.Add(id);
                _createdIds.Add(id);
                return Json("{\"data\":{\"createProjectV2StatusUpdate\":{\"statusUpdate\":{\"id\":\"" + id + "\"}}}}");
            }

            throw new InvalidOperationException($"Unexpected GraphQL operation: {query}");
        }

        private string FetchPage(JsonElement variables)
        {
            var visible = TargetStatusUpdateIds
                .Where(id => Resume || !HideCreatedIdsUntilResume || !_createdIds.Contains(id))
                .ToList();
            if (Resume)
            {
                for (var candidate = 1; candidate <= AmbiguousCandidates; candidate++)
                {
                    visible.Add("PVTSU_ambiguous_" + candidate.ToString(CultureInfo.InvariantCulture));
                }
            }

            var after = variables.TryGetProperty("after", out var cursor) && cursor.ValueKind == JsonValueKind.String
                ? cursor.GetString()
                : null;
            var skip = after is null
                ? 0
                : int.Parse(after["cursor-".Length..], CultureInfo.InvariantCulture) * PageSize;
            var page = visible.Skip(skip).Take(PageSize).ToList();
            var hasNextPage = visible.Count > skip + page.Count;
            var pageIndex = (skip / PageSize) + 1;
            var nodes = string.Join(",", page.Select(id => "{\"id\":\"" + id + "\"}"));
            return "{\"data\":{\"node\":{\"statusUpdates\":{\"nodes\":[" + nodes +
                "],\"pageInfo\":{\"hasNextPage\":" + (hasNextPage ? "true" : "false") +
                ",\"endCursor\":\"cursor-" + pageIndex.ToString(CultureInfo.InvariantCulture) + "\"}}}}}";
        }

        private static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
    }
}
