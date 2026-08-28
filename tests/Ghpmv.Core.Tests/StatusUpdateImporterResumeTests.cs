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
/// rely on a create idempotency key, so resume is driven by the target node ids persisted
/// in <c>import-log.json</c>. A pending record is written before the mutation leaves the
/// process, and an ambiguous result without a persisted target ID fails closed for manual
/// reconciliation rather than claiming a target update by mutable content.
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
            using var client = CreateClient(handler);

            await Assert.ThrowsAsync<AmbiguousMutationResultException>(
                () => new StatusUpdateImporter(client).ImportAsync(
                    CreateSnapshot(Update("Only")),
                    Target,
                    directory,
                    cancellationToken));

            // The log was already on disk when the create was observed, so a process
            // crash between send and response leaves actionable durable state.
            Assert.True(handler.PendingWasPresentAtMutation);

            var log = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(log);
            var pending = Assert.Single(log.PendingStatusUpdates);
            Assert.Equal("0", pending.Key);
            Assert.Equal(handler.ClientMutationId, pending.Value.OperationId);
            Assert.Equal(Target.ProjectId, pending.Value.ProjectId);
            Assert.Empty(log.StatusUpdates);
            Assert.Equal(0, handler.FetchCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Pending_create_with_an_exact_unique_content_match_fails_closed_without_querying_or_resending()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-status-resume-").FullName;
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var handler = new ResumeHandler(directory) { Ambiguous = true };
            using var client = CreateClient(handler);
            var snapshot = CreateSnapshot(Update("Line one\nLine two"));
            var importer = new StatusUpdateImporter(client);

            await Assert.ThrowsAsync<AmbiguousMutationResultException>(
                () => importer.ImportAsync(snapshot, Target, directory, cancellationToken));

            var pendingLog = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(pendingLog);
            var operationId = Assert.Single(pendingLog.PendingStatusUpdates).Value.OperationId;
            Assert.Equal(handler.ClientMutationId, operationId);

            var exception = await Assert.ThrowsAsync<StatusUpdateReconciliationRequiredException>(
                () => importer.ImportAsync(snapshot, Target, directory, cancellationToken));

            Assert.Equal(1, handler.CreateMutationCount);
            Assert.Equal(0, handler.FetchCount);
            Assert.Single(handler.AmbiguousTargetBodies);
            Assert.EndsWith(
                "Line one\nLine two",
                handler.AmbiguousTargetBodies[0],
                StringComparison.Ordinal);
            Assert.Equal(operationId, exception.OperationId);
            Assert.Equal(Target.ProjectId, exception.ProjectId);
            Assert.Equal(0, exception.SourceIndex);
            Assert.Equal(Path.Combine(directory, ImportLog.FileName), exception.ImportLogPath);
            Assert.Contains(
                "Automatic content-based reconciliation is disabled",
                exception.Message,
                StringComparison.Ordinal);

            var log = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(log);
            Assert.Empty(log.StatusUpdates);
            Assert.Equal(operationId, Assert.Single(log.PendingStatusUpdates).Value.OperationId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Pending_create_never_claims_one_of_concurrent_identical_updates()
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
            var snapshot = CreateSnapshot(Update("Identical concurrent body"));
            var importer = new StatusUpdateImporter(client);

            await Assert.ThrowsAsync<AmbiguousMutationResultException>(
                () => importer.ImportAsync(snapshot, Target, directory, cancellationToken));
            var operationId = handler.ClientMutationId;
            var exception = await Assert.ThrowsAsync<StatusUpdateReconciliationRequiredException>(
                () => importer.ImportAsync(snapshot, Target, directory, cancellationToken));

            Assert.Equal(1, handler.CreateMutationCount);
            Assert.Equal(0, handler.FetchCount);
            Assert.Equal(2, handler.AmbiguousTargetBodies.Count);
            Assert.All(
                handler.AmbiguousTargetBodies,
                body => Assert.EndsWith(
                    "Identical concurrent body",
                    body,
                    StringComparison.Ordinal));
            Assert.Equal(operationId, exception.OperationId);
            var log = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(log);
            Assert.Empty(log.StatusUpdates);
            Assert.Equal(operationId, Assert.Single(log.PendingStatusUpdates).Value.OperationId);
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
        LinkedRepositories = [],
        LinkedTeams = [],
        Project = new ProjectInfoSnapshot { Title = "Roadmap", Public = false, Closed = false, Template = false },
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
    /// Models the target project's status-update history and create mutation.
    /// <see cref="Ambiguous"/> simulates the side effect succeeding while the response is lost.
    /// </summary>
    private sealed class ResumeHandler(string logDirectory) : HttpMessageHandler
    {
        /// <summary>Makes the create mutation fail ambiguously (transport failure after send).</summary>
        public bool Ambiguous { get; set; }

        /// <summary>How many new ids the ambiguous create left behind on the target.</summary>
        public int AmbiguousCandidates { get; init; } = 1;

        /// <summary>Fails the create with a definitive, pre-side-effect GraphQL error.</summary>
        public bool FailDefinitively { get; init; }

        public List<string> TargetStatusUpdateIds { get; } = [];

        public List<string> AmbiguousTargetBodies { get; } = [];

        public List<string> RequestBodies { get; } = [];

        public List<string> MutationOperations { get; } = [];

        public List<string> CreateBodies { get; } = [];

        public int CreateMutationCount { get; private set; }

        public int FetchCount { get; private set; }

        public string? ClientMutationId { get; private set; }

        public bool PendingWasPresentAtMutation { get; private set; }

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
                return Json(
                    """{"data":{"node":{"statusUpdates":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}""");
            }

            if (query.Contains("createProjectV2StatusUpdate", StringComparison.Ordinal))
            {
                CreateMutationCount++;
                MutationOperations.Add("createProjectV2StatusUpdate");
                CreateBodies.Add(variables.GetProperty("body").GetString() ?? string.Empty);
                ClientMutationId = variables.GetProperty("clientMutationId").GetString();

                var log = await ImportLog.LoadAsync(logDirectory, cancellationToken);
                PendingWasPresentAtMutation = log?.PendingStatusUpdates.Count == 1;

                if (FailDefinitively)
                {
                    return Json("""{"data":null,"errors":[{"type":"BAD_USER_INPUT","message":"Invalid input"}]}""");
                }

                if (Ambiguous)
                {
                    for (var candidate = 1; candidate <= AmbiguousCandidates; candidate++)
                    {
                        TargetStatusUpdateIds.Add(
                            "PVTSU_ambiguous_" + candidate.ToString(CultureInfo.InvariantCulture));
                        AmbiguousTargetBodies.Add(
                            variables.GetProperty("body").GetString() ?? string.Empty);
                    }
                    throw new HttpRequestException("Response ended prematurely.");
                }

                var id = "PVTSU_created_" + CreateMutationCount.ToString(CultureInfo.InvariantCulture);
                TargetStatusUpdateIds.Add(id);
                return Json("{\"data\":{\"createProjectV2StatusUpdate\":{\"statusUpdate\":{\"id\":\"" + id + "\"}}}}");
            }

            throw new InvalidOperationException($"Unexpected GraphQL operation: {query}");
        }

        private static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
    }
}
