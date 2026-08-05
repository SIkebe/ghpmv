using System.Net;
using System.Text;
using System.Text.Json;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Tests;

public class ProjectImporterResumeTests
{
    [Fact]
    public async Task Ambiguous_project_create_is_adopted_without_resending()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-project-resume-").FullName;
        try
        {
            using var handler = new ProjectResumeHandler(directory);
            using var client = CreateClient(handler);
            var prewriteCount = 0;
            var importer = new ProjectImporter(client)
            {
                OperationLogDirectory = directory,
                BeforeWriteAsync = _ =>
                {
                    prewriteCount++;
                    return Task.CompletedTask;
                },
            };

            await Assert.ThrowsAsync<AmbiguousMutationResultException>(
                () => importer.ImportAsync(Snapshot(), "target", cancellationToken));

            var pending = await ProjectImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(pending.PendingProject);
            Assert.Equal(pending.PendingProject.OperationId, handler.ClientMutationId);
            Assert.True(handler.PendingWasPresentAtMutation);

            handler.Resume = true;
            var result = await importer.ImportAsync(Snapshot(), "target", cancellationToken);

            Assert.Equal("PVT_created", result.ProjectId);
            Assert.Equal(1, handler.CreateMutationCount);
            Assert.Equal(2, prewriteCount);
            var completed = await ProjectImportLog.LoadAsync(directory, cancellationToken);
            Assert.Null(completed.PendingProject);
            Assert.Equal("PVT_created", completed.CreatedProjectId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Strict_reservation_does_not_adopt_unrecorded_ambiguous_project_candidate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-strict-project-resume-").FullName;
        try
        {
            await new ProjectImportLog
            {
                PendingProject = new PendingProjectOperation
                {
                    OperationId = "ambiguous-project",
                    OwnerLogin = "target",
                    Title = "Project",
                    ExistingProjectIds = [],
                },
            }.SaveAsync(directory, cancellationToken);
            using var handler = new ProjectResumeHandler(directory) { Resume = true };
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => importer.ReserveProjectAsync("target", "Project", cancellationToken));

            Assert.Contains("no recorded Project ID", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, handler.CreateMutationCount);
            Assert.NotNull((await ProjectImportLog.LoadAsync(directory, cancellationToken)).PendingProject);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Recorded_project_id_is_not_replaced_by_same_title_candidate_on_resume()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-recorded-project-replacement-").FullName;
        try
        {
            await new ProjectImportLog
            {
                CreatedProjectId = "PVT_created",
                PendingProject = new PendingProjectOperation
                {
                    OperationId = "recorded-project",
                    OwnerLogin = "target",
                    Title = "Project",
                    ExistingProjectIds = [],
                },
            }.SaveAsync(directory, cancellationToken);
            using var handler = new ProjectResumeHandler(directory)
            {
                Resume = true,
                ReturnReplacementOnResume = true,
            };
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => importer.ImportAsync(Snapshot(), "target", cancellationToken));

            Assert.Contains("PVT_created", exception.Message, StringComparison.Ordinal);
            Assert.Contains("was not found", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, handler.CreateMutationCount);
            var log = await ProjectImportLog.LoadAsync(directory, cancellationToken);
            Assert.Equal("PVT_created", log.CreatedProjectId);
            Assert.NotNull(log.PendingProject);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Owned_project_update_resets_completed_state_before_metadata_mutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-owned-project-update-").FullName;
        try
        {
            await new ProjectImportLog
            {
                CreatedProjectId = "PVT_created",
                ImportCompleted = true,
            }.SaveAsync(directory, cancellationToken);
            using var handler = new ProjectResumeHandler(directory)
            {
                Resume = true,
                FailFirstProjectUpdate = true,
            };
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client)
            {
                OperationLogDirectory = directory,
                OnConflict = ConflictAction.Update,
            };

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => importer.ImportAsync(Snapshot(), "target", cancellationToken));

            Assert.False((await ProjectImportLog.LoadAsync(directory, cancellationToken)).ImportCompleted);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Strict_reservation_resumes_recorded_project_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-strict-recorded-project-").FullName;
        try
        {
            await new ProjectImportLog
            {
                CreatedProjectId = "PVT_created",
                PendingProject = new PendingProjectOperation
                {
                    OperationId = "recorded-project",
                    OwnerLogin = "target",
                    Title = "Project",
                    ExistingProjectIds = [],
                },
            }.SaveAsync(directory, cancellationToken);
            using var handler = new ProjectResumeHandler(directory) { Resume = true };
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            var created = await importer.ReserveProjectAsync("target", "Project", cancellationToken);

            Assert.False(created);
            var completed = await ProjectImportLog.LoadAsync(directory, cancellationToken);
            Assert.Equal("PVT_created", completed.CreatedProjectId);
            Assert.Null(completed.PendingProject);
            Assert.Equal(0, handler.CreateMutationCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Strict_reservation_compensates_when_duplicate_appears_after_create()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-strict-project-race-").FullName;
        try
        {
            using var handler = new ProjectResumeHandler(directory)
            {
                CreateSucceeds = true,
                ReturnDuplicateAfterCreate = true,
            };
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => importer.ReserveProjectAsync("target", "Project", cancellationToken));

            Assert.Contains("unrelated same-title Project", exception.Message, StringComparison.Ordinal);
            Assert.Equal(1, handler.CreateMutationCount);
            Assert.Equal(["PVT_created"], handler.DeletedProjectIds);
            var log = await ProjectImportLog.LoadAsync(directory, cancellationToken);
            Assert.Null(log.CreatedProjectId);
            Assert.Null(log.PendingProjectDeletionId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Strict_reservation_compensates_when_created_project_is_not_visible()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-strict-project-visibility-").FullName;
        try
        {
            using var handler = new ProjectResumeHandler(directory)
            {
                CreateSucceeds = true,
                HideCreatedAfterCreate = true,
            };
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => importer.ReserveProjectAsync("target", "Project", cancellationToken));

            Assert.Contains("was not visible", exception.Message, StringComparison.Ordinal);
            Assert.Equal(["PVT_created"], handler.DeletedProjectIds);
            Assert.Null((await ProjectImportLog.LoadAsync(directory, cancellationToken)).CreatedProjectId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Strict_reservation_refuses_compensation_when_durable_project_id_changed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-strict-project-cas-").FullName;
        try
        {
            using var handler = new ProjectResumeHandler(directory)
            {
                CreateSucceeds = true,
                ReturnDuplicateAfterCreate = true,
                ReplaceDurableProjectIdAfterCreate = true,
            };
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => importer.ReserveProjectAsync("target", "Project", cancellationToken));

            Assert.Contains(
                "changed from 'PVT_created' to 'PVT_other'",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Empty(handler.DeletedProjectIds);
            Assert.Equal(
                "PVT_other",
                (await ProjectImportLog.LoadAsync(directory, cancellationToken)).CreatedProjectId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Project_created_before_apply_failure_reuses_default_view_on_resume()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-created-project-resume-").FullName;
        try
        {
            using var handler = new ProjectResumeHandler(directory)
            {
                CreateSucceeds = true,
                FailFirstProjectUpdate = true,
            };
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };
            var snapshot = Snapshot() with
            {
                Views =
                [
                    new ViewSnapshot
                    {
                        Number = 1,
                        Name = "Backlog",
                        Layout = "TABLE_LAYOUT",
                        Filter = null,
                        GroupByFields = [],
                        SortByFields = [],
                        VerticalGroupByFields = [],
                        VisibleFields = [],
                    },
                ],
            };

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => importer.ImportAsync(snapshot, "target", cancellationToken));
            Assert.NotNull((await ProjectImportLog.LoadAsync(directory, cancellationToken)).PendingProject);

            handler.Resume = true;
            var result = await importer.ImportAsync(snapshot, "target", cancellationToken);

            Assert.Equal("PVT_created", result.ProjectId);
            Assert.Equal("PVTV_default", handler.UpdatedViewId);
            Assert.Equal(0, handler.ViewCreateMutationCount);
            Assert.Null((await ProjectImportLog.LoadAsync(directory, cancellationToken)).PendingProject);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Ambiguous_field_create_is_adopted_without_resending()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-field-resume-").FullName;
        try
        {
            using var handler = new FieldResumeHandler(directory);
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            await Assert.ThrowsAsync<AmbiguousMutationResultException>(
                () => importer.ImportIntoAsync(Snapshot(withField: true), "target", 7, cancellationToken));

            var pending = await ProjectImportLog.LoadAsync(directory, cancellationToken);
            var operation = Assert.Single(pending.PendingFields).Value;
            Assert.Equal(operation.OperationId, handler.ClientMutationId);
            Assert.True(handler.PendingWasPresentAtMutation);

            handler.Resume = true;
            var result = await importer.ImportIntoAsync(Snapshot(withField: true), "target", 7, cancellationToken);

            Assert.Equal("PVTF_created", result.FieldIds["Custom"]);
            Assert.Equal(1, handler.CreateMutationCount);
            Assert.Empty((await ProjectImportLog.LoadAsync(directory, cancellationToken)).PendingFields);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_into_rejects_pending_project_before_mutating_selected_project()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-project-target-").FullName;
        try
        {
            var log = new ProjectImportLog
            {
                PendingProject = new PendingProjectOperation
                {
                    OperationId = "pending-project",
                    OwnerLogin = "target",
                    Title = "Project",
                    ExistingProjectIds = [],
                },
            };
            await log.SaveAsync(directory, cancellationToken);
            using var handler = new FieldResumeHandler(directory);
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => importer.ImportIntoAsync(Snapshot(), "target", 7, cancellationToken));

            Assert.Contains("pending project operation", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_into_rejects_incomplete_owned_project_mismatch_before_mutating()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-owned-project-target-").FullName;
        try
        {
            await new ProjectImportLog
            {
                CreatedProjectId = "PVT_owned",
                ImportCompleted = false,
            }.SaveAsync(directory, cancellationToken);
            using var handler = new FieldResumeHandler(directory);
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => importer.ImportIntoAsync(Snapshot(), "target", 7, cancellationToken));

            Assert.Contains("incomplete import for project 'PVT_owned'", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, handler.ProjectUpdateMutationCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_into_owned_project_resets_completed_state_before_metadata_mutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-owned-project-import-into-").FullName;
        try
        {
            await new ProjectImportLog
            {
                CreatedProjectId = "PVT_existing",
                ImportCompleted = true,
            }.SaveAsync(directory, cancellationToken);
            using var handler = new FieldResumeHandler(directory) { FailProjectUpdate = true };
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => importer.ImportIntoAsync(Snapshot(), "target", 7, cancellationToken));

            Assert.False((await ProjectImportLog.LoadAsync(directory, cancellationToken)).ImportCompleted);
            Assert.Equal(1, handler.ProjectUpdateMutationCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_into_authentication_failure_preserves_completed_state_before_metadata_mutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-owned-project-import-into-auth-").FullName;
        try
        {
            await new ProjectImportLog
            {
                CreatedProjectId = "PVT_existing",
                ImportCompleted = true,
            }.SaveAsync(directory, cancellationToken);
            using var handler = new FieldResumeHandler(directory);
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client)
            {
                OperationLogDirectory = directory,
                BeforeWriteAsync = _ => throw new InvalidOperationException("authentication failed"),
            };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => importer.ImportIntoAsync(Snapshot(), "target", 7, cancellationToken));

            Assert.Equal("authentication failed", exception.Message);
            Assert.True((await ProjectImportLog.LoadAsync(directory, cancellationToken)).ImportCompleted);
            Assert.Equal(0, handler.ProjectUpdateMutationCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_into_rejects_pending_field_omitted_from_snapshot_before_mutating()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-field-target-").FullName;
        try
        {
            var log = new ProjectImportLog();
            log.PendingFields["Custom"] = new PendingFieldOperation
            {
                OperationId = "pending-field",
                ProjectId = "PVT_existing",
                Name = "Custom",
                DataType = "TEXT",
                ExistingFieldIds = [],
            };
            await log.SaveAsync(directory, cancellationToken);
            using var handler = new FieldResumeHandler(directory);
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => importer.ImportIntoAsync(Snapshot(), "target", 7, cancellationToken));

            Assert.Contains("does not match", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Field_reconciliation_rejects_multiple_same_named_candidates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-field-duplicates-").FullName;
        try
        {
            using var handler = new FieldResumeHandler(directory);
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            await Assert.ThrowsAsync<AmbiguousMutationResultException>(
                () => importer.ImportIntoAsync(Snapshot(withField: true), "target", 7, cancellationToken));

            handler.Resume = true;
            handler.ReturnDuplicateFields = true;
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => importer.ImportIntoAsync(Snapshot(withField: true), "target", 7, cancellationToken));

            Assert.Contains("multiple new fields", exception.Message, StringComparison.Ordinal);
            Assert.Equal(1, handler.CreateMutationCount);
            Assert.Single((await ProjectImportLog.LoadAsync(directory, cancellationToken)).PendingFields);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Ambiguous_issue_field_create_is_adopted_without_resending()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-issue-field-resume-").FullName;
        try
        {
            using var handler = new IssueFieldResumeHandler(directory, ambiguousFieldCreate: true);
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            await Assert.ThrowsAsync<AmbiguousMutationResultException>(
                () => importer.ImportIntoAsync(IssueFieldSnapshot(), "target", 7, cancellationToken));

            Assert.Single((await ProjectImportLog.LoadAsync(directory, cancellationToken)).PendingIssueFields);
            Assert.True(handler.PendingWasPresentAtMutation);

            handler.Resume = true;
            var resumedSnapshot = IssueFieldSnapshot(includeSameNamedProjectField: true);
            resumedSnapshot = resumedSnapshot with
            {
                Fields = resumedSnapshot.Fields.Select(field => field.IssueField is null
                    ? field
                    : field with
                    {
                        Options =
                        [
                            .. field.Options!,
                            new SingleSelectOptionSnapshot { Id = "source-sdk", Name = "SDK", Color = "GREEN" },
                        ],
                        IssueField = field.IssueField with { Description = "Updated teams" },
                    }).ToArray(),
            };
            var result = await importer.ImportIntoAsync(
                resumedSnapshot,
                "target",
                7,
                cancellationToken);

            Assert.Equal("IFM_created", result.IssueFieldIds["Teams"]);
            Assert.Equal("PVTF_normal_teams", result.FieldIds["Teams"]);
            Assert.Equal(1, handler.IssueFieldCreateMutationCount);
            Assert.Equal(1, handler.IssueFieldUpdateMutationCount);
            Assert.Equal("IFO_sdk", result.IssueFieldOptionIds["Teams"]["SDK"]);
            Assert.Empty((await ProjectImportLog.LoadAsync(directory, cancellationToken)).PendingIssueFields);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Issue_field_reconciliation_rejects_multiple_candidates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-issue-field-duplicates-").FullName;
        try
        {
            using var handler = new IssueFieldResumeHandler(directory, ambiguousFieldCreate: true);
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            await Assert.ThrowsAsync<AmbiguousMutationResultException>(
                () => importer.ImportIntoAsync(IssueFieldSnapshot(), "target", 7, cancellationToken));

            handler.Resume = true;
            handler.ReturnDuplicates = true;
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => importer.ImportIntoAsync(IssueFieldSnapshot(), "target", 7, cancellationToken));

            Assert.Contains("multiple new Issue Fields", exception.Message, StringComparison.Ordinal);
            Assert.Equal(1, handler.IssueFieldCreateMutationCount);
            Assert.Single((await ProjectImportLog.LoadAsync(directory, cancellationToken)).PendingIssueFields);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Ambiguous_issue_field_link_is_resumed_with_an_idempotent_mutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-issue-field-link-resume-").FullName;
        try
        {
            using var handler = new IssueFieldResumeHandler(directory, ambiguousFieldCreate: false);
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            await Assert.ThrowsAsync<AmbiguousMutationResultException>(
                () => importer.ImportIntoAsync(IssueFieldSnapshot(), "target", 7, cancellationToken));

            Assert.Single((await ProjectImportLog.LoadAsync(directory, cancellationToken)).PendingIssueFieldLinks);
            Assert.True(handler.PendingWasPresentAtMutation);

            handler.Resume = true;
            var result = await importer.ImportIntoAsync(IssueFieldSnapshot(), "target", 7, cancellationToken);

            Assert.False(result.FieldIds.ContainsKey("Teams"));
            Assert.Equal(2, handler.LinkCreateMutationCount);
            Assert.Empty((await ProjectImportLog.LoadAsync(directory, cancellationToken)).PendingIssueFieldLinks);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Issue_field_link_resume_does_not_depend_on_broken_field_enumeration()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-issue-field-link-duplicates-").FullName;
        try
        {
            using var handler = new IssueFieldResumeHandler(directory, ambiguousFieldCreate: false);
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            await Assert.ThrowsAsync<AmbiguousMutationResultException>(
                () => importer.ImportIntoAsync(IssueFieldSnapshot(), "target", 7, cancellationToken));

            handler.Resume = true;
            handler.ReturnDuplicates = true;
            var result = await importer.ImportIntoAsync(IssueFieldSnapshot(), "target", 7, cancellationToken);

            Assert.False(result.FieldIds.ContainsKey("Teams"));
            Assert.Equal(2, handler.LinkCreateMutationCount);
            Assert.Empty((await ProjectImportLog.LoadAsync(directory, cancellationToken)).PendingIssueFieldLinks);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static GitHubGraphQLClient CreateClient(HttpMessageHandler handler)
        => new("token", new Uri("https://example.test/graphql"), handler, (_, _) => Task.CompletedTask);

    private static ProjectSnapshot Snapshot(bool withField = false) => new()
    {
        SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
        Project = new ProjectInfoSnapshot { Title = "Project", Public = false, Closed = false },
        Fields = withField ? [new FieldSnapshot { Name = "Custom", DataType = "TEXT" }] : [],
        Views = [],
        Workflows = [],
        Items = [],
    };

    private static ProjectSnapshot IssueFieldSnapshot(bool includeSameNamedProjectField = false)
    {
        List<FieldSnapshot> fields = [];
        if (includeSameNamedProjectField)
        {
            fields.Add(new FieldSnapshot { Name = "Teams", DataType = "TEXT" });
        }

        fields.Add(
            new FieldSnapshot
            {
                Name = "Teams",
                DataType = "MULTI_SELECT",
                Options =
                [
                    new SingleSelectOptionSnapshot { Id = "source-platform", Name = "Platform", Color = "PURPLE" },
                ],
                IssueField = new IssueFieldConfigurationSnapshot
                {
                    Description = "Teams involved",
                    Visibility = "ALL",
                },
            });

        return Snapshot() with { Fields = fields };
    }

    private abstract class ResumeHandler(string directory) : HttpMessageHandler
    {
        public bool Resume { get; set; }

        public bool PendingWasPresentAtMutation { get; protected set; }

        public string? ClientMutationId { get; protected set; }

        public int CreateMutationCount { get; protected set; }

        protected string Directory { get; } = directory;

        protected static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        protected static async Task<(string Query, JsonElement Variables)> ReadAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            return (
                document.RootElement.GetProperty("query").GetString() ?? string.Empty,
                document.RootElement.GetProperty("variables").Clone());
        }
    }

    private sealed class ProjectResumeHandler(string directory) : ResumeHandler(directory)
    {
        public bool CreateSucceeds { get; init; }

        public bool FailFirstProjectUpdate { get; init; }

        public bool ReturnDuplicateAfterCreate { get; init; }

        public bool HideCreatedAfterCreate { get; init; }

        public bool ReplaceDurableProjectIdAfterCreate { get; init; }

        public bool ReturnReplacementOnResume { get; init; }

        public List<string> DeletedProjectIds { get; } = [];

        public int ViewCreateMutationCount { get; private set; }

        public string? UpdatedViewId { get; private set; }

        private bool _projectUpdateFailed;
        private bool _durableProjectIdReplaced;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var (query, variables) = await ReadAsync(request, cancellationToken);
            if (query.Contains("projectsV2(first:", StringComparison.Ordinal))
            {
                if (CreateMutationCount > 0 && ReplaceDurableProjectIdAfterCreate && !_durableProjectIdReplaced)
                {
                    var log = await ProjectImportLog.LoadAsync(Directory, cancellationToken);
                    log.CreatedProjectId = "PVT_other";
                    await log.SaveAsync(Directory, cancellationToken);
                    _durableProjectIdReplaced = true;
                }

                if (ReturnDuplicateAfterCreate && CreateMutationCount > 0)
                {
                    return Json(
                        """
                        {"data":{"organization":{"projectsV2":{"nodes":[
                          {"id":"PVT_created","number":7,"title":"Project","url":"https://github.com/orgs/target/projects/7"},
                          {"id":"PVT_other","number":8,"title":"Project","url":"https://github.com/orgs/target/projects/8"}
                        ],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                        """);
                }

                if (HideCreatedAfterCreate && CreateMutationCount > 0)
                {
                    return Json("""{"data":{"organization":{"projectsV2":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}""");
                }

                if (!Resume)
                {
                    return Json("""{"data":{"organization":{"projectsV2":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}""");
                }

                return ReturnReplacementOnResume
                    ? Json("""{"data":{"organization":{"projectsV2":{"nodes":[{"id":"PVT_replacement","number":8,"title":"Project","url":"https://github.com/orgs/target/projects/8"}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}""")
                    : Json("""{"data":{"organization":{"projectsV2":{"nodes":[{"id":"PVT_created","number":7,"title":"Project","url":"https://github.com/orgs/target/projects/7"}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}""");
            }

            if (query.Contains("organization(login:", StringComparison.Ordinal))
            {
                return Json("""{"data":{"organization":{"id":"O_target"}}}""");
            }

            if (query.Contains("createProjectV2(input:", StringComparison.Ordinal))
            {
                CreateMutationCount++;
                var log = await ProjectImportLog.LoadAsync(Directory, cancellationToken);
                PendingWasPresentAtMutation = log.PendingProject is not null;
                ClientMutationId = variables.GetProperty("clientMutationId").GetString();
                if (CreateSucceeds)
                {
                    return Json("""{"data":{"createProjectV2":{"projectV2":{"id":"PVT_created","number":7,"title":"Project","url":"https://github.com/orgs/target/projects/7"}}}}""");
                }

                throw new HttpRequestException("Response ended prematurely.");
            }

            if (query.Contains("deleteProjectV2", StringComparison.Ordinal))
            {
                DeletedProjectIds.Add(variables.GetProperty("projectId").GetString() ?? string.Empty);
                return Json("""{"data":{"deleteProjectV2":{"projectV2":{"id":"PVT_created"}}}}""");
            }

            if (query.Contains("updateProjectV2(input:", StringComparison.Ordinal))
            {
                if (FailFirstProjectUpdate && !_projectUpdateFailed)
                {
                    _projectUpdateFailed = true;
                    throw new InvalidOperationException("Apply failed after project creation.");
                }

                return Json("""{"data":{"updateProjectV2":{"projectV2":{"id":"PVT_created"}}}}""");
            }

            if (query.Contains("fields(first:", StringComparison.Ordinal))
            {
                return Json("""{"data":{"node":{"fields":{"nodes":[]}}}}""");
            }

            if (query.Contains("views(first:", StringComparison.Ordinal))
            {
                return Json("""{"data":{"node":{"views":{"nodes":[{"id":"PVTV_default","number":1,"name":"View 1","layout":"TABLE_LAYOUT"}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}""");
            }

            if (query.Contains("createProjectV2View", StringComparison.Ordinal))
            {
                ViewCreateMutationCount++;
                return Json("""{"data":{"createProjectV2View":{"projectV2View":{"id":"PVTV_created","number":2,"name":"Backlog","layout":"TABLE_LAYOUT"}}}}""");
            }

            if (query.Contains("updateProjectV2View", StringComparison.Ordinal))
            {
                UpdatedViewId = variables.GetProperty("viewId").GetString();
                return Json("""{"data":{"updateProjectV2View":{"projectV2View":{"id":"PVTV_default","number":1,"name":"Backlog","layout":"TABLE_LAYOUT"}}}}""");
            }

            throw new InvalidOperationException($"Unexpected operation: {query}");
        }
    }

    private sealed class FieldResumeHandler(string directory) : ResumeHandler(directory)
    {
        public bool ReturnDuplicateFields { get; set; }

        public bool FailProjectUpdate { get; init; }

        public int ProjectUpdateMutationCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var (query, variables) = await ReadAsync(request, cancellationToken);
            if (query.Contains("projectV2(number:", StringComparison.Ordinal))
            {
                return Json("""{"data":{"organization":{"projectV2":{"id":"PVT_existing","number":7,"title":"Project","url":"https://github.com/orgs/target/projects/7"}}}}""");
            }

            if (query.Contains("updateProjectV2", StringComparison.Ordinal))
            {
                ProjectUpdateMutationCount++;
                if (FailProjectUpdate)
                {
                    throw new InvalidOperationException("Apply failed during project update.");
                }

                return Json("""{"data":{"updateProjectV2":{"projectV2":{"id":"PVT_existing"}}}}""");
            }

            if (query.Contains("fields(first:", StringComparison.Ordinal))
            {
                if (!Resume)
                {
                    return Json("""{"data":{"node":{"fields":{"nodes":[]}}}}""");
                }

                return ReturnDuplicateFields
                    ? Json("""{"data":{"node":{"fields":{"nodes":[{"id":"PVTF_created_1","name":"Custom","dataType":"TEXT"},{"id":"PVTF_created_2","name":"Custom","dataType":"TEXT"}]}}}}""")
                    : Json("""{"data":{"node":{"fields":{"nodes":[{"id":"PVTF_created","name":"Custom","dataType":"TEXT"}]}}}}""");
            }

            if (query.Contains("createProjectV2Field", StringComparison.Ordinal))
            {
                CreateMutationCount++;
                var log = await ProjectImportLog.LoadAsync(Directory, cancellationToken);
                PendingWasPresentAtMutation = log.PendingFields.Count == 1;
                ClientMutationId = variables.GetProperty("clientMutationId").GetString();
                throw new HttpRequestException("Response ended prematurely.");
            }

            throw new InvalidOperationException($"Unexpected operation: {query}");
        }
    }

    private sealed class IssueFieldResumeHandler(string directory, bool ambiguousFieldCreate) : ResumeHandler(directory)
    {
        public bool ReturnDuplicates { get; set; }

        public int IssueFieldCreateMutationCount { get; private set; }

        public int IssueFieldUpdateMutationCount { get; private set; }

        public int LinkCreateMutationCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var (query, _) = await ReadAsync(request, cancellationToken);
            if (query.Contains("projectV2(number:", StringComparison.Ordinal))
            {
                return Json("""{"data":{"organization":{"projectV2":{"id":"PVT_existing","number":7,"title":"Project","url":"https://github.com/orgs/target/projects/7"}}}}""");
            }

            if (query.Contains("updateProjectV2", StringComparison.Ordinal))
            {
                return Json("""{"data":{"updateProjectV2":{"projectV2":{"id":"PVT_existing"}}}}""");
            }

            if (query.Contains("fields(first:", StringComparison.Ordinal))
            {
                if (!Resume)
                {
                    return Json("""{"data":{"node":{"fields":{"nodes":[]}}}}""");
                }

                if (ambiguousFieldCreate)
                {
                    return Json("""{"data":{"node":{"fields":{"nodes":[{"__typename":"ProjectV2Field","id":"PVTF_normal_teams","name":"Teams","dataType":"TEXT"}]}}}}""");
                }

                return ReturnDuplicates
                    ? Json("""{"data":{"node":{"fields":{"nodes":[{"__typename":"ProjectV2Field","id":"PVTF_created_1","name":"Teams"},{"__typename":"ProjectV2Field","id":"PVTF_created_2","name":"Teams"}]}}}}""")
                    : Json("""{"data":{"node":{"fields":{"nodes":[{"__typename":"ProjectV2Field","id":"PVTF_created","name":"Teams"}]}}}}""");
            }

            if (query.Contains("field(name:", StringComparison.Ordinal))
            {
                return ambiguousFieldCreate && Resume
                    ? Json("""{"data":{"node":{"field":{"__typename":"ProjectV2Field","id":"PVTF_normal_teams","name":"Teams"}}}}""")
                    : Json("""{"data":{"node":{"field":null}}}""");
            }

            if (query.Contains("nodes(ids:", StringComparison.Ordinal))
            {
                return ambiguousFieldCreate && Resume
                    ? Json("""{"data":{"nodes":[{"id":"PVTF_normal_teams","dataType":"TEXT"}]}}""")
                    : Json("""{"data":{"nodes":[null]},"errors":[{"message":"Something went wrong while executing your query on the preview API."}]}""");
            }

            if (query.Contains("issueFields(first:", StringComparison.Ordinal))
            {
                if (ambiguousFieldCreate && !Resume)
                {
                    return Json("""{"data":{"organization":{"issueFields":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}""");
                }

                var nodes = ReturnDuplicates && ambiguousFieldCreate
                    ? IssueFieldNode("IFM_created_1") + "," + IssueFieldNode("IFM_created_2")
                    : IssueFieldNode("IFM_created");
                return Json(string.Concat(
                    """{"data":{"organization":{"issueFields":{"nodes":[""",
                    nodes,
                    """],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}"""));
            }

            if (query.Contains("createIssueField(", StringComparison.Ordinal))
            {
                IssueFieldCreateMutationCount++;
                var log = await ProjectImportLog.LoadAsync(Directory, cancellationToken);
                PendingWasPresentAtMutation = log.PendingIssueFields.Count == 1;
                throw new HttpRequestException("Response ended prematurely.");
            }

            if (query.Contains("updateIssueField(", StringComparison.Ordinal))
            {
                IssueFieldUpdateMutationCount++;
                return Json(
                    """
                    {"data":{"updateIssueField":{"issueField":{
                      "__typename":"IssueFieldMultiSelect","id":"IFM_created","name":"Teams",
                      "dataType":"MULTI_SELECT","description":"Updated teams","visibility":"ALL",
                      "options":[
                        {"id":"IFO_platform","name":"Platform","color":"PURPLE","description":null},
                        {"id":"IFO_sdk","name":"SDK","color":"GREEN","description":null}
                      ]
                    }}}}
                    """);
            }

            if (query.Contains("createProjectV2IssueField(", StringComparison.Ordinal))
            {
                LinkCreateMutationCount++;
                if (!ambiguousFieldCreate && !Resume)
                {
                    var log = await ProjectImportLog.LoadAsync(Directory, cancellationToken);
                    PendingWasPresentAtMutation = log.PendingIssueFieldLinks.Count == 1;
                    throw new HttpRequestException("Response ended prematurely.");
                }

                return Json("""{"data":{"createProjectV2IssueField":{"clientMutationId":"link-operation"}}}""");
            }

            if (query.Contains("organization(login:", StringComparison.Ordinal))
            {
                return Json("""{"data":{"organization":{"id":"O_target"}}}""");
            }

            throw new InvalidOperationException($"Unexpected operation: {query}");
        }

        private static string IssueFieldNode(string id)
            => $$"""{"__typename":"IssueFieldMultiSelect","id":"{{id}}","name":"Teams","dataType":"MULTI_SELECT","description":"Teams involved","visibility":"ALL","options":[{"id":"IFO_platform","name":"Platform","color":"PURPLE","description":null}]}""";
    }
}
