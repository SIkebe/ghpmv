using System.Net;
using System.Text;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Tests;

/// <summary>
/// Tests for the narrow <see cref="ProjectTemplateWriteSession"/> seam (issue #46):
/// GitHub rejects status-update writes against a project that is marked as a template,
/// so the importer temporarily unmarks the target and restores the flag as the final
/// import stage. Broader project-template migration (issue #47) is out of scope here.
/// </summary>
public class ProjectTemplateWriteSessionTests
{
    [Fact]
    public async Task Prepare_leaves_a_non_template_project_alone()
    {
        using var handler = new TemplateHandler(template: false);
        using var client = CreateClient(handler);
        var progress = new List<string>();

        var session = await ProjectTemplateWriteSession.PrepareAsync(
            client,
            ProjectId,
            progress.Add,
            TestContext.Current.CancellationToken);

        Assert.False(session.RestorationRequired);
        Assert.Single(handler.RequestBodies);
        Assert.Equal(0, handler.UnmarkCount);
        Assert.Equal(0, handler.MarkCount);
        Assert.Empty(progress);
    }

    [Fact]
    public async Task Prepare_unmarks_an_existing_template_project()
    {
        using var handler = new TemplateHandler(template: true);
        using var client = CreateClient(handler);

        var session = await ProjectTemplateWriteSession.PrepareAsync(
            client,
            ProjectId,
            onProgress: null,
            TestContext.Current.CancellationToken);

        Assert.True(session.RestorationRequired);
        Assert.Equal(1, handler.UnmarkCount);
        Assert.Equal(0, handler.MarkCount);
        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Contains("unmarkProjectV2AsTemplate", handler.RequestBodies[1], StringComparison.Ordinal);
        Assert.Contains($"\"projectId\":\"{ProjectId}\"", handler.RequestBodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prepare_failure_attempts_to_restore_template_state_before_rethrowing()
    {
        using var handler = new TemplateHandler(template: true) { UnmarkPayloadIncomplete = true };
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<GitHubGraphQLException>(
            () => ProjectTemplateWriteSession.PrepareAsync(
                client,
                ProjectId,
                onProgress: null,
                TestContext.Current.CancellationToken));

        Assert.Equal(
            "GraphQL success response did not contain the expected 'unmarkProjectV2AsTemplate' result.",
            exception.Message);
        Assert.Equal(4, handler.UnmarkCount);
        Assert.Equal(1, handler.MarkCount);
        Assert.Contains("markProjectV2AsTemplate", handler.RequestBodies[^1], StringComparison.Ordinal);
        Assert.DoesNotContain("unmarkProjectV2AsTemplate", handler.RequestBodies[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pending_restoration_survives_a_new_process_and_is_cleared_after_remark()
    {
        var persistedRestorationRequired = false;
        Task PersistAsync(bool required, CancellationToken _)
        {
            persistedRestorationRequired = required;
            return Task.CompletedTask;
        }

        using (var firstHandler = new TemplateHandler(template: true))
        using (var firstClient = CreateClient(firstHandler))
        {
            var interruptedSession = await ProjectTemplateWriteSession.PrepareAsync(
                firstClient,
                ProjectId,
                restorationWasPending: false,
                PersistAsync,
                onProgress: null,
                TestContext.Current.CancellationToken);

            Assert.True(interruptedSession.RestorationRequired);
            Assert.True(persistedRestorationRequired);
        }

        using var resumedHandler = new TemplateHandler(template: false);
        using var resumedClient = CreateClient(resumedHandler);
        var resumedSession = await ProjectTemplateWriteSession.PrepareAsync(
            resumedClient,
            ProjectId,
            persistedRestorationRequired,
            PersistAsync,
            onProgress: null,
            TestContext.Current.CancellationToken);

        Assert.True(resumedSession.RestorationRequired);
        Assert.Equal(0, resumedHandler.UnmarkCount);
        await resumedSession.RestoreAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, resumedHandler.MarkCount);
        Assert.False(persistedRestorationRequired);
    }

    [Fact]
    public async Task Pending_restoration_already_restored_clears_the_flag_without_unmarking()
    {
        var persistedRestorationRequired = true;
        Task PersistAsync(bool required, CancellationToken _)
        {
            persistedRestorationRequired = required;
            return Task.CompletedTask;
        }

        using var handler = new TemplateHandler(template: true);
        using var client = CreateClient(handler);

        var session = await ProjectTemplateWriteSession.PrepareAsync(
            client,
            ProjectId,
            restorationWasPending: true,
            PersistAsync,
            onProgress: null,
            TestContext.Current.CancellationToken);

        Assert.False(session.RestorationRequired);
        Assert.False(persistedRestorationRequired);
        Assert.Equal(0, handler.UnmarkCount);
        Assert.Equal(0, handler.MarkCount);
        Assert.Single(handler.RequestBodies);
    }

    [Fact]
    public async Task Restore_remarks_the_project_as_a_template()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var handler = new TemplateHandler(template: true);
        using var client = CreateClient(handler);

        var session = await ProjectTemplateWriteSession.PrepareAsync(
            client,
            ProjectId,
            onProgress: null,
            cancellationToken);
        await session.RestoreAsync(cancellationToken);

        Assert.Equal(1, handler.UnmarkCount);
        Assert.Equal(1, handler.MarkCount);

        // Unmark must come first, otherwise the status-update writes in between would
        // have run against a template project and failed.
        Assert.Equal(3, handler.RequestBodies.Count);
        Assert.Contains("unmarkProjectV2AsTemplate", handler.RequestBodies[1], StringComparison.Ordinal);
        Assert.Contains("markProjectV2AsTemplate", handler.RequestBodies[2], StringComparison.Ordinal);
        Assert.DoesNotContain("unmarkProjectV2AsTemplate", handler.RequestBodies[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_is_idempotent_when_called_twice()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var handler = new TemplateHandler(template: true);
        using var client = CreateClient(handler);
        var progress = new List<string>();

        var session = await ProjectTemplateWriteSession.PrepareAsync(
            client,
            ProjectId,
            progress.Add,
            cancellationToken);

        // The CLI restores on the happy path and again from its finally block.
        await session.RestoreAsync(cancellationToken);
        await session.RestoreAsync(cancellationToken);

        Assert.Equal(1, handler.MarkCount);
        Assert.Equal(3, handler.RequestBodies.Count);
        Assert.Equal(
            1,
            progress.Count(message => message.Contains("Restoring the target project's", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Restore_is_a_no_op_when_restoration_was_not_required()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var handler = new TemplateHandler(template: false);
        using var client = CreateClient(handler);
        var progress = new List<string>();

        var session = await ProjectTemplateWriteSession.PrepareAsync(
            client,
            ProjectId,
            progress.Add,
            cancellationToken);
        await session.RestoreAsync(cancellationToken);

        // Marking a project that was never a template would silently change the target.
        Assert.False(session.RestorationRequired);
        Assert.Equal(0, handler.MarkCount);
        Assert.Single(handler.RequestBodies);
        Assert.Empty(progress);
    }

    [Fact]
    public async Task Prepare_throws_when_the_target_node_is_missing()
    {
        using var handler = new TemplateHandler(template: false) { NodeMissing = true };
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<GitHubGraphQLException>(
            () => ProjectTemplateWriteSession.PrepareAsync(
                client,
                "PVT_missing",
                onProgress: null,
                TestContext.Current.CancellationToken));

        Assert.Equal(
            "Target project 'PVT_missing' was not found while checking template state.",
            exception.Message);
        Assert.Single(handler.RequestBodies);
        Assert.Equal(0, handler.UnmarkCount);
    }

    [Fact]
    public async Task Prepare_and_restore_emit_the_documented_progress_messages()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var handler = new TemplateHandler(template: true);
        using var client = CreateClient(handler);
        var progress = new List<string>();

        var session = await ProjectTemplateWriteSession.PrepareAsync(
            client,
            ProjectId,
            progress.Add,
            cancellationToken);
        await session.RestoreAsync(cancellationToken);

        Assert.Equal(
            [
                "Temporarily unmarking the target project as a template before status update writes...",
                "Restoring the target project's template state as the final import stage...",
            ],
            progress);
    }

    [Fact]
    public async Task Template_mutations_use_the_idempotent_retry_policy_and_required_result_path()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var retryingHandler = new TemplateHandler(template: true) { FailFirstMarkTransiently = true };
        using var retryingClient = CreateClient(retryingHandler);

        var session = await ProjectTemplateWriteSession.PrepareAsync(
            retryingClient,
            ProjectId,
            onProgress: null,
            cancellationToken);
        await session.RestoreAsync(cancellationToken);

        // A create-policy mutation would have failed the whole import as ambiguous;
        // marking a template is idempotent, so the transient error is simply retried.
        Assert.Equal(2, retryingHandler.MarkCount);
        Assert.Equal(4, retryingHandler.RequestBodies.Count);

        using var incompleteHandler = new TemplateHandler(template: true) { MarkPayloadIncomplete = true };
        using var incompleteClient = CreateClient(incompleteHandler);

        var incompleteSession = await ProjectTemplateWriteSession.PrepareAsync(
            incompleteClient,
            ProjectId,
            onProgress: null,
            cancellationToken);
        var exception = await Assert.ThrowsAsync<GitHubGraphQLException>(
            () => incompleteSession.RestoreAsync(cancellationToken));

        // requiredResultPath: "projectV2.id" — a payload without it is never accepted
        // as success, no matter how many times it is retried.
        Assert.Equal(
            "GraphQL success response did not contain the expected 'markProjectV2AsTemplate' result.",
            exception.Message);
        Assert.Equal(4, incompleteHandler.MarkCount);
    }

    [Fact]
    public async Task Apply_snapshot_does_not_touch_template_state()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-template-").FullName;
        try
        {
            using var handler = new SnapshotImportHandler();
            using var client = CreateClient(handler);
            var snapshot = new ProjectSnapshot
            {
                SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
                Project = new ProjectInfoSnapshot { Title = "Roadmap", Public = false, Closed = false },
                Fields = [],
                Views = [],
                Workflows = [],
                Items = [],
                StatusUpdates =
                [
                    new StatusUpdateSnapshot
                    {
                        Body = "Kickoff",
                        Status = "ON_TRACK",
                        Creator = "octocat",
                        CreatedAt = "2026-01-01T09:00:00Z",
                        UpdatedAt = "2026-01-01T09:00:00Z",
                    },
                ],
            };

            var result = await new ProjectImporter(client) { OperationLogDirectory = directory }
                .ImportIntoAsync(snapshot, "target", 7, TestContext.Current.CancellationToken);

            Assert.Equal(ProjectImportOutcome.Updated, result.Outcome);

            // The template seam belongs to the CLI / fixture orchestration, not to
            // ApplySnapshotAsync: the project importer must never toggle the flag, and
            // it must not replay status updates either.
            Assert.NotEmpty(handler.RequestBodies);
            Assert.DoesNotContain(
                handler.RequestBodies,
                body => body.Contains("markProjectV2AsTemplate", StringComparison.Ordinal)
                    || body.Contains("unmarkProjectV2AsTemplate", StringComparison.Ordinal));
            Assert.DoesNotContain(
                handler.RequestBodies,
                body => body.Contains("createProjectV2StatusUpdate", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private const string ProjectId = "PVT_target";

    private static GitHubGraphQLClient CreateClient(HttpMessageHandler handler) =>
        new("token", baseUrl: null, handler, static (_, _) => Task.CompletedTask);

    private sealed class TemplateHandler(bool template) : HttpMessageHandler
    {
        public bool NodeMissing { get; init; }

        public bool FailFirstMarkTransiently { get; init; }

        public bool MarkPayloadIncomplete { get; init; }

        public bool UnmarkPayloadIncomplete { get; init; }

        public List<string> RequestBodies { get; } = [];

        public int MarkCount { get; private set; }

        public int UnmarkCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(body);

            if (body.Contains("unmarkProjectV2AsTemplate", StringComparison.Ordinal))
            {
                UnmarkCount++;
                if (UnmarkPayloadIncomplete)
                {
                    return Json("""{"data":{"unmarkProjectV2AsTemplate":{"projectV2":null}}}""");
                }

                return Json("""{"data":{"unmarkProjectV2AsTemplate":{"projectV2":{"id":"PVT_target","template":false}}}}""");
            }

            if (body.Contains("markProjectV2AsTemplate", StringComparison.Ordinal))
            {
                MarkCount++;
                if (MarkPayloadIncomplete)
                {
                    return Json("""{"data":{"markProjectV2AsTemplate":{"projectV2":null}}}""");
                }

                if (FailFirstMarkTransiently && MarkCount == 1)
                {
                    return Json("""{"errors":[{"message":"Something went wrong while executing your query."}]}""");
                }

                return Json("""{"data":{"markProjectV2AsTemplate":{"projectV2":{"id":"PVT_target","template":true}}}}""");
            }

            if (NodeMissing)
            {
                return Json("""{"data":{"node":null}}""");
            }

            return Json(
                "{\"data\":{\"node\":{\"id\":\"PVT_target\",\"template\":" +
                (template ? "true" : "false") + "}}}");
        }

        private static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
    }

    private sealed class SnapshotImportHandler : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(body);
            var response = body switch
            {
                _ when body.Contains("projectV2(number:", StringComparison.Ordinal) =>
                    """{"data":{"organization":{"projectV2":{"id":"PVT_target","number":7,"title":"Roadmap","url":"https://github.com/orgs/target/projects/7","public":false}}}}""",
                _ when body.Contains("updateProjectV2(", StringComparison.Ordinal) =>
                    """{"data":{"updateProjectV2":{"projectV2":{"id":"PVT_target"}}}}""",
                _ when body.Contains("fields(first:", StringComparison.Ordinal) =>
                    """{"data":{"node":{"fields":{"nodes":[{"__typename":"ProjectV2Field","id":"PVTF_title","name":"Title","dataType":"TITLE"}]}}}}""",
                _ => throw new InvalidOperationException($"Unexpected request: {body}"),
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }
}
