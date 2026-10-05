using System.Net;
using System.Text;
using System.Text.Json;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Tests;

public sealed class IterationMergeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_and_operation_owned_fields_merge_and_refresh_target_ids(bool operationOwned)
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-iteration-merge-").FullName;
        try
        {
            if (operationOwned)
            {
                await new ProjectImportLog
                {
                    CreatedProjectId = "PVT_target",
                    CreatedFields = { ["Sprint"] = "PVTF_sprint" },
                }.SaveAsync(directory, TestContext.Current.CancellationToken);
            }

            using var handler = new IterationHandler(Target());
            using var client = Client(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };
            var result = await importer.ImportIntoAsync(Snapshot(Source()), "target", 7, TestContext.Current.CancellationToken);

            Assert.Empty(importer.Warnings);
            Assert.Equal("target-shared", result.IterationIds["Sprint"]["Shared"]);
            Assert.Equal("target-only", result.IterationIds["Sprint"]["Target only"]);
            Assert.Equal("new-1", result.IterationIds["Sprint"]["New"]);
            Assert.Equal("target-past", result.IterationIds["Sprint"]["Past"]);
            var input = Assert.Single(handler.UpdateInputs);
            Assert.Equal(14, input.GetProperty("duration").GetInt32());
            Assert.Equal("2026-08-03", input.GetProperty("startDate").GetString());
            var iterations = input.GetProperty("iterations").EnumerateArray().ToArray();
            Assert.Equal(["Past", "Target only", "Shared", "New"],
                iterations.Select(iteration => iteration.GetProperty("title").GetString()));
            Assert.Equal(["target-past", "target-only", "target-shared"],
                iterations.Take(3).Select(iteration => iteration.GetProperty("id").GetString()));
            Assert.False(iterations[3].TryGetProperty("id", out _));
            Assert.Equal("2026-08-04", iterations[0].GetProperty("startDate").GetString());
            Assert.Equal("2026-09-14", iterations[2].GetProperty("startDate").GetString());
            Assert.Equal(21, iterations[2].GetProperty("duration").GetInt32());
            Assert.DoesNotContain("source-", input.GetRawText(), StringComparison.Ordinal);

            await importer.ImportIntoAsync(Snapshot(Source()), "target", 7, TestContext.Current.CancellationToken);
            Assert.Single(handler.UpdateInputs);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Ambiguous_addition_is_not_retried_and_resumption_reuses_created_ids()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-iteration-resume-").FullName;
        try
        {
            using var handler = new IterationHandler(Target()) { LoseUpdateResponse = true };
            using var client = Client(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };
            await Assert.ThrowsAsync<AmbiguousMutationResultException>(() =>
                importer.ImportIntoAsync(Snapshot(Source()), "target", 7, TestContext.Current.CancellationToken));
            Assert.Single(handler.UpdateInputs);

            var resumed = await new ProjectImporter(client) { OperationLogDirectory = directory }
                .ImportIntoAsync(Snapshot(Source()), "target", 7, TestContext.Current.CancellationToken);
            Assert.Equal("new-1", resumed.IterationIds["Sprint"]["New"]);
            Assert.Single(handler.UpdateInputs);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uninitialized_source_never_clears_an_existing_schedule(bool targetUninitialized)
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-iteration-empty-").FullName;
        try
        {
            var empty = Configuration(0, 0, []);
            using var handler = new IterationHandler(targetUninitialized ? empty : Target());
            using var client = Client(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };
            await importer.ImportIntoAsync(Snapshot(empty), "target", 7, TestContext.Current.CancellationToken);
            Assert.Empty(handler.UpdateInputs);
            if (targetUninitialized)
            {
                Assert.Empty(importer.Warnings);
            }
            else
            {
                Assert.Contains("preserving it", Assert.Single(importer.Warnings), StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Update_with_only_existing_ids_can_retry_without_replacing_identities()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-iteration-retry-").FullName;
        try
        {
            using var handler = new IterationHandler(Target()) { LoseUpdateResponse = true };
            using var client = Client(handler);
            var result = await new ProjectImporter(client) { OperationLogDirectory = directory }
                .ImportIntoAsync(Snapshot(Configuration(14, 1, [])), "target", 7, TestContext.Current.CancellationToken);
            Assert.Equal(2, handler.UpdateInputs.Count);
            Assert.Equal(handler.UpdateInputs[0].GetRawText(), handler.UpdateInputs[1].GetRawText());
            Assert.Equal("target-shared", result.IterationIds["Sprint"]["Shared"]);
            Assert.Equal("target-only", result.IterationIds["Sprint"]["Target only"]);
            Assert.Equal("target-past", result.IterationIds["Sprint"]["Past"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Api_rejection_is_surfaced_without_retrying_or_reporting_a_success()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-iteration-rejection-").FullName;
        try
        {
            using var handler = new IterationHandler(Target()) { RejectUpdate = true };
            using var client = Client(handler);
            var exception = await Assert.ThrowsAsync<GitHubGraphQLException>(() =>
                new ProjectImporter(client) { OperationLogDirectory = directory }
                    .ImportIntoAsync(Snapshot(Source()), "target", 7, TestContext.Current.CancellationToken));
            Assert.Equal("BAD_USER_INPUT", exception.ErrorType);
            Assert.Single(handler.UpdateInputs);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Initialized_empty_source_updates_defaults_without_deleting_target_iterations()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-iteration-defaults-").FullName;
        try
        {
            using var handler = new IterationHandler(Target());
            using var client = Client(handler);
            await new ProjectImporter(client) { OperationLogDirectory = directory }
                .ImportIntoAsync(Snapshot(Configuration(14, 1, [])), "target", 7, TestContext.Current.CancellationToken);
            var input = Assert.Single(handler.UpdateInputs);
            Assert.Equal(14, input.GetProperty("duration").GetInt32());
            Assert.Equal(3, input.GetProperty("iterations").GetArrayLength());
            Assert.All(input.GetProperty("iterations").EnumerateArray(), iteration => Assert.True(iteration.TryGetProperty("id", out _)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Uninitialized_target_can_receive_new_iterations()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-iteration-initialize-").FullName;
        try
        {
            using var handler = new IterationHandler(Configuration(0, 0, []));
            using var client = Client(handler);
            var result = await new ProjectImporter(client) { OperationLogDirectory = directory }
                .ImportIntoAsync(Snapshot(Source()), "target", 7, TestContext.Current.CancellationToken);
            Assert.Equal(3, result.IterationIds["Sprint"].Count);
            Assert.All(Assert.Single(handler.UpdateInputs).GetProperty("iterations").EnumerateArray(),
                iteration => Assert.False(iteration.TryGetProperty("id", out _)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("source-title")]
    [InlineData("target-title")]
    [InlineData("target-id")]
    [InlineData("empty-id")]
    public async Task Ambiguous_titles_or_target_ids_fail_before_a_configuration_write(string ambiguity)
    {
        var source = Source();
        var target = Target();
        switch (ambiguity)
        {
            case "source-title":
                source = source with { CompletedIterations = [source.Iterations[0]] };
                break;
            case "target-title":
                target = target with { CompletedIterations = [target.Iterations[0]] };
                break;
            case "target-id":
                target = target with { CompletedIterations = [target.CompletedIterations[0] with { Id = target.Iterations[0].Id }] };
                break;
            case "empty-id":
                target = target with { CompletedIterations = [target.CompletedIterations[0] with { Id = "" }] };
                break;
        }

        var directory = Directory.CreateTempSubdirectory("ghpmv-iteration-ambiguous-").FullName;
        try
        {
            using var handler = new IterationHandler(target);
            using var client = Client(handler);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new ProjectImporter(client) { OperationLogDirectory = directory }
                    .ImportIntoAsync(Snapshot(source), "target", 7, TestContext.Current.CancellationToken));
            Assert.Empty(handler.UpdateInputs);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static GitHubGraphQLClient Client(HttpMessageHandler handler) =>
        new("dummy-token", new Uri("https://example.test/graphql"), handler, delayAsync: (_, _) => Task.CompletedTask);

    private static IterationConfigurationSnapshot Source() => Configuration(14, 1,
    [
        Iteration("source-shared", "Shared", "2026-09-14", 21),
        Iteration("source-new", "New", "2026-10-12", 14),
    ]) with { CompletedIterations = [Iteration("source-past", "Past", "2026-08-04", 3)] };

    private static IterationConfigurationSnapshot Target() => Configuration(7, 2,
    [
        Iteration("target-shared", "Shared", "2026-09-07", 7),
        Iteration("target-only", "Target only", "2026-08-31", 7),
    ]) with { CompletedIterations = [Iteration("target-past", "Past", "2026-07-28", 7)] };

    private static IterationConfigurationSnapshot Configuration(int duration, int startDay, IReadOnlyList<IterationSnapshot> iterations) =>
        new() { Duration = duration, StartDay = startDay, Iterations = iterations, CompletedIterations = [] };

    private static IterationSnapshot Iteration(string id, string title, string date, int duration) =>
        new() { Id = id, Title = title, StartDate = date, Duration = duration };

    private static ProjectSnapshot Snapshot(IterationConfigurationSnapshot configuration) => new()
    {
        SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
        Project = new ProjectInfoSnapshot { Title = "Roadmap", Public = false, Closed = false, Template = false },
        Fields = [new FieldSnapshot { Name = "Sprint", DataType = "ITERATION", IterationConfiguration = configuration }],
        Views = [],
        Workflows = [],
        Items = [],
        StatusUpdates = [],
        LinkedRepositories = [],
        LinkedTeams = [],
    };

    private sealed class IterationHandler(IterationConfigurationSnapshot target) : HttpMessageHandler
    {
        private IterationConfigurationSnapshot _target = target;
        private int _nextId;
        public bool LoseUpdateResponse { get; init; }
        public bool RejectUpdate { get; init; }
        public List<JsonElement> UpdateInputs { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var query = body.RootElement.GetProperty("query").GetString()!;
            object response;
            if (query.Contains("updateProjectV2Field", StringComparison.Ordinal))
            {
                var input = body.RootElement.GetProperty("variables").GetProperty("configuration").Clone();
                UpdateInputs.Add(input);
                if (RejectUpdate)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """{"errors":[{"type":"BAD_USER_INPUT","message":"Synthetic rejected configuration."}]}""",
                            Encoding.UTF8, "application/json"),
                    };
                }

                var iterations = input.GetProperty("iterations").EnumerateArray().Select(iteration => Iteration(
                    iteration.TryGetProperty("id", out var id) ? id.GetString()! : $"new-{++_nextId}",
                    iteration.GetProperty("title").GetString()!,
                    iteration.GetProperty("startDate").GetString()!,
                    iteration.GetProperty("duration").GetInt32())).ToArray();
                var startDate = DateOnly.ParseExact(input.GetProperty("startDate").GetString()!, "yyyy-MM-dd");
                _target = Configuration(input.GetProperty("duration").GetInt32(),
                    startDate.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)startDate.DayOfWeek, iterations);
                if (LoseUpdateResponse && UpdateInputs.Count == 1)
                {
                    throw new HttpRequestException("Synthetic lost response after applying the configuration.");
                }

                response = new { data = new { updateProjectV2Field = new { projectV2Field = Field() } } };
            }
            else if (query.Contains("updateProjectV2(", StringComparison.Ordinal))
            {
                response = new { data = new { updateProjectV2 = new { projectV2 = new { id = "PVT_target" } } } };
            }
            else if (query.Contains("projectV2(number:", StringComparison.Ordinal))
            {
                response = new { data = new { organization = new { projectV2 = new
                {
                    id = "PVT_target", number = 7, title = "Roadmap", url = "https://github.com/orgs/target/projects/7",
                    @public = false, viewerCanUpdate = true,
                } } } };
            }
            else if (query.Contains("fields(first:", StringComparison.Ordinal))
            {
                response = new { data = new { node = new { fields = new { nodes = new[] { Field() } } } } };
            }
            else
            {
                Assert.Contains("duration startDay", query, StringComparison.Ordinal);
                response = new { data = new { node = Field() } };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json"),
            };
        }

        private object Field() => new
        {
            __typename = "ProjectV2IterationField", id = "PVTF_sprint", name = "Sprint", dataType = "ITERATION",
            configuration = new
            {
                duration = _target.Duration, startDay = _target.StartDay,
                iterations = _target.Iterations.Select(Row),
                completedIterations = _target.CompletedIterations.Select(Row),
            },
        };

        private static object Row(IterationSnapshot iteration) =>
            new { id = iteration.Id, title = iteration.Title, startDate = iteration.StartDate, duration = iteration.Duration };
    }
}
