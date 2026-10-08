using System.Net;
using System.Text;
using System.Text.Json;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Tests;

public sealed class PendingViewRecoveryTests
{
    [Fact]
    public async Task Cancellation_after_pending_save_before_send_clears_only_unsent_operation()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-pending-investigation-").FullName;
        try
        {
            using var cancellation = new CancellationTokenSource();
            using var handler = new Handler();
            using var client = Client(handler);
            var log = new ProjectImportLog();
            var importer = new ProjectViewImporter(client, log, async ct =>
            {
                await log.SaveAsync(directory, ct);
                cancellation.Cancel();
            });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Import(importer, cancellation.Token));
            Assert.Equal(0, handler.Creates);
            var persisted = await ProjectImportLog.LoadAsync(directory, TestContext.Current.CancellationToken);
            Assert.Empty(persisted.PendingViews);

            var resumed = new ProjectViewImporter(client, persisted, ct => persisted.SaveAsync(directory, ct));
            Assert.Equal(8, (await Import(resumed))[2]);
            Assert.Equal(1, handler.Creates);
            Assert.Empty(persisted.PendingViews);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Committed_pending_save_failure_before_send_clears_unsent_operation_and_preserves_failure()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-pending-investigation-").FullName;
        try
        {
            using var handler = new Handler();
            using var client = Client(handler);
            var log = new ProjectImportLog();
            var saves = 0;
            var importer = new ProjectViewImporter(client, log, async ct =>
            {
                await log.SaveAsync(directory, ct);
                if (++saves == 1)
                {
                    throw new IOException("Injected post-commit persistence failure");
                }
            });
            await Assert.ThrowsAsync<IOException>(() => Import(importer));
            Assert.Equal(0, handler.Creates);
            var persisted = await ProjectImportLog.LoadAsync(directory, TestContext.Current.CancellationToken);
            Assert.Empty(persisted.PendingViews);
            var resumed = new ProjectViewImporter(client, persisted, ct => persisted.SaveAsync(directory, ct));
            Assert.Equal(8, (await Import(resumed))[2]);
            Assert.Equal(1, handler.Creates);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Successful_create_with_pending_clear_save_failure_is_adopted_on_resume()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-pending-investigation-").FullName;
        try
        {
            using var handler = new Handler();
            using var client = Client(handler);
            var log = new ProjectImportLog();
            var saves = 0;
            var importer = new ProjectViewImporter(client, log, ct =>
            {
                if (++saves == 2)
                {
                    throw new IOException("Injected clear save failure");
                }
                return log.SaveAsync(directory, ct);
            });
            await Assert.ThrowsAsync<IOException>(() => Import(importer));
            Assert.Equal(1, handler.Creates);
            var persisted = await ProjectImportLog.LoadAsync(directory, TestContext.Current.CancellationToken);
            Assert.Single(persisted.PendingViews);
            var resumed = new ProjectViewImporter(client, persisted, ct => persisted.SaveAsync(directory, ct));
            Assert.Equal(8, (await Import(resumed))[2]);
            Assert.Equal(1, handler.Creates);
            Assert.Empty(persisted.PendingViews);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Pending_save_and_cleanup_failures_are_both_reported_without_sending()
    {
        using var handler = new Handler();
        using var client = Client(handler);
        var log = new ProjectImportLog();
        var saves = 0;
        var original = new IOException("Injected initial save failure");
        var cleanup = new IOException("Injected cleanup save failure");
        var importer = new ProjectViewImporter(client, log, _ =>
            throw (++saves == 1 ? original : cleanup));
        var failure = await Assert.ThrowsAsync<AggregateException>(() => Import(importer));
        Assert.Equal([original, cleanup], failure.InnerExceptions);
        Assert.Equal(0, handler.Creates);
    }

    [Fact]
    public async Task Failed_unsent_operation_cleanup_preserves_other_pending_records()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-pending-recovery-").FullName;
        try
        {
            using var handler = new Handler { Created = true, BaselineMissing = true };
            using var client = Client(handler);
            var log = Pending();
            var saves = 0;
            var importer = new ProjectViewImporter(client, log, async ct =>
            {
                await log.SaveAsync(directory, ct);
                if (++saves == 1)
                {
                    throw new IOException("Injected pending save failure");
                }
            });
            await Assert.ThrowsAsync<IOException>(() => importer.ImportAsync(
                [View(1, "Table", "TABLE_LAYOUT"), View(2, "Board", "BOARD_LAYOUT")],
                "PVT_target", new Dictionary<string, string>(), ProjectImportOutcome.Created,
                TestContext.Current.CancellationToken));
            var persisted = await ProjectImportLog.LoadAsync(directory, TestContext.Current.CancellationToken);
            Assert.Equal(2, Assert.Single(persisted.PendingViews).Key);
            Assert.Equal("investigation-operation", persisted.PendingViews[2].OperationId);
            Assert.Equal(0, handler.Creates);
            Assert.Equal(0, handler.Updates);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Cancellation_after_sending_keeps_pending_operation()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler { CancelAtCreate = cancellation };
        using var client = Client(handler);
        var log = new ProjectImportLog();
        var importer = new ProjectViewImporter(client, log, _ => Task.CompletedTask);
        var failure = await Assert.ThrowsAsync<AmbiguousMutationResultException>(() =>
            Import(importer, cancellation.Token));
        Assert.Equal("request-cancelled-or-timed-out", failure.FailureReason);
        Assert.Equal(1, handler.Creates);
        Assert.Single(log.PendingViews);
        handler.CancelAtCreate = null;
        Assert.Equal(8, (await Import(importer))[2]);
        Assert.Equal(1, handler.Creates);
        Assert.Empty(log.PendingViews);
    }

    [Fact]
    public async Task Unreconcilable_later_pending_blocks_earlier_view_updates()
    {
        using var handler = new Handler();
        using var client = Client(handler);
        var log = Pending();
        var importer = new ProjectViewImporter(client, log, _ => Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(
            [View(1, "Table", "TABLE_LAYOUT"), View(2, "Board", "BOARD_LAYOUT")],
            "PVT_target", new Dictionary<string, string>(), ProjectImportOutcome.Created,
            TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Updates);
        Assert.Equal(0, handler.Creates);
        Assert.Single(log.PendingViews);
    }

    [Fact]
    public async Task Overlapping_pending_candidates_block_writes_and_preserve_both_operations()
    {
        using var handler = new Handler { Created = true };
        using var client = Client(handler);
        var log = Pending();
        log.PendingViews[1] = log.PendingViews[2] with { SourceNumber = 1, OperationId = "other-operation" };
        var importer = new ProjectViewImporter(client, log, _ => Task.CompletedTask);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(
            [View(1, "Board", "BOARD_LAYOUT"), View(2, "Board", "BOARD_LAYOUT")],
            "PVT_target", new Dictionary<string, string>(), ProjectImportOutcome.Created,
            TestContext.Current.CancellationToken));
        Assert.Contains("reserved by another operation", failure.Message, StringComparison.Ordinal);
        Assert.Equal(2, log.PendingViews.Count);
        Assert.Equal(0, handler.Updates);
        Assert.Equal(0, handler.Creates);
        Assert.Equal(2, MigrationDiagnostics.Get(failure)?.Element?.Number);
    }

    [Theory]
    [InlineData(false, false, "could not be reconciled")]
    [InlineData(true, true, "matches multiple new views")]
    public async Task Zero_and_multiple_candidates_fail_closed(bool created, bool duplicate, string message)
    {
        using var handler = new Handler { Created = created, Duplicate = duplicate };
        using var client = Client(handler);
        var log = Pending();
        var importer = new ProjectViewImporter(client, log, _ => Task.CompletedTask);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Import(importer));
        Assert.Contains(message, failure.Message, StringComparison.Ordinal);
        Assert.Equal(created ? 2 : 4, handler.ViewQueries);
        Assert.Equal(0, handler.Creates);
        Assert.Equal(0, handler.Updates);
        Assert.Single(log.PendingViews);
    }

    [Fact]
    public async Task Candidate_on_later_page_is_adopted()
    {
        using var handler = new Handler { Created = true, Paginated = true };
        using var client = Client(handler);
        var log = Pending();
        var importer = new ProjectViewImporter(client, log, _ => Task.CompletedTask);
        Assert.Equal(8, (await Import(importer))[2]);
        Assert.Equal(4, handler.ViewQueries);
        Assert.Equal(0, handler.Creates);
        Assert.Equal(1, handler.Updates);
        Assert.Empty(log.PendingViews);
    }

    [Fact]
    public async Task Candidate_visible_on_last_reconciliation_attempt_is_adopted()
    {
        using var handler = new Handler { RevealOnQuery = 4 };
        using var client = Client(handler);
        var log = Pending();
        var importer = new ProjectViewImporter(client, log, _ => Task.CompletedTask);
        Assert.Equal(8, (await Import(importer))[2]);
        Assert.Equal(4, handler.ViewQueries);
        Assert.Equal(0, handler.Creates);
    }

    [Fact]
    public async Task Candidate_visible_after_retry_window_succeeds_on_next_invocation()
    {
        using var handler = new Handler { RevealOnQuery = 5 };
        using var client = Client(handler);
        var log = Pending();
        var importer = new ProjectViewImporter(client, log, _ => Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Import(importer));
        Assert.Equal(8, (await Import(importer))[2]);
        Assert.Equal(0, handler.Creates);
        Assert.Empty(log.PendingViews);
    }

    [Fact]
    public async Task Wrong_project_is_rejected_before_any_request()
    {
        using var handler = new Handler();
        using var client = Client(handler);
        var log = Pending("PVT_other");
        var importer = new ProjectViewImporter(client, log, _ => Task.CompletedTask);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Import(importer));
        Assert.Contains("does not match", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.ViewQueries);
        Assert.Equal(0, handler.Creates);
        Assert.Equal(0, handler.Updates);
    }

    [Fact]
    public async Task Known_pre_side_effect_error_without_mutation_payload_clears_pending()
    {
        using var handler = new Handler
        {
            CreateError = """{"data":null,"errors":[{"type":"BAD_USER_INPUT","message":"Invalid input"}]}""",
        };
        using var client = Client(handler);
        var log = new ProjectImportLog();
        var importer = new ProjectViewImporter(client, log, _ => Task.CompletedTask);
        var failure = await Assert.ThrowsAsync<GitHubGraphQLException>(() => Import(importer));
        Assert.Equal("BAD_USER_INPUT", failure.ErrorType);
        Assert.Empty(log.PendingViews);
        Assert.Equal(1, handler.Creates);
    }

    [Fact]
    public async Task Null_mutation_payload_known_error_preserves_pending_by_design()
    {
        using var handler = new Handler
        {
            CreateError = """{"data":{"createProjectV2View":null},"errors":[{"type":"FORBIDDEN","message":"Forbidden","path":["createProjectV2View"]}]}""",
        };
        using var client = Client(handler);
        var log = new ProjectImportLog();
        var importer = new ProjectViewImporter(client, log, _ => Task.CompletedTask);
        var failure = await Assert.ThrowsAsync<AmbiguousMutationResultException>(() => Import(importer));
        Assert.Equal("graphql-error-with-mutation-payload", failure.FailureReason);
        Assert.Single(log.PendingViews);
        var resumeFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => Import(importer));
        Assert.Contains("could not be reconciled", resumeFailure.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.Creates);
    }

    [Fact]
    public async Task Resume_default_reuse_does_not_overwrite_pending_candidate()
    {
        using var handler = new Handler { Created = true, BaselineMissing = true };
        using var client = Client(handler);
        var log = Pending();
        var importer = new ProjectViewImporter(client, log, _ => Task.CompletedTask);
        var result = await importer.ImportAsync(
            [View(1, "Table", "TABLE_LAYOUT"), View(2, "Board", "BOARD_LAYOUT")],
            "PVT_target", new Dictionary<string, string>(), ProjectImportOutcome.Created,
            TestContext.Current.CancellationToken);
        Assert.NotEqual(result[1], result[2]);
        Assert.Equal(2, handler.Updates);
        Assert.Equal("Board", handler.CreatedName);
        Assert.Equal("BOARD_LAYOUT", handler.CreatedLayout);
        Assert.Empty(log.PendingViews);
        Assert.Equal(1, handler.Creates);
    }

    [Fact]
    public async Task Pending_candidate_is_not_reused_by_earlier_same_named_view()
    {
        using var handler = new Handler { Created = true, BaselineMissing = true };
        using var client = Client(handler);
        var log = Pending();
        var importer = new ProjectViewImporter(client, log, _ => Task.CompletedTask);
        var result = await importer.ImportAsync(
            [View(1, "Board", "BOARD_LAYOUT"), View(2, "Board", "BOARD_LAYOUT")],
            "PVT_target", new Dictionary<string, string>(), ProjectImportOutcome.Created,
            TestContext.Current.CancellationToken);
        Assert.NotEqual(result[1], result[2]);
        Assert.Equal(8, result[2]);
        Assert.Equal(2, handler.Updates);
        Assert.Equal(1, handler.Creates);
        Assert.Empty(log.PendingViews);
    }

    [Fact]
    public async Task Ambiguous_create_then_default_deletion_then_resume_preserves_pending_candidate()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-pending-investigation-").FullName;
        try
        {
            using var handler = new Handler { FailCreateAmbiguously = true };
            using var client = Client(handler);
            var views = new[] { View(1, "Table", "TABLE_LAYOUT"), View(2, "Board", "BOARD_LAYOUT") };
            var log = new ProjectImportLog();
            var importer = new ProjectViewImporter(client, log, ct => log.SaveAsync(directory, ct));
            await Assert.ThrowsAsync<AmbiguousMutationResultException>(() => importer.ImportAsync(
                views, "PVT_target", new Dictionary<string, string>(), ProjectImportOutcome.Created,
                TestContext.Current.CancellationToken));
            Assert.Equal(1, handler.Creates);
            Assert.Equal("Table", handler.DefaultName);
            Assert.Equal(["PVTV_default"], Assert.Single(log.PendingViews).Value.ExistingViewIds);

            handler.BaselineMissing = true;
            handler.FailCreateAmbiguously = false;
            var persisted = await ProjectImportLog.LoadAsync(directory, TestContext.Current.CancellationToken);
            var progress = new List<string>();
            var resumed = new ProjectViewImporter(client, persisted, ct => persisted.SaveAsync(directory, ct))
            {
                OnProgress = progress.Add,
            };
            var result = await resumed.ImportAsync(
                views, "PVT_target", new Dictionary<string, string>(), ProjectImportOutcome.Created,
                TestContext.Current.CancellationToken);
            Assert.NotEqual(result[1], result[2]);
            Assert.Equal(8, result[2]);
            Assert.Contains("Applying API settings for view 'Table' (TABLE_LAYOUT)...", progress);
            Assert.Equal("Board", handler.CreatedName);
            Assert.Equal(2, handler.Creates);
            Assert.Empty((await ProjectImportLog.LoadAsync(directory, TestContext.Current.CancellationToken)).PendingViews);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Real_http_transport_with_precancelled_token_is_not_ambiguous()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var client = new GitHubGraphQLClient("token", new Uri("http://127.0.0.1:1/graphql"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.MutationAsync(
            "createProjectV2View",
            "mutation { createProjectV2View { projectV2View { id } } }",
            requiredResultPath: "projectV2View.id",
            cancellationToken: cancellation.Token));
    }

    private static GitHubGraphQLClient Client(HttpMessageHandler handler)
        => new("token", new Uri("https://example.test/graphql"), handler, (_, _) => Task.CompletedTask);

    private static Task<IReadOnlyDictionary<int, int>> Import(
        ProjectViewImporter importer, CancellationToken? cancellationToken = null)
        => importer.ImportAsync([View(2, "Board", "BOARD_LAYOUT")], "PVT_target",
            new Dictionary<string, string>(), ProjectImportOutcome.Updated,
            cancellationToken ?? TestContext.Current.CancellationToken);

    private static ViewSnapshot View(int number, string name, string layout) => new()
    {
        Number = number, Name = name, Layout = layout,
        GroupByFields = [], SortByFields = [], VerticalGroupByFields = [], VisibleFields = [],
    };

    private static ProjectImportLog Pending(string projectId = "PVT_target") => new()
    {
        PendingViews = new()
        {
            [2] = new()
            {
                OperationId = "investigation-operation", ProjectId = projectId, SourceNumber = 2,
                Name = "Board", Layout = "BOARD_LAYOUT", ExistingViewIds = ["PVTV_default"],
            },
        },
    };

    private sealed class Handler : HttpMessageHandler
    {
        public bool Created { get; set; }
        public bool Duplicate { get; init; }
        public bool Paginated { get; init; }
        public bool BaselineMissing { get; set; }
        public bool FailCreateAmbiguously { get; set; }
        public CancellationTokenSource? CancelAtCreate { get; set; }
        public int RevealOnQuery { get; init; } = int.MaxValue;
        public string? CreateError { get; init; }
        public string CreatedName { get; private set; } = "Board";
        public string CreatedLayout { get; private set; } = "BOARD_LAYOUT";
        public string DefaultName { get; private set; } = "View 1";
        public string DefaultLayout { get; private set; } = "TABLE_LAYOUT";
        public int Creates { get; private set; }
        public int Updates { get; private set; }
        public int ViewQueries { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var query = document.RootElement.GetProperty("query").GetString()!;
            var variables = document.RootElement.GetProperty("variables");
            if (query.Contains("views(first:", StringComparison.Ordinal))
            {
                ViewQueries++;
                Created |= ViewQueries >= RevealOnQuery;
                var secondPage = variables.GetProperty("after").ValueKind == JsonValueKind.String;
                var nodes = new List<object>();
                if (!BaselineMissing && !secondPage)
                {
                    nodes.Add(new { id = "PVTV_default", number = 1, name = DefaultName, layout = DefaultLayout });
                }
                if (Created && (!Paginated || secondPage))
                {
                    nodes.Add(new { id = "PVTV_created", number = 8, name = CreatedName, layout = CreatedLayout });
                    if (Duplicate)
                    {
                        nodes.Add(new { id = "PVTV_duplicate", number = 9, name = "Board", layout = "BOARD_LAYOUT" });
                    }
                }
                return Json(JsonSerializer.Serialize(new
                {
                    data = new { node = new { views = new
                    {
                        nodes,
                        pageInfo = new { hasNextPage = Paginated && !secondPage, endCursor = "page2" },
                    } } },
                }));
            }
            if (query.Contains("createProjectV2View", StringComparison.Ordinal))
            {
                Creates++;
                if (CreateError is not null)
                {
                    return Json(CreateError);
                }
                var name = variables.GetProperty("name").GetString()!;
                var layout = variables.GetProperty("layout").GetString()!;
                var id = Created ? "PVTV_additional" : "PVTV_created";
                var number = Created ? 10 : 8;
                if (!Created)
                {
                    Created = true;
                    CreatedName = name;
                    CreatedLayout = layout;
                }
                if (FailCreateAmbiguously)
                {
                    throw new HttpRequestException("Injected lost response after create side effect");
                }
                if (CancelAtCreate is not null)
                {
                    CancelAtCreate.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return Json(JsonSerializer.Serialize(new
                {
                    data = new { createProjectV2View = new { projectV2View = new { id, number, name, layout } } },
                }));
            }
            if (query.Contains("updateProjectV2View", StringComparison.Ordinal))
            {
                Updates++;
                var id = variables.GetProperty("viewId").GetString()!;
                var name = variables.GetProperty("name").GetString()!;
                var layout = variables.GetProperty("layout").GetString()!;
                if (id == "PVTV_created")
                {
                    CreatedName = name;
                    CreatedLayout = layout;
                }
                else
                {
                    DefaultName = name;
                    DefaultLayout = layout;
                }
                return Json(JsonSerializer.Serialize(new
                {
                    data = new { updateProjectV2View = new { projectV2View =
                        new { id, number = id == "PVTV_created" ? 8 : id == "PVTV_additional" ? 10 : 1, name, layout } } },
                }));
            }
            throw new InvalidOperationException("Unexpected operation: " + query);
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
