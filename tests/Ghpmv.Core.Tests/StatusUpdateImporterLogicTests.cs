using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Tests;

/// <summary>
/// Pure-logic tests for <see cref="StatusUpdateImporter"/> (issue #46). The attribution
/// note built here is the contract shared with <c>ProjectVerifier.CompareStatusUpdates</c>:
/// GitHub's create API cannot preserve the original author or creation time, so the
/// importer prepends them to the body and the verifier expects exactly that shape.
/// </summary>
public class StatusUpdateImporterLogicTests
{
    private static StatusUpdateSnapshot Update(
        string body = "Original body.",
        string? creator = "octocat",
        string createdAt = "2026-01-05T09:00:00Z") => new()
        {
            Body = body,
            Status = "ON_TRACK",
            StartDate = "2026-01-01",
            TargetDate = "2026-04-15",
            Creator = creator,
            CreatedAt = createdAt,
            UpdatedAt = "2026-01-05T09:00:00Z",
        };

    [Fact]
    public void BuildImportedBody_prepends_creator_and_created_at()
    {
        var body = StatusUpdateImporter.BuildImportedBody(Update());

        Assert.Equal(
            "> _Originally created by @octocat on 2026-01-05T09:00:00Z._\n\nOriginal body.",
            body);
        Assert.StartsWith("> _Originally created by @octocat on 2026-01-05T09:00:00Z._\n\n", body, StringComparison.Ordinal);
        Assert.EndsWith("Original body.", body, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildImportedBody_without_creator_omits_the_mention()
    {
        var body = StatusUpdateImporter.BuildImportedBody(Update(creator: null));

        Assert.Equal(
            "> _Originally created on 2026-01-05T09:00:00Z._\n\nOriginal body.",
            body);
        Assert.DoesNotContain("@", body, StringComparison.Ordinal);

        // An empty creator string must behave exactly like a null creator.
        Assert.Equal(body, StatusUpdateImporter.BuildImportedBody(Update(creator: "")));
    }

    [Fact]
    public void BuildImportedBody_returns_only_the_note_when_body_is_empty()
    {
        var body = StatusUpdateImporter.BuildImportedBody(Update(body: ""));

        Assert.Equal("> _Originally created by @octocat on 2026-01-05T09:00:00Z._", body);
        Assert.DoesNotContain("\n", body, StringComparison.Ordinal);
        Assert.Equal(body.TrimEnd(), body);
    }

    [Fact]
    public void BuildImportedBody_preserves_multi_line_markdown_body()
    {
        const string Original = "## Heading\n\n- first bullet\n- second bullet\n\nClosing paragraph.";

        var body = StatusUpdateImporter.BuildImportedBody(Update(body: Original));

        var note = "> _Originally created by @octocat on 2026-01-05T09:00:00Z._";
        Assert.Equal(note + "\n\n" + Original, body);

        // The original body survives verbatim: no re-wrapping and no line-ending rewrite.
        Assert.Equal(Original, body[(note.Length + 2)..]);
        Assert.DoesNotContain("\r", body, StringComparison.Ordinal);
        Assert.Equal(8, body.Split('\n').Length);
    }

    [Fact]
    public void BuildImportedBody_is_stable_for_repeated_calls()
    {
        var update = Update();

        var first = StatusUpdateImporter.BuildImportedBody(update);
        var second = StatusUpdateImporter.BuildImportedBody(update);
        var fromEquivalentInstance = StatusUpdateImporter.BuildImportedBody(Update());

        // The verifier recomputes this value on every run, so it must be deterministic
        // and depend only on the snapshot contents.
        Assert.Equal(first, second);
        Assert.Equal(first, fromEquivalentInstance);
        Assert.Contains("Original body.", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_creates_status_updates_oldest_first()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-status-").FullName;
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var handler = new StatusUpdateHandler();
            using var client = CreateClient(handler);
            var snapshot = CreateSnapshot(
                SnapshotUpdate("Newest", createdAt: "2026-01-05T09:00:00Z"),
                SnapshotUpdate("Middle", createdAt: "2026-01-03T09:00:00Z"),
                SnapshotUpdate("Oldest", createdAt: "2026-01-01T09:00:00Z"));

            var result = await new StatusUpdateImporter(client).ImportAsync(
                snapshot,
                Target,
                directory,
                cancellationToken);

            Assert.Equal(3, result.Created);
            Assert.Equal(0, result.Resumed);
            Assert.Equal(0, result.AlreadyComplete);

            // The snapshot stores newest-first, but GitHub renders status updates in
            // creation order, so they must be replayed oldest-first.
            Assert.Equal(
                ["Oldest", "Middle", "Newest"],
                handler.CreateBodies.Select(body => body[(body.IndexOf("\n\n", StringComparison.Ordinal) + 2)..]));

            var log = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(log);

            // Log keys stay *snapshot* indices, so the oldest update owns the last key.
            Assert.Equal(["0", "1", "2"], log.StatusUpdates.Keys.Order(StringComparer.Ordinal));
            Assert.Equal("PVTSU_1", log.StatusUpdates["2"]);
            Assert.Equal("PVTSU_2", log.StatusUpdates["1"]);
            Assert.Equal("PVTSU_3", log.StatusUpdates["0"]);
            Assert.Empty(log.PendingStatusUpdates);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_sends_the_attributed_body_and_optional_dates()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-status-").FullName;
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var handler = new StatusUpdateHandler();
            using var client = CreateClient(handler);
            var dated = SnapshotUpdate(
                "Dated",
                status: "AT_RISK",
                createdAt: "2026-01-05T09:00:00Z",
                startDate: "2026-01-01",
                targetDate: "2026-04-15");
            var undated = SnapshotUpdate(
                "Undated",
                status: "OFF_TRACK",
                createdAt: "2026-01-02T09:00:00Z",
                startDate: null,
                targetDate: null);

            await new StatusUpdateImporter(client).ImportAsync(
                CreateSnapshot(dated, undated),
                Target,
                directory,
                cancellationToken);

            // Oldest first: the undated update was created earlier.
            Assert.Equal(StatusUpdateImporter.BuildImportedBody(undated), handler.CreateBodies[0]);
            Assert.Equal(StatusUpdateImporter.BuildImportedBody(dated), handler.CreateBodies[1]);
            Assert.Equal(["OFF_TRACK", "AT_RISK"], handler.CreateStatuses);
            Assert.Equal([null, "2026-01-01"], handler.CreateStartDates);
            Assert.Equal([null, "2026-04-15"], handler.CreateTargetDates);
            Assert.All(handler.CreateBodies, body =>
                Assert.StartsWith("> _Originally created by @octocat on ", body, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_does_nothing_when_the_snapshot_has_an_empty_status_update_list()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-status-").FullName;
        try
        {
            using var handler = new StatusUpdateHandler();
            using var client = CreateClient(handler);
            var progress = new List<string>();
            var importer = new StatusUpdateImporter(client) { OnProgress = progress.Add };

            var result = await importer.ImportAsync(
                CreateSnapshot(),
                Target,
                directory,
                TestContext.Current.CancellationToken);

            Assert.Empty(handler.RequestBodies);
            Assert.Equal(0, result.Created);
            Assert.Equal(0, result.Resumed);
            Assert.Equal(0, result.AlreadyComplete);

            // An empty list is "captured, but there is nothing to replay" — a different
            // state from a schema-v1 snapshot, so the summary is still emitted.
            Assert.Equal(
                ["Status update import finished: 0 created, 0 resumed, 0 already complete."],
                progress);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_rejects_unsupported_status_values()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-status-").FullName;
        try
        {
            using var handler = new StatusUpdateHandler();
            using var client = CreateClient(handler);
            var snapshot = CreateSnapshot(
                SnapshotUpdate("Fine", status: "ON_TRACK"),
                SnapshotUpdate("Broken", status: "BLOCKED", createdAt: "2026-01-02T09:00:00Z"));

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => new StatusUpdateImporter(client).ImportAsync(
                    snapshot,
                    Target,
                    directory,
                    TestContext.Current.CancellationToken));

            Assert.Equal(
                "Status update at snapshot sequence 1 has unsupported status 'BLOCKED'.",
                exception.Message);

            // Validation runs before any write, so the first (valid) update must not
            // have been created either — otherwise a retry would duplicate it.
            Assert.Empty(handler.RequestBodies);
            Assert.Equal(0, handler.CreateMutationCount);
            Assert.False(File.Exists(Path.Combine(directory, ImportLog.FileName)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("INACTIVE")]
    [InlineData("ON_TRACK")]
    [InlineData("AT_RISK")]
    [InlineData("OFF_TRACK")]
    [InlineData("COMPLETE")]
    public async Task Import_accepts_every_supported_status(string status)
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-status-").FullName;
        try
        {
            using var handler = new StatusUpdateHandler();
            using var client = CreateClient(handler);

            var result = await new StatusUpdateImporter(client).ImportAsync(
                CreateSnapshot(SnapshotUpdate("Body", status: status)),
                Target,
                directory,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, result.Created);

            // The status enum value is forwarded verbatim: no casing or aliasing.
            Assert.Equal(status, Assert.Single(handler.CreateStatuses));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_accepts_a_statusless_update()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-status-").FullName;
        try
        {
            using var handler = new StatusUpdateHandler();
            using var client = CreateClient(handler);

            var result = await new StatusUpdateImporter(client).ImportAsync(
                CreateSnapshot(SnapshotUpdate("Body without status", status: null)),
                Target,
                directory,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, result.Created);
            Assert.Null(Assert.Single(handler.CreateStatuses));
            Assert.Contains(
                "$status: ProjectV2StatusUpdateStatus,",
                Assert.Single(handler.RequestBodies, body =>
                    body.Contains("createProjectV2StatusUpdate", StringComparison.Ordinal)),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_rejects_invalid_created_at_values()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-status-").FullName;
        try
        {
            using var handler = new StatusUpdateHandler();
            using var client = CreateClient(handler);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => new StatusUpdateImporter(client).ImportAsync(
                    CreateSnapshot(SnapshotUpdate("Body", createdAt: "not-a-date")),
                    Target,
                    directory,
                    TestContext.Current.CancellationToken));

            Assert.Equal(
                "Status update at snapshot sequence 0 has invalid createdAt 'not-a-date'.",
                exception.Message);
            Assert.Empty(handler.RequestBodies);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("2026-1-01", null, "startDate")]
    [InlineData(null, "01/31/2026", "targetDate")]
    public async Task Import_rejects_invalid_dates_before_creating_any_updates(
        string? startDate,
        string? targetDate,
        string propertyName)
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-status-").FullName;
        try
        {
            using var handler = new StatusUpdateHandler();
            using var client = CreateClient(handler);
            var snapshot = CreateSnapshot(
                SnapshotUpdate("Valid oldest", createdAt: "2026-01-01T09:00:00Z"),
                SnapshotUpdate(
                    "Invalid later entry",
                    createdAt: "2026-01-02T09:00:00Z",
                    startDate: startDate,
                    targetDate: targetDate));

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => new StatusUpdateImporter(client).ImportAsync(
                    snapshot,
                    Target,
                    directory,
                    TestContext.Current.CancellationToken));

            Assert.Contains($"invalid {propertyName}", exception.Message, StringComparison.Ordinal);
            Assert.Empty(handler.RequestBodies);
            Assert.Equal(0, handler.CreateMutationCount);
            Assert.False(File.Exists(Path.Combine(directory, ImportLog.FileName)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_rejects_a_log_from_a_different_snapshot_or_target()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-status-").FullName;
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var handler = new StatusUpdateHandler();
            using var client = CreateClient(handler);
            await new ImportLog
            {
                ProjectId = "PVT_other_project",
                SourceSnapshotFingerprint = "0BADC0DE",
            }.SaveAsync(directory, cancellationToken);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new StatusUpdateImporter(client).ImportAsync(
                    CreateSnapshot(SnapshotUpdate("Body")),
                    Target,
                    directory,
                    cancellationToken));

            Assert.Contains(
                "belongs to a different source snapshot or target project. Use a separate log directory or restore the matching snapshot and target before resuming.",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Contains(directory, exception.Message, StringComparison.Ordinal);
            Assert.Empty(handler.RequestBodies);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_rejects_log_state_outside_the_snapshot_range()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-status-").FullName;
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var handler = new StatusUpdateHandler();
            using var client = CreateClient(handler);
            var snapshot = CreateSnapshot(
                SnapshotUpdate("A"),
                SnapshotUpdate("B", createdAt: "2026-01-02T09:00:00Z"),
                SnapshotUpdate("C", createdAt: "2026-01-03T09:00:00Z"));
            var log = new ImportLog
            {
                ProjectId = Target.ProjectId,
                SourceSnapshotFingerprint = ImportLog.ComputeSnapshotFingerprint(snapshot),
            };
            log.StatusUpdates["9"] = "PVTSU_stale";
            await log.SaveAsync(directory, cancellationToken);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new StatusUpdateImporter(client).ImportAsync(
                    snapshot,
                    Target,
                    directory,
                    cancellationToken));

            Assert.Equal(
                "import-log.json contains status update state that does not match the selected snapshot and target project.",
                exception.Message);
            Assert.Empty(handler.RequestBodies);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_throws_when_the_mutation_returns_an_empty_status_update_id()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-status-").FullName;
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var handler = new StatusUpdateHandler { ReturnEmptyId = true };
            using var client = CreateClient(handler);

            // An empty id is indistinguishable from "the result never arrived": the
            // client refuses to treat it as success, so the create stays ambiguous and
            // the pending record survives for explicit manual reconciliation.
            var exception = await Assert.ThrowsAsync<AmbiguousMutationResultException>(
                () => new StatusUpdateImporter(client).ImportAsync(
                    CreateSnapshot(SnapshotUpdate("Body")),
                    Target,
                    directory,
                    cancellationToken));

            Assert.Equal("createProjectV2StatusUpdate", exception.OperationName);
            Assert.Equal(1, handler.CreateMutationCount);

            var log = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(log);
            Assert.Empty(log.StatusUpdates);
            Assert.Equal(Target.ProjectId, Assert.Single(log.PendingStatusUpdates).Value.ProjectId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_reports_progress_for_each_stage_and_a_final_summary()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-status-").FullName;
        try
        {
            using var handler = new StatusUpdateHandler();
            using var client = CreateClient(handler);
            var progress = new List<string>();
            var importer = new StatusUpdateImporter(client) { OnProgress = progress.Add };

            await importer.ImportAsync(
                CreateSnapshot(
                    SnapshotUpdate("Newest", createdAt: "2026-01-05T09:00:00Z"),
                    SnapshotUpdate("Middle", createdAt: "2026-01-03T09:00:00Z"),
                    SnapshotUpdate("Oldest", createdAt: "2026-01-01T09:00:00Z")),
                Target,
                directory,
                TestContext.Current.CancellationToken);

            Assert.Equal(
                [
                    "[1/3] Creating status update at snapshot sequence 2...",
                    "[2/3] Creating status update at snapshot sequence 1...",
                    "[3/3] Creating status update at snapshot sequence 0...",
                    "Status update import finished: 3 created, 0 resumed, 0 already complete.",
                ],
                progress);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static StatusUpdateSnapshot SnapshotUpdate(
        string body,
        string? status = "ON_TRACK",
        string createdAt = "2026-01-01T09:00:00Z",
        string? startDate = "2026-01-01",
        string? targetDate = "2026-04-15",
        string? creator = "octocat") => new()
        {
            Body = body,
            Status = status,
            StartDate = startDate,
            TargetDate = targetDate,
            Creator = creator,
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
    /// Records every status-update request and answers the two operations the importer
    /// issues: the target-history id fetch and the create mutation.
    /// </summary>
    private sealed class StatusUpdateHandler : HttpMessageHandler
    {
        public bool ReturnEmptyId { get; init; }

        public List<string> RequestBodies { get; } = [];

        public List<string> CreateBodies { get; } = [];

        public List<string?> CreateStatuses { get; } = [];

        public List<string?> CreateStartDates { get; } = [];

        public List<string?> CreateTargetDates { get; } = [];

        public List<string?> ClientMutationIds { get; } = [];

        public int CreateMutationCount { get; private set; }

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
                return Json("""{"data":{"node":{"statusUpdates":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}""");
            }

            if (query.Contains("createProjectV2StatusUpdate", StringComparison.Ordinal))
            {
                CreateMutationCount++;
                CreateBodies.Add(variables.GetProperty("body").GetString() ?? string.Empty);
                CreateStatuses.Add(variables.GetProperty("status").GetString());
                CreateStartDates.Add(OptionalString(variables, "startDate"));
                CreateTargetDates.Add(OptionalString(variables, "targetDate"));
                ClientMutationIds.Add(variables.GetProperty("clientMutationId").GetString());
                var id = ReturnEmptyId
                    ? string.Empty
                    : "PVTSU_" + CreateMutationCount.ToString(CultureInfo.InvariantCulture);
                return Json(
                    "{\"data\":{\"createProjectV2StatusUpdate\":{\"statusUpdate\":{\"id\":\"" + id + "\"}}}}");
            }

            throw new InvalidOperationException($"Unexpected GraphQL operation: {query}");
        }

        private static string? OptionalString(JsonElement variables, string name)
            => variables.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
    }
}
