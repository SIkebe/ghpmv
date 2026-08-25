using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Tests;

public class CliImportTests
{
    [Fact]
    public async Task Verify_reports_category_statuses_and_writes_consistent_json()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-verify-" + Guid.NewGuid().ToString("N"));
        var reportPath = Path.Combine(directory, "report.json");
        await SnapshotFile.SaveAsync(VerifySnapshot(), directory, cancellationToken);

        using var server = new GraphQlStubServer(
            VerifyProjectResponse,
            VerifyItemsResponse,
            VerifyStatusUpdatesResponse,
            VerifyFieldsResponse,
            VerifyTeamsResponse);
        try
        {
            var result = await RunVerifyCliAsync(directory, server, "--report-json", reportPath);

            Assert.Equal(5, server.RequestBodies.Count);
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("Project: Match", result.Output, StringComparison.Ordinal);
            Assert.Contains("LinkedRepository: PartialMatch", result.Output, StringComparison.Ordinal);
            Assert.Contains("Collaborator: NotVerified", result.Output, StringComparison.Ordinal);
            Assert.Contains("1 warning(s)", result.Output, StringComparison.Ordinal);
            Assert.EndsWith("NotVerified." + Environment.NewLine, result.Output, StringComparison.Ordinal);

            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath, cancellationToken));
            Assert.Equal("NotVerified", report.RootElement.GetProperty("status").GetString());
            Assert.Equal(1, report.RootElement.GetProperty("warningCount").GetInt32());
            Assert.Equal(1, report.RootElement.GetProperty("notVerifiedCount").GetInt32());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Verify_reports_template_drift_in_the_project_category_and_json()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-verify-template-" + Guid.NewGuid().ToString("N"));
        var reportPath = Path.Combine(directory, "report.json");
        var snapshot = VerifySnapshot();
        await SnapshotFile.SaveAsync(
            snapshot with { Project = snapshot.Project with { Template = true } },
            directory,
            cancellationToken);

        using var server = new GraphQlStubServer(
            VerifyProjectResponse,
            VerifyItemsResponse,
            VerifyStatusUpdatesResponse,
            VerifyFieldsResponse,
            VerifyTeamsResponse);
        try
        {
            var result = await RunVerifyCliAsync(directory, server, "--report-json", reportPath);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("Project: Mismatch", result.Output, StringComparison.Ordinal);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath, cancellationToken));
            Assert.Contains(
                report.RootElement.GetProperty("differences").EnumerateArray(),
                difference => difference.GetProperty("category").GetString() == "Project"
                    && difference.GetProperty("message").GetString()!
                        .Contains("template state mismatch", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Verify_categories_limits_comparison_and_api_sections()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-verify-categories-" + Guid.NewGuid().ToString("N"));
        await SnapshotFile.SaveAsync(VerifySnapshot(), directory, cancellationToken);

        using var server = new GraphQlStubServer(VerifyProjectResponse);
        try
        {
            var result = await RunVerifyCliAsync(directory, server, "--categories", "view");

            Assert.Equal(0, result.ExitCode);
            Assert.Single(server.RequestBodies);
            Assert.Contains("selected verification categories match", result.Output, StringComparison.Ordinal);
            Assert.Contains("View: Match", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("Project:", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("Workflow:", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Conflict_skip_with_browser_automation_does_not_run_downstream_importers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-skip-" + Guid.NewGuid().ToString("N"));
        await SnapshotFile.SaveAsync(SnapshotWithDownstreamContent(), directory, cancellationToken);

        using var server = new GraphQlStubServer(ExistingProjectResponse);
        try
        {
            var result = await RunCliAsync(
                directory,
                server,
                "--on-conflict", "skip",
                "--enable-browser-automation");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("result=skipped project=42", result.Output, StringComparison.Ordinal);
            Assert.Contains("skipped without making changes", result.Error, StringComparison.Ordinal);
            Assert.Single(server.RequestBodies);
            Assert.DoesNotContain("mutation", server.RequestBodies[0], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Conflict_fail_returns_error_without_a_result_line()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-fail-" + Guid.NewGuid().ToString("N"));
        await SnapshotFile.SaveAsync(MinimalSnapshot(), directory, cancellationToken);

        using var server = new GraphQlStubServer(ExistingProjectResponse);
        try
        {
            var result = await RunCliAsync(directory, server, "--on-conflict", "fail");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("already exists", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("result=", result.Output, StringComparison.Ordinal);
            var request = Assert.Single(server.RequestBodies);
            Assert.DoesNotContain("mutation", request, StringComparison.OrdinalIgnoreCase);
        }

        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Browser_filter_mapping_preflight_fails_before_any_mutation_by_default()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-filter-preflight-" + Guid.NewGuid().ToString("N"));
        var snapshot = MinimalSnapshot() with
        {
            Views =
            [
                new ViewSnapshot
                {
                    Number = 1,
                    Name = "EMU",
                    Layout = "TABLE_LAYOUT",
                    Filter = "assignee:old-user",
                    GroupByFields = [],
                    SortByFields = [],
                    VerticalGroupByFields = [],
                    VisibleFields = [],
                },
            ],
            StatusUpdates = [],
        };
        await SnapshotFile.SaveAsync(snapshot, directory, cancellationToken);
        await new ImportLog
        {
            ProjectId = "PVT_existing",
            SourceSnapshotFingerprint = ImportLog.ComputeSnapshotFingerprint(snapshot),
            TemplateRestorationRequired = true,
        }.SaveAsync(directory, cancellationToken);

        using var server = new GraphQlStubServer(
            TemplateProjectResponse,
            ExistingProjectResponse);
        try
        {
            var result = await RunCliAsync(directory, server, "--enable-browser-automation");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("unmapped assignee value 'old-user'", result.Error, StringComparison.Ordinal);
            Assert.Contains("Filter mapping preflight failed", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain(server.RequestBodies, request =>
                request.Contains("mutation", StringComparison.OrdinalIgnoreCase));
            var importLog = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(importLog);
            Assert.False(importLog.TemplateRestorationRequired);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Conflict_update_emits_stable_result_and_applies_project_mutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-update-" + Guid.NewGuid().ToString("N"));
        await SnapshotFile.SaveAsync(MinimalSnapshot(), directory, cancellationToken);

        using var server = new GraphQlStubServer(
            ExistingProjectResponse,
            UpdateProjectResponse,
            EmptyFieldsResponse,
            NonTemplateProjectResponse);
        try
        {
            var result = await RunCliAsync(directory, server, "--on-conflict", "update");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("result=updated project=42", result.Output, StringComparison.Ordinal);
            Assert.Contains(
                "items: created=0 resumed=0 already-complete=0 skipped=0 warnings=0",
                result.Output,
                StringComparison.Ordinal);
            Assert.Contains(
                "status-updates: created=0 resumed=0 already-complete=0",
                result.Output,
                StringComparison.Ordinal);
            Assert.Contains("views: imported=0 warnings=0", result.Output, StringComparison.Ordinal);
            Assert.Equal(4, server.RequestBodies.Count);
            Assert.Single(server.RequestBodies, request =>
                request.Contains("mutation", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(server.RequestBodies, request =>
                request.Contains("ProjectV2AsTemplate", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task User_owned_template_snapshot_fails_before_any_api_request()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-user-template-" + Guid.NewGuid().ToString("N"));
        await SnapshotFile.SaveAsync(SnapshotWithTemplate(true), directory, cancellationToken);

        using var server = new GraphQlStubServer();
        try
        {
            var result = await RunCliAsync(directory, server, "--owner-type", "user");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("user-owned Project cannot be marked as a template", result.Error, StringComparison.Ordinal);
            Assert.Empty(server.RequestBodies);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Successful_created_project_import_restores_requested_conflict_policy_on_retry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-created-complete-" + Guid.NewGuid().ToString("N"));
        await SnapshotFile.SaveAsync(MinimalSnapshot(), directory, cancellationToken);

        try
        {
            using (var createServer = new GraphQlStubServer(
                       EmptyProjectsResponse,
                       OwnerResponse,
                       CreateProjectResponse,
                       UpdateCreatedProjectResponse,
                       EmptyFieldsResponse,
                       NonTemplateProjectResponse))
            {
                var created = await RunCliAsync(directory, createServer);

                Assert.Equal(0, created.ExitCode);
                Assert.Contains("result=created project=42", created.Output, StringComparison.Ordinal);
            }

            var completedLog = await ProjectImportLog.LoadAsync(directory, cancellationToken);
            Assert.Equal("PVT_created", completedLog.CreatedProjectId);
            Assert.True(completedLog.ImportCompleted);
            Assert.False(completedLog.HasUnresolvedWarnings);

            using var retryServer = new GraphQlStubServer(CreatedProjectLookupResponse);
            var retry = await RunCliAsync(directory, retryServer, "--on-conflict", "fail");

            Assert.Equal(1, retry.ExitCode);
            Assert.Contains("already exists", retry.Error, StringComparison.Ordinal);
            Assert.Single(retryServer.RequestBodies);
            Assert.DoesNotContain(
                retryServer.RequestBodies,
                request => request.Contains("mutation", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void Import_completion_remains_incomplete_when_core_stages_warn(
        int projectWarningCount,
        int itemWarningCount)
    {
        var log = new ProjectImportLog
        {
            CreatedProjectId = "PVT_created",
            ImportCompleted = false,
            HasUnresolvedWarnings = false,
        };

        var changed = log.TryMarkImportCompleted(
            browserAutomationEnabled: false,
            projectWarningCount,
            itemWarningCount,
            viewWarningCount: 0,
            workflowWarningCount: 0);

        Assert.False(changed);
        Assert.False(log.ImportCompleted);
        Assert.True(log.HasUnresolvedWarnings);

        changed = log.TryMarkImportCompleted(
            browserAutomationEnabled: false,
            projectWarningCount: 0,
            itemWarningCount: 0,
            viewWarningCount: 0,
            workflowWarningCount: 0);

        Assert.False(changed);
        Assert.False(log.ImportCompleted);
        Assert.True(log.HasUnresolvedWarnings);
    }

    [Fact]
    public void Legacy_incomplete_import_without_warning_state_fails_closed()
    {
        var log = new ProjectImportLog
        {
            CreatedProjectId = "PVT_created",
            ImportCompleted = false,
        };

        var changed = log.TryMarkImportCompleted(
            browserAutomationEnabled: false,
            projectWarningCount: 0,
            itemWarningCount: 0,
            viewWarningCount: 0,
            workflowWarningCount: 0);

        Assert.False(changed);
        Assert.False(log.ImportCompleted);
        Assert.Null(log.HasUnresolvedWarnings);
    }

    [Fact]
    public async Task Incomplete_item_log_forces_update_on_default_retry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-resume-" + Guid.NewGuid().ToString("N"));
        var snapshot = MinimalSnapshot() with
        {
            Items =
            [
                new ItemSnapshot
                {
                    Type = "DRAFT_ISSUE",
                    Position = 0,
                    IsArchived = false,
                    Draft = new DraftIssueSnapshot { Title = "Interrupted", Assignees = [] },
                    FieldValues = [],
                },
            ],
        };
        await SnapshotFile.SaveAsync(snapshot, directory, cancellationToken);
        var log = new Ghpmv.Core.Import.ImportLog
        {
            ProjectId = "PVT_existing",
            SourceSnapshotFingerprint = Ghpmv.Core.Import.ImportLog.ComputeSnapshotFingerprint(snapshot),
        };
        log.Items["0"] = "PVTI_interrupted";
        log.ItemStates["DRAFT_ISSUE:Interrupted::::position:0"] = new Ghpmv.Core.Import.ImportItemState
        {
            TargetItemId = "PVTI_interrupted",
            TargetContentIdentity = "DRAFT_ISSUE:assignees:",
        };
        await log.SaveAsync(directory, cancellationToken);

        using var server = new GraphQlStubServer(
            ExistingProjectResponse,
            UpdateProjectResponse,
            EmptyFieldsResponse,
            PositionResponse,
            NonTemplateProjectResponse);
        try
        {
            var result = await RunCliAsync(directory, server);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("result=updated project=42", result.Output, StringComparison.Ordinal);
            Assert.Contains("resumed=1", result.Output, StringComparison.Ordinal);
            var completed = await Ghpmv.Core.Import.ImportLog.LoadAsync(directory, cancellationToken);
            var state = Assert.Single(completed!.ItemStates).Value;
            Assert.True(state.FieldValuesApplied);
            Assert.True(state.PositionApplied);
            Assert.True(state.ArchiveApplied);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_prints_the_status_update_summary_line_after_items()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-status-line-" + Guid.NewGuid().ToString("N"));
        await SnapshotFile.SaveAsync(SnapshotWithStatusUpdates(), directory, cancellationToken);

        using var server = new GraphQlStubServer(
            ExistingProjectResponse,
            UpdateProjectResponse,
            EmptyFieldsResponse,
            NonTemplateProjectResponse,
            CreateStatusUpdateResponse);
        try
        {
            var result = await RunCliAsync(directory, server, "--on-conflict", "update");

            Assert.Equal(0, result.ExitCode);
            string[] expected =
            [
                "https://github.com/orgs/target/projects/42",
                "result=updated project=42",
                "items: created=0 resumed=0 already-complete=0 skipped=0 warnings=0",
                "status-updates: created=1 resumed=0 already-complete=0",
                "views: imported=0 warnings=0",
            ];
            Assert.Equal(expected, result.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));

            // The stdout contract is additive: the new line reports real work, and the
            // created update carries the attribution note plus the snapshot's dates.
            Assert.Equal(5, server.RequestBodies.Count);
            var createRequest = Assert.Single(
                server.RequestBodies,
                request => request.Contains("createProjectV2StatusUpdate", StringComparison.Ordinal));
            Assert.Contains("Originally created by @octocat on 2024-01-05T09:00:00Z", createRequest, StringComparison.Ordinal);
            Assert.Contains("Kickoff complete.", createRequest, StringComparison.Ordinal);
            Assert.Contains("\"status\":\"ON_TRACK\"", createRequest, StringComparison.Ordinal);
            Assert.Contains("\"startDate\":\"2024-01-01\"", createRequest, StringComparison.Ordinal);
            Assert.Contains("\"targetDate\":\"2024-03-31\"", createRequest, StringComparison.Ordinal);

            // A non-template target must never be touched by the template seam.
            Assert.DoesNotContain(server.RequestBodies, request =>
                request.Contains("ProjectV2AsTemplate", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_skip_path_does_not_print_the_status_update_line()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-status-skip-" + Guid.NewGuid().ToString("N"));
        await SnapshotFile.SaveAsync(SnapshotWithStatusUpdates(), directory, cancellationToken);

        using var server = new GraphQlStubServer(ExistingProjectResponse);
        try
        {
            var result = await RunCliAsync(directory, server, "--on-conflict", "skip");

            Assert.Equal(0, result.ExitCode);
            string[] expected =
            [
                "https://github.com/orgs/target/projects/42",
                "result=skipped project=42",
            ];
            Assert.Equal(expected, result.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
            Assert.DoesNotContain("status-updates:", result.Output, StringComparison.Ordinal);

            // Skipping means no downstream stage ran at all: no template probe, no writes.
            var request = Assert.Single(server.RequestBodies);
            Assert.DoesNotContain("mutation", request, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("statusUpdates", request, StringComparison.Ordinal);
            Assert.Contains("skipped without making changes", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_marks_and_restores_the_template_only_when_the_snapshot_has_status_updates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var withoutDirectory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-template-none-" + Guid.NewGuid().ToString("N"));
        var withDirectory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-template-some-" + Guid.NewGuid().ToString("N"));
        await SnapshotFile.SaveAsync(MinimalSnapshot() with { StatusUpdates = [] }, withoutDirectory, cancellationToken);
        var templateSnapshot = SnapshotWithStatusUpdates();
        await SnapshotFile.SaveAsync(
            templateSnapshot with { Project = templateSnapshot.Project with { Template = true } },
            withDirectory,
            cancellationToken);

        try
        {
            using (var withoutServer = new GraphQlStubServer(
                ExistingProjectResponse,
                UpdateProjectResponse,
                EmptyFieldsResponse,
                NonTemplateProjectResponse))
            {
                var withoutResult = await RunCliAsync(withoutDirectory, withoutServer, "--on-conflict", "update");

                Assert.Equal(0, withoutResult.ExitCode);
                Assert.Contains(
                    "status-updates: created=0 resumed=0 already-complete=0",
                    withoutResult.Output,
                    StringComparison.Ordinal);

                Assert.Equal(4, withoutServer.RequestBodies.Count);
                Assert.DoesNotContain(withoutServer.RequestBodies, request =>
                    request.Contains("ProjectV2AsTemplate", StringComparison.Ordinal));
                Assert.DoesNotContain(withoutServer.RequestBodies, request =>
                    request.Contains("createProjectV2StatusUpdate", StringComparison.Ordinal));
            }

            using var withServer = new GraphQlStubServer(
                ExistingProjectResponse,
                UpdateProjectResponse,
                EmptyFieldsResponse,
                TemplateProjectResponse,
                UnmarkTemplateResponse,
                CreateStatusUpdateResponse,
                MarkTemplateResponse);
            var withResult = await RunCliAsync(withDirectory, withServer, "--on-conflict", "update");

            Assert.Equal(0, withResult.ExitCode);
            Assert.Contains(
                "status-updates: created=1 resumed=0 already-complete=0",
                withResult.Output,
                StringComparison.Ordinal);
            Assert.Equal(7, withServer.RequestBodies.Count);
            Assert.Single(withServer.RequestBodies, IsUnmarkTemplateMutation);
            Assert.Single(withServer.RequestBodies, IsMarkTemplateMutation);
            Assert.Single(withServer.RequestBodies, request =>
                request.Contains("createProjectV2StatusUpdate", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(withoutDirectory, recursive: true);
            Directory.Delete(withDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_applies_the_requested_template_state_after_downstream_importers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-template-order-" + Guid.NewGuid().ToString("N"));
        var snapshot = SnapshotWithStatusUpdates();
        await SnapshotFile.SaveAsync(
            snapshot with { Project = snapshot.Project with { Template = true } },
            directory,
            cancellationToken);

        using var server = new GraphQlStubServer(
            ExistingProjectResponse,
            UpdateProjectResponse,
            EmptyFieldsResponse,
            TemplateProjectResponse,
            UnmarkTemplateResponse,
            CreateStatusUpdateResponse,
            MarkTemplateResponse);
        try
        {
            var result = await RunCliAsync(directory, server, "--on-conflict", "update");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(7, server.RequestBodies.Count);

            var unmarkIndex = server.RequestBodies.FindIndex(IsUnmarkTemplateMutation);
            var createIndex = server.RequestBodies.FindIndex(request =>
                request.Contains("createProjectV2StatusUpdate", StringComparison.Ordinal));
            var markIndex = server.RequestBodies.FindIndex(IsMarkTemplateMutation);
            var projectUpdateIndex = server.RequestBodies.FindIndex(request =>
                request.Contains("updateProjectV2", StringComparison.Ordinal));

            Assert.True(projectUpdateIndex >= 0 && projectUpdateIndex < unmarkIndex);
            Assert.True(unmarkIndex >= 0 && unmarkIndex < createIndex);
            Assert.True(createIndex < markIndex);

            // The requested final template state is the last orchestration stage.
            Assert.Equal(server.RequestBodies.Count - 1, markIndex);
            Assert.Contains(
                "Temporarily unmarking the target project as a template before status update writes...",
                result.Error,
                StringComparison.Ordinal);
            Assert.Contains(
                "Marking the target project as a template as the final import stage...",
                result.Error,
                StringComparison.Ordinal);
            Assert.Contains(
                "status-updates: created=1 resumed=0 already-complete=0",
                result.Output,
                StringComparison.Ordinal);
            var importLog = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(importLog);
            Assert.Single(importLog.StatusUpdates);
            Assert.Empty(importLog.PendingStatusUpdates);
            Assert.False(importLog.TemplateRestorationRequired);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_marks_a_new_project_as_a_template_only_after_all_writers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-template-create-" + Guid.NewGuid().ToString("N"));
        var snapshot = SnapshotWithStatusUpdates();
        await SnapshotFile.SaveAsync(
            snapshot with { Project = snapshot.Project with { Template = true } },
            directory,
            cancellationToken);

        using var server = new GraphQlStubServer(
            EmptyProjectsResponse,
            OwnerResponse,
            CreateProjectResponse,
            UpdateCreatedProjectResponse,
            EmptyFieldsResponse,
            """{"data":{"node":{"id":"PVT_created","template":false}}}""",
            CreateStatusUpdateResponse,
            """{"data":{"markProjectV2AsTemplate":{"projectV2":{"id":"PVT_created","template":true}}}}""");
        try
        {
            var result = await RunCliAsync(directory, server);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(8, server.RequestBodies.Count);
            var statusUpdateIndex = server.RequestBodies.FindIndex(request =>
                request.Contains("createProjectV2StatusUpdate", StringComparison.Ordinal));
            Assert.True(statusUpdateIndex >= 0 && statusUpdateIndex < server.RequestBodies.Count - 1);
            Assert.True(IsMarkTemplateMutation(server.RequestBodies[^1]));
            Assert.Contains("\"projectId\":\"PVT_created\"", server.RequestBodies[^1], StringComparison.Ordinal);
            Assert.Contains(
                "Marking the target project as a template as the final import stage...",
                result.Error,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_unmarks_an_existing_project_as_the_final_stage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-template-unmark-" + Guid.NewGuid().ToString("N"));
        await SnapshotFile.SaveAsync(
            SnapshotWithTemplate(false) with { StatusUpdates = [] },
            directory,
            cancellationToken);

        using var server = new GraphQlStubServer(
            ExistingProjectResponse,
            UpdateProjectResponse,
            EmptyFieldsResponse,
            TemplateProjectResponse,
            UnmarkTemplateResponse);
        try
        {
            var result = await RunCliAsync(directory, server, "--on-conflict", "update");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(5, server.RequestBodies.Count);
            Assert.True(IsUnmarkTemplateMutation(server.RequestBodies[^1]));
            Assert.DoesNotContain(server.RequestBodies, IsMarkTemplateMutation);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_reports_a_template_restore_failure_on_stderr_and_fails_the_run()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-template-restore-fail-" + Guid.NewGuid().ToString("N"));
        var snapshot = SnapshotWithStatusUpdates();
        await SnapshotFile.SaveAsync(
            snapshot with { Project = snapshot.Project with { Template = true } },
            directory,
            cancellationToken);

        using var server = new GraphQlStubServer(
            ExistingProjectResponse,
            UpdateProjectResponse,
            EmptyFieldsResponse,
            TemplateProjectResponse,
            UnmarkTemplateResponse,
            CreateStatusUpdateResponse,
            TemplateMutationErrorResponse);
        try
        {
            var result = await RunCliAsync(directory, server, "--on-conflict", "update");

            // The status update itself was written before the restore failed.
            Assert.Single(server.RequestBodies, request =>
                request.Contains("createProjectV2StatusUpdate", StringComparison.Ordinal));
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("Template restore is not permitted", result.Error, StringComparison.Ordinal);

            // The finally-path retry reports the dedicated restore diagnostic.
            Assert.Contains(
                "error: failed to restore the target project's template state:",
                result.Error,
                StringComparison.Ordinal);

            // Both restore attempts are mark mutations; nothing else follows them.
            Assert.Equal(2, server.RequestBodies.Count(IsMarkTemplateMutation));
            Assert.True(IsMarkTemplateMutation(server.RequestBodies[^1]));
            var importLog = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(importLog);
            Assert.Single(importLog.StatusUpdates);
            Assert.True(importLog.TemplateRestorationRequired);

            // The stdout contract is never partially emitted: the failure happens before
            // the summary block, so no result/items/status-updates/views line is printed.
            Assert.Equal(string.Empty, result.Output);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_restores_a_pending_template_even_when_project_resume_fails_first()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-template-early-fail-" + Guid.NewGuid().ToString("N"));
        var snapshot = SnapshotWithStatusUpdates();
        await SnapshotFile.SaveAsync(snapshot, directory, cancellationToken);
        await new ImportLog
        {
            ProjectId = "PVT_existing",
            SourceSnapshotFingerprint = ImportLog.ComputeSnapshotFingerprint(snapshot),
            TemplateRestorationRequired = true,
        }.SaveAsync(directory, cancellationToken);

        using var server = new GraphQlStubServer(
            NonTemplateProjectResponse,
            TemplateMutationErrorResponse,
            MarkTemplateResponse);
        try
        {
            var result = await RunCliAsync(directory, server);

            Assert.Equal(1, result.ExitCode);
            Assert.Single(server.RequestBodies, IsMarkTemplateMutation);
            Assert.DoesNotContain(server.RequestBodies, IsUnmarkTemplateMutation);
            Assert.True(IsMarkTemplateMutation(server.RequestBodies[^1]));

            var importLog = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(importLog);
            Assert.False(importLog.TemplateRestorationRequired);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static bool IsMarkTemplateMutation(string requestBody)
        => requestBody.Contains("markProjectV2AsTemplate", StringComparison.Ordinal)
            && !requestBody.Contains("unmarkProjectV2AsTemplate", StringComparison.Ordinal);

    private static bool IsUnmarkTemplateMutation(string requestBody)
        => requestBody.Contains("unmarkProjectV2AsTemplate", StringComparison.Ordinal);

    private static async Task<(int ExitCode, string Output, string Error)> RunCliAsync(
        string directory,
        GraphQlStubServer server,
        params string[] additionalArguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ghpmv.dll"));
        foreach (var argument in new[]
        {
            "import",
            "--org", "target",
            "--in", directory,
            "--token", "dummy-token",
            "--target-base-url", server.GraphQlUrl,
            "--no-update-check",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var argument in additionalArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the ghpmv process.");
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        return (process.ExitCode, await output, await error);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunVerifyCliAsync(
        string directory,
        GraphQlStubServer server,
        params string[] additionalArguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ghpmv.dll"),
            "verify",
            "--org", "target",
            "--project", "42",
            "--in", directory,
            "--token", "dummy-token",
            "--target-base-url", server.GraphQlUrl,
            "--no-update-check",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var argument in additionalArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the ghpmv process.");
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        return (process.ExitCode, await output, await error);
    }

    private static ProjectSnapshot MinimalSnapshot() => new()
    {
        SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
        Project = new ProjectInfoSnapshot
        {
            Title = "Roadmap",
            Public = false,
            Closed = false,
        },
        Fields = [],
        Views = [],
        Workflows = [],
        Items = [],
    };

    private static ProjectSnapshot SnapshotWithDownstreamContent() => new()
    {
        SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
        Project = new ProjectInfoSnapshot
        {
            Title = "Roadmap",
            Public = false,
            Closed = false,
        },
        Fields = [],
        Views =
        [
            new ViewSnapshot
            {
                Number = 1,
                Name = "Table",
                Layout = "TABLE_LAYOUT",
                Filter = "assignee:old-user",
                GroupByFields = [],
                SortByFields = [],
                VerticalGroupByFields = [],
                VisibleFields = [],
            },
        ],
        Workflows =
        [
            new WorkflowSnapshot
            {
                Number = 1,
                Name = "Item added to project",
                Enabled = true,
            },
        ],
        Items =
        [
            new ItemSnapshot
            {
                Type = "DRAFT_ISSUE",
                Position = 0,
                IsArchived = false,
                Draft = new DraftIssueSnapshot
                {
                    Title = "Must not be imported",
                    Assignees = [],
                },
                FieldValues = [],
            },
        ],
    };

    private static ProjectSnapshot VerifySnapshot() => MinimalSnapshot() with
    {
        Collaborators = null,
        LinkedRepositories = [],
        LinkedTeams = [],
    };

    private static ProjectSnapshot SnapshotWithStatusUpdates() => MinimalSnapshot() with
    {
        StatusUpdates =
        [
            new StatusUpdateSnapshot
            {
                Body = "Kickoff complete.",
                Status = "ON_TRACK",
                StartDate = "2024-01-01",
                TargetDate = "2024-03-31",
                Creator = "octocat",
                CreatedAt = "2024-01-05T09:00:00Z",
                UpdatedAt = "2024-01-06T09:00:00Z",
            },
        ],
    };

    private static ProjectSnapshot SnapshotWithTemplate(bool template)
    {
        var snapshot = MinimalSnapshot();
        return snapshot with { Project = snapshot.Project with { Template = template } };
    }

    private const string ExistingProjectResponse =
        """
        {"data":{"organization":{"projectsV2":{
          "nodes":[{"id":"PVT_existing","number":42,"title":"Roadmap","url":"https://github.com/orgs/target/projects/42"}],
          "pageInfo":{"hasNextPage":false,"endCursor":null}
        }}}}
        """;

    private const string EmptyProjectsResponse =
        """
        {"data":{"organization":{"projectsV2":{
          "nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}
        }}}}
        """;

    private const string OwnerResponse =
        """{"data":{"organization":{"id":"O_target"}}}""";

    private const string CreateProjectResponse =
        """{"data":{"createProjectV2":{"projectV2":{"id":"PVT_created","number":42,"title":"Roadmap","url":"https://github.com/orgs/target/projects/42","public":false}}}}""";

    private const string CreatedProjectLookupResponse =
        """
        {"data":{"organization":{"projectsV2":{
          "nodes":[{"id":"PVT_created","number":42,"title":"Roadmap","url":"https://github.com/orgs/target/projects/42"}],
          "pageInfo":{"hasNextPage":false,"endCursor":null}
        }}}}
        """;

    private const string UpdateCreatedProjectResponse =
        """{"data":{"updateProjectV2":{"projectV2":{"id":"PVT_created"}}}}""";

    private const string UpdateProjectResponse =
        """{"data":{"updateProjectV2":{"projectV2":{"id":"PVT_existing"}}}}""";

    private const string EmptyFieldsResponse =
        """{"data":{"node":{"fields":{"nodes":[]}}}}""";

    private const string PositionResponse =
        """{"data":{"updateProjectV2ItemPosition":{"clientMutationId":"position"}}}""";

    private const string TemplateProjectResponse =
        """{"data":{"node":{"id":"PVT_existing","template":true}}}""";

    private const string NonTemplateProjectResponse =
        """{"data":{"node":{"id":"PVT_existing","template":false}}}""";

    private const string UnmarkTemplateResponse =
        """{"data":{"unmarkProjectV2AsTemplate":{"projectV2":{"id":"PVT_existing","template":false}}}}""";

    private const string MarkTemplateResponse =
        """{"data":{"markProjectV2AsTemplate":{"projectV2":{"id":"PVT_existing","template":true}}}}""";

    private const string TemplateMutationErrorResponse =
        """{"errors":[{"type":"FORBIDDEN","message":"Template restore is not permitted."}]}""";

    private const string CreateStatusUpdateResponse =
        """{"data":{"createProjectV2StatusUpdate":{"statusUpdate":{"id":"PVTSU_imported"}}}}""";

    private const string VerifyProjectResponse =
        """
        {"data":{"organization":{"projectV2":{
          "title":"Roadmap","shortDescription":null,"readme":null,"public":false,"closed":false,"template":false,
          "views":{"nodes":[]},"workflows":{"nodes":[]},
          "repositories":{"nodes":[{"nameWithOwner":"target/extra"}]}
        }}}}
        """;

    private const string VerifyItemsResponse =
        """
        {"data":{"organization":{"projectV2":{
          "items":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}
        }}}}
        """;

    private const string VerifyFieldsResponse =
        """
        {"data":{"organization":{"projectV2":{
          "fields":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}
        }}}}
        """;

    private const string VerifyStatusUpdatesResponse =
        """
        {"data":{"organization":{"projectV2":{
          "statusUpdates":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}
        }}}}
        """;

    private const string VerifyTeamsResponse =
        """{"data":{"organization":{"projectV2":{"teams":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}}""";

    private sealed class GraphQlStubServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly string[] _responses;
        private readonly Task _serverTask;

        public GraphQlStubServer(params string[] responses)
        {
            _responses = responses;
            using var portReservation = new TcpListener(IPAddress.Loopback, 0);
            portReservation.Start();
            var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
            portReservation.Stop();

            var prefix = $"http://127.0.0.1:{port}/";
            GraphQlUrl = prefix + "graphql";
            _listener.Prefixes.Add(prefix);
            _listener.Start();
            _serverTask = ServeAsync(_cancellation.Token);
        }

        public string GraphQlUrl { get; }

        public List<string> RequestBodies { get; } = [];

        public void Dispose()
        {
            _cancellation.Cancel();
            _listener.Close();
            try
            {
                _serverTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }

            _cancellation.Dispose();
        }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
                }
                catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                RequestBodies.Add(await reader.ReadToEndAsync(cancellationToken));

                var responseIndex = Math.Min(RequestBodies.Count - 1, _responses.Length - 1);
                var response = Encoding.UTF8.GetBytes(_responses[responseIndex]);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = response.Length;
                await context.Response.OutputStream.WriteAsync(response, cancellationToken);
                context.Response.Close();
            }
        }
    }
}
