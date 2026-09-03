using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Ghpmv.Core.Fixtures;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Tests;

public class FixtureProjectBuilderTests
{
    [Fact]
    public void Concurrent_fixture_ui_operation_is_rejected()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-fixture-ui-lock-").FullName;
        try
        {
            using var operationLock = FixtureUiOperation.AcquireLock(directory);

            var exception = Assert.Throws<InvalidOperationException>(
                () => FixtureUiOperation.AcquireLock(directory));

            Assert.Contains("Another fixture UI operation", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("7\nsnapshot-a", 7, "snapshot-a", true)]
    [InlineData("7", 7, "snapshot-a", false)]
    [InlineData("7\nsnapshot-b", 7, "snapshot-a", false)]
    [InlineData("8\nsnapshot-a", 7, "snapshot-a", false)]
    public void Fixture_ui_completion_marker_requires_the_expected_project_and_snapshot(
        string marker,
        int projectNumber,
        string snapshotFingerprint,
        bool expected)
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-fixture-ui-marker-").FullName;
        try
        {
            var completionPath = Path.Combine(directory, "fixture-ui-complete");
            File.WriteAllText(completionPath, marker);

            Assert.Equal(
                expected,
                FixtureUiOperation.IsCompleted(completionPath, projectNumber, snapshotFingerprint));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Fixture_ui_completion_marker_is_published_without_temporary_files()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-fixture-ui-marker-").FullName;
        try
        {
            var completionPath = Path.Combine(directory, "fixture-ui-complete");

            await FixtureUiOperation.MarkCompletedAsync(
                completionPath,
                projectNumber: 7,
                snapshotFingerprint: "snapshot-a",
                TestContext.Current.CancellationToken);

            Assert.True(FixtureUiOperation.IsCompleted(
                completionPath,
                projectNumber: 7,
                snapshotFingerprint: "snapshot-a"));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Concurrent_fixture_operation_is_rejected_before_network_writes(bool requireNewResources)
    {
        var logRoot = Directory.CreateTempSubdirectory("ghpmv-fixture-lock-").FullName;
        try
        {
            var operationDirectory = GetOperationDirectory(logRoot, "example", "Fixture", "fixture");
            Directory.CreateDirectory(operationDirectory);
            using var operationLock = new FileStream(
                Path.Combine(operationDirectory, "fixture-operation.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            using var graphQlHandler = new RecordingHandler();
            using var restHandler = new RecordingHandler();
            using var graphQl = new GitHubGraphQLClient(
                "token",
                baseUrl: null,
                graphQlHandler,
                (_, _) => Task.CompletedTask);
            using var rest = new GitHubRestClient("token", baseUri: null, restHandler);
            var builder = new FixtureProjectBuilder(graphQl, rest)
            {
                OperationLogDirectory = logRoot,
                RequireNewResources = requireNewResources,
            };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => builder.CreateAsync(
                    "Example",
                    "Fixture",
                    "Fixture",
                    TestContext.Current.CancellationToken));

            Assert.Contains("Another fixture operation", exception.Message, StringComparison.Ordinal);
            Assert.Empty(graphQlHandler.RequestBodies);
            Assert.Empty(restHandler.RequestMethods);
        }
        finally
        {
            Directory.Delete(logRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Concurrent_fixture_repository_operation_with_different_title_is_rejected_before_network_writes()
    {
        var logRoot = Directory.CreateTempSubdirectory("ghpmv-fixture-repository-lock-").FullName;
        try
        {
            var repositoryLockPath = GetRepositoryLockPath(logRoot, "example/fixture");
            Directory.CreateDirectory(Path.GetDirectoryName(repositoryLockPath)!);
            using var repositoryLock = new FileStream(
                repositoryLockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            using var graphQlHandler = new RecordingHandler();
            using var restHandler = new RecordingHandler();
            using var graphQl = new GitHubGraphQLClient(
                "token",
                baseUrl: null,
                graphQlHandler,
                (_, _) => Task.CompletedTask);
            using var rest = new GitHubRestClient("token", baseUri: null, restHandler);
            var builder = new FixtureProjectBuilder(graphQl, rest)
            {
                OperationLogDirectory = logRoot,
                RequireNewResources = true,
                AllowExistingEmptyRepository = true,
            };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => builder.CreateAsync(
                    "Example",
                    "Different Fixture",
                    "Fixture",
                    TestContext.Current.CancellationToken));

            Assert.Contains("repository 'example/fixture'", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(graphQlHandler.RequestBodies);
            Assert.Empty(restHandler.RequestMethods);
        }
        finally
        {
            Directory.Delete(logRoot, recursive: true);
        }
    }

    [Fact]
    public void Completed_owned_fixture_skips_implicit_ui_setup()
    {
        var result = new FixtureProjectSetupResult(1, "https://github.com/orgs/example/projects/1", Created: false, OwnedByOperation: true);

        Assert.True(result.ShouldSkipUiSetup(projectExplicitlySelected: false, uiSetupCompleted: true));
    }

    [Fact]
    public void Incomplete_owned_fixture_allows_ui_setup_retry()
    {
        var result = new FixtureProjectSetupResult(1, "https://github.com/orgs/example/projects/1", Created: false, OwnedByOperation: true);

        Assert.False(result.ShouldSkipUiSetup(projectExplicitlySelected: false, uiSetupCompleted: false));
    }

    [Fact]
    public void Explicit_project_allows_completed_ui_setup_reapply()
    {
        var result = new FixtureProjectSetupResult(1, "https://github.com/orgs/example/projects/1", Created: false, OwnedByOperation: true);

        Assert.False(result.ShouldSkipUiSetup(projectExplicitlySelected: true, uiSetupCompleted: true));
    }

    [Fact]
    public void Three_argument_result_constructor_preserves_unowned_existing_behavior()
    {
        var result = new FixtureProjectSetupResult(1, "https://github.com/orgs/example/projects/1", Created: false);

        Assert.False(result.OwnedByOperation);
        Assert.True(result.ShouldSkipUiSetup(projectExplicitlySelected: false, uiSetupCompleted: false));
    }

    [Fact]
    public void Completed_operation_requires_no_fixture_writes()
    {
        var projectLog = new ProjectImportLog
        {
            CreatedProjectId = "PVT_1",
            ImportCompleted = true,
        };
        var itemLog = new ImportLog
        {
            ProjectId = "PVT_1",
            SourceSnapshotFingerprint = "fingerprint",
        };

        Assert.True(FixtureProjectBuilder.IsCompletedOperation(projectLog, itemLog));
    }

    [Fact]
    public void Pending_operation_still_requires_fixture_writes()
    {
        var projectLog = new ProjectImportLog
        {
            CreatedProjectId = "PVT_1",
            ImportCompleted = true,
            PendingViews =
            {
                [1] = new PendingViewOperation
                {
                    OperationId = "view",
                    ProjectId = "PVT_1",
                    SourceNumber = 1,
                    Name = "View",
                    Layout = "TABLE_LAYOUT",
                    ExistingViewIds = [],
                },
            },
        };
        var itemLog = new ImportLog
        {
            ProjectId = "PVT_1",
            SourceSnapshotFingerprint = "fingerprint",
        };

        Assert.False(FixtureProjectBuilder.IsCompletedOperation(projectLog, itemLog));
    }

    [Fact]
    public async Task Completed_operation_revalidates_snapshot_before_shortcut()
    {
        var logRoot = Directory.CreateTempSubdirectory("ghpmv-fixture-completed-revalidate-").FullName;
        try
        {
            var operationDirectory = GetOperationDirectory(logRoot, "example", "Fixture", "fixture");
            Directory.CreateDirectory(operationDirectory);
            await File.WriteAllLinesAsync(
                Path.Combine(operationDirectory, "fixture-repository.txt"),
                ["https://api.github.com", "example/fixture", "claimed", "1"],
                TestContext.Current.CancellationToken);
            await new ProjectImportLog
            {
                CreatedProjectId = "PVT_1",
                ImportCompleted = true,
            }.SaveAsync(operationDirectory, TestContext.Current.CancellationToken);
            await new ImportLog
            {
                ProjectId = "PVT_1",
                SourceSnapshotFingerprint = ImportLog.ComputeSnapshotFingerprint(
                    FixtureProjectBuilder.CreateSnapshot("Fixture", "example/fixture", "original-user", 2)),
            }.SaveAsync(operationDirectory, TestContext.Current.CancellationToken);
            using var graphQlHandler = new RecordingHandler(
                JsonResponse(
                    """
                    {"data":{"organization":{"projectsV2":{"nodes":[{"id":"PVT_1","number":1,"title":"Fixture","url":"https://github.com/orgs/example/projects/1"}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                    """),
                JsonResponse("""{"data":{"viewer":{"login":"different-user"}}}"""));
            using var restHandler = new RecordingHandler(
                JsonResponse("""{"id":1,"name":"fixture","private":true}"""),
                JsonResponse("""[{"number":2}]"""));
            using var graphQl = new GitHubGraphQLClient("token", baseUrl: null, graphQlHandler, (_, _) => Task.CompletedTask);
            using var rest = new GitHubRestClient("token", baseUri: null, restHandler);
            var builder = CreateRequireNewBuilder(graphQl, rest, operationLogDirectory: logRoot);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                builder.CreateAsync("example", "Fixture", "fixture", TestContext.Current.CancellationToken));

            Assert.Contains("different fixture snapshot", exception.Message, StringComparison.Ordinal);
            Assert.Equal([HttpMethod.Get, HttpMethod.Get], restHandler.RequestMethods);
            Assert.DoesNotContain(graphQlHandler.RequestBodies, body => body.Contains("mutation", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(logRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Successful_fixture_stages_mark_the_operation_complete()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-fixture-complete-").FullName;
        try
        {
            await new ProjectImportLog
            {
                CreatedProjectId = "PVT_1",
                ImportCompleted = false,
                HasUnresolvedWarnings = false,
            }.SaveAsync(directory, TestContext.Current.CancellationToken);

            var changed = await FixtureProjectBuilder.MarkOperationCompletedAsync(
                directory,
                warningCount: 0,
                TestContext.Current.CancellationToken);

            Assert.True(changed);
            Assert.True((await ProjectImportLog.LoadAsync(
                directory,
                TestContext.Current.CancellationToken)).ImportCompleted);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Fixture_item_warnings_keep_the_operation_incomplete()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-fixture-warning-").FullName;
        try
        {
            await new ProjectImportLog
            {
                CreatedProjectId = "PVT_1",
                ImportCompleted = false,
                HasUnresolvedWarnings = false,
            }.SaveAsync(directory, TestContext.Current.CancellationToken);

            var changed = await FixtureProjectBuilder.MarkOperationCompletedAsync(
                directory,
                warningCount: 1,
                TestContext.Current.CancellationToken);

            Assert.False(changed);
            var warningLog = await ProjectImportLog.LoadAsync(
                directory,
                TestContext.Current.CancellationToken);
            Assert.False(warningLog.ImportCompleted);
            Assert.True(warningLog.HasUnresolvedWarnings);

            changed = await FixtureProjectBuilder.MarkOperationCompletedAsync(
                directory,
                warningCount: 0,
                TestContext.Current.CancellationToken);

            Assert.False(changed);
            var retriedLog = await ProjectImportLog.LoadAsync(
                directory,
                TestContext.Current.CancellationToken);
            Assert.False(retriedLog.ImportCompleted);
            Assert.True(retriedLog.HasUnresolvedWarnings);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Demo_fixture_exercises_every_snapshot_field_pattern()
    {
        var snapshot = FixtureProjectBuilder.CreateSnapshot(
            "Fixture",
            "example/fixture",
            "octocat",
            pullRequestNumber: 2);
        var values = snapshot.Items.SelectMany(item => item.FieldValues).ToList();

        Assert.True(snapshot.Project.Template);
        foreach (var property in typeof(FieldValueSnapshot).GetProperties()
                     .Where(property => property.Name != nameof(FieldValueSnapshot.FieldName)))
        {
            Assert.Contains(values, value => property.GetValue(value) is not null);
        }

        foreach (var property in typeof(FieldSnapshot).GetProperties()
                     .Where(property => property.Name is not nameof(FieldSnapshot.Name)
                         and not nameof(FieldSnapshot.DataType)
                         and not nameof(FieldSnapshot.DefaultValue)))
        {
            Assert.Contains(snapshot.Fields, field => property.GetValue(field) is not null);
        }
    }

    [Fact]
    public void Demo_fixture_positions_project_dates_and_iterations_around_the_reference_week()
    {
        var snapshot = FixtureProjectBuilder.CreateSnapshot(
            "Fixture",
            "example/fixture",
            "octocat",
            pullRequestNumber: 2,
            referenceDate: new DateOnly(2026, 8, 17));

        var iterationField = Assert.Single(snapshot.Fields, field => field.Name == "Fixture Sprint");
        Assert.NotNull(iterationField.IterationConfiguration);
        Assert.Equal(
            ["2026-07-20", "2026-08-17", "2026-08-31", "2026-09-14", "2026-09-28"],
            iterationField.IterationConfiguration.CompletedIterations!
                .Concat(iterationField.IterationConfiguration.Iterations!)
                .Select(iteration => iteration.StartDate));

        Assert.Equal(
            ["2026-07-27", "2026-08-21", "2026-09-12"],
            snapshot.Items
                .Where(item => item.Draft?.Title is "Fixture draft 1" or "Fixture draft 2" or "Fixture draft 3")
                .Select(item => Assert.Single(item.FieldValues, value => value.FieldName == "Fixture Date").Date));
    }

    [Fact]
    public void Demo_fixture_includes_distinct_empty_Board_columns()
    {
        var snapshot = FixtureProjectBuilder.CreateSnapshot(
            "Fixture",
            "example/fixture",
            "octocat",
            pullRequestNumber: 2);

        var selectField = Assert.Single(snapshot.Fields, field => field.Name == "Fixture Select");
        Assert.Contains(selectField.Options!, option => option.Name == "Delta");
        var sprintField = Assert.Single(snapshot.Fields, field => field.Name == "Fixture Sprint");
        Assert.Contains(
            sprintField.IterationConfiguration!.Iterations!,
            iteration => iteration.Title == "Sprint 4");
        Assert.DoesNotContain(
            snapshot.Items.SelectMany(item => item.FieldValues),
            value => value.SingleSelectOptionName == "Delta" || value.IterationTitle == "Sprint 4");
    }

    [Fact]
    public async Task Dynamic_fixture_reference_date_is_persisted_across_retries()
    {
        var operationDirectory = Directory.CreateTempSubdirectory("ghpmv-fixture-reference-date-").FullName;
        try
        {
            var first = await FixtureProjectBuilder.ResolveFixtureReferenceDateAsync(
                operationDirectory,
                useCurrentWeek: true,
                currentDate: new DateOnly(2026, 8, 20),
                TestContext.Current.CancellationToken);
            var retry = await FixtureProjectBuilder.ResolveFixtureReferenceDateAsync(
                operationDirectory,
                useCurrentWeek: true,
                currentDate: new DateOnly(2026, 9, 7),
                TestContext.Current.CancellationToken);

            Assert.Equal(new DateOnly(2026, 8, 17), first);
            Assert.Equal(first, retry);
            Assert.Equal(
                "2026-08-17",
                await File.ReadAllTextAsync(
                    Path.Combine(operationDirectory, "fixture-reference-date"),
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(operationDirectory, recursive: true);
        }
    }

    [Fact]
    public void New_e2e_fixture_adds_a_long_dated_roadmap_item_without_changing_the_shared_fixture()
    {
        var snapshot = FixtureProjectBuilder.CreateSnapshot(
            "Fixture",
            "example/fixture",
            "octocat",
            pullRequestNumber: 2,
            referenceDate: new DateOnly(2026, 8, 17));

        var extended = FixtureProjectBuilder.AddRoadmapRenderingItem(snapshot);
        var item = Assert.Single(
            extended.Items,
            candidate => candidate.Draft?.Title == FixtureProjectBuilder.RoadmapLongTitle);

        Assert.Equal(snapshot.Items.Count + 1, extended.Items.Count);
        Assert.DoesNotContain(
            snapshot.Items,
            candidate => candidate.Draft?.Title == FixtureProjectBuilder.RoadmapLongTitle);
        Assert.Contains(item.FieldValues, value => value.FieldName == "Fixture Date" && value.Date is not null);
        Assert.Contains(item.FieldValues, value => value.FieldName == "Fixture Sprint" && value.IterationTitle is not null);
        Assert.Equal(2, extended.Items.Count(candidate => candidate.FieldValues.Any(value =>
            value.FieldName == "Fixture Select"
            && value.SingleSelectOptionName == "Alpha")));
        Assert.Equal(2, extended.Items.Count(candidate => candidate.FieldValues.Any(value =>
            value.FieldName == "Fixture Sprint"
            && value.IterationTitle == "Sprint 0")));
        Assert.True(FixtureProjectBuilder.IsRoadmapRenderingItem(item));
        Assert.False(FixtureProjectBuilder.IsRoadmapRenderingItem(item with { IsArchived = true }));
        Assert.False(FixtureProjectBuilder.IsRoadmapRenderingItem(item with
        {
            FieldValues = item.FieldValues.Where(value => value.FieldName != "Fixture Date").ToList(),
        }));
    }

    [Fact]
    public void Demo_fixture_normalizes_repository_identity_for_case_insensitive_retries()
    {
        var mixedCase = FixtureProjectBuilder.CreateSnapshot(
            "Fixture",
            "Example/Fixture",
            "octocat",
            pullRequestNumber: 2);
        var lowerCase = FixtureProjectBuilder.CreateSnapshot(
            "Fixture",
            "example/fixture",
            "octocat",
            pullRequestNumber: 2);

        Assert.Equal(
            ImportLog.ComputeSnapshotFingerprint(lowerCase),
            ImportLog.ComputeSnapshotFingerprint(mixedCase));
        Assert.Equal(["example/fixture"], mixedCase.LinkedRepositories);
        Assert.All(
            mixedCase.Items.Where(item => item.Repository is not null),
            item => Assert.Equal("example/fixture", item.Repository));
    }

    [Fact]
    public void Demo_fixture_puts_multi_select_values_on_a_real_issue()
    {
        var snapshot = FixtureProjectBuilder.CreateSnapshot(
            "Fixture",
            "example/fixture",
            "octocat",
            pullRequestNumber: 2);

        var field = Assert.Single(snapshot.Fields, field => field.Name == "Fixture Teams");
        Assert.Equal("MULTI_SELECT", field.DataType);
        Assert.NotNull(field.IssueField);
        Assert.Equal("ALL", field.IssueField.Visibility);
        Assert.Equal(["Platform", "SDK", "Docs"], field.Options!.Select(option => option.Name));

        var issue = Assert.Single(snapshot.Items, item => item.Type == "ISSUE");
        var value = Assert.Single(issue.FieldValues, value => value.FieldName == field.Name);
        Assert.Equal(["Platform", "SDK"], value.MultiSelectOptionNames);
    }

    [Fact]
    public void Demo_fixture_exercises_ordinary_project_multi_select_fields()
    {
        var snapshot = FixtureProjectBuilder.CreateSnapshot(
            "Fixture",
            "example/fixture",
            "octocat",
            pullRequestNumber: 2);

        var field = Assert.Single(snapshot.Fields, field => field.Name == "Fixture Areas");
        Assert.Equal("MULTI_SELECT", field.DataType);
        Assert.Null(field.IssueField);
        Assert.Equal(["Backend", "Frontend", "Operations"], field.Options!.Select(option => option.Name));

        var draft = Assert.Single(snapshot.Items, item => item.Draft?.Title == "Fixture draft 1");
        var value = Assert.Single(draft.FieldValues, value => value.FieldName == field.Name);
        Assert.Equal(false, value.IsIssueField);
        Assert.Equal(["Backend", "Frontend"], value.MultiSelectOptionNames);
    }

    [Fact]
    public void Demo_fixture_can_include_a_dedicated_team_link()
    {
        var snapshot = FixtureProjectBuilder.CreateSnapshot(
            "Fixture",
            "example/fixture",
            "octocat",
            pullRequestNumber: 2,
            teamSlug: "ghpmv-fixture-team");

        Assert.Equal("example/ghpmv-fixture-team", Assert.Single(snapshot.LinkedTeams!).Identity);
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, false, false)]
    public void Item_stage_runs_only_for_new_or_resumable_fixture(
        bool projectAlreadyExists,
        bool hasItemWork,
        bool projectImportWasPending,
        bool expected)
    {
        Assert.Equal(
            expected,
            FixtureProjectBuilder.ShouldImportItems(
                projectAlreadyExists,
                hasItemWork,
                projectImportWasPending));
    }

    [Fact]
    public void Status_only_import_log_does_not_resume_the_fixture_item_stage()
    {
        var log = new ImportLog
        {
            ProjectId = "PVT_fixture",
            SourceSnapshotFingerprint = "fingerprint",
        };
        log.StatusUpdates["0"] = "PVTSU_fixture";

        Assert.False(FixtureProjectBuilder.HasItemWork(log));
        Assert.False(FixtureProjectBuilder.ShouldImportItems(
            projectAlreadyExists: true,
            hasItemWork: FixtureProjectBuilder.HasItemWork(log),
            projectImportWasPending: false));
    }

    [Fact]
    public void Fixture_status_history_matching_finds_complete_ordered_subsequence_among_unrelated_entries()
    {
        var expected = FixtureStatusUpdates();
        var actual = expected.Select((update, index) => new FixtureProjectBuilder.FixtureStatusUpdate(
            $"PVTSU_fixture_{index}",
            update with
            {
                Creator = "server-user",
                CreatedAt = "2026-08-16T00:00:00Z",
                UpdatedAt = "2026-08-16T00:01:00Z",
            })).ToList();
        actual.Insert(0, Unrelated(expected, "PVTSU_unrelated_newer"));
        actual.Add(Unrelated(expected, "PVTSU_unrelated_older"));

        var reconciliation = FixtureProjectBuilder.ReconcileFixtureStatusUpdates(
            expected,
            actual,
            log: null);
        var matches = reconciliation.CanonicalMatches;

        Assert.False(reconciliation.ImportRequired);
        Assert.Equal(expected.Count, matches.Count);
        Assert.Equal(
            Enumerable.Range(0, expected.Count),
            matches.Keys.Order());
        Assert.DoesNotContain("PVTSU_unrelated_newer", matches.Values);
        Assert.DoesNotContain("PVTSU_unrelated_older", matches.Values);
    }

    [Fact]
    public void Fixture_status_history_matching_accepts_a_created_prefix_with_unrelated_entries_anywhere()
    {
        var expected = FixtureStatusUpdates();
        var actual = new[]
        {
            Unrelated(expected, "PVTSU_unrelated_newer"),
            new FixtureProjectBuilder.FixtureStatusUpdate("PVTSU_existing_3", expected[3]),
            Unrelated(expected, "PVTSU_unrelated_interleaved"),
            new FixtureProjectBuilder.FixtureStatusUpdate("PVTSU_existing_4", expected[4]),
            Unrelated(expected, "PVTSU_unrelated_older"),
        };

        var matches = FixtureProjectBuilder.MatchFixtureStatusUpdates(expected, actual);

        Assert.Equal([3, 4], matches.Keys.Order());
        Assert.Equal("PVTSU_existing_3", matches[3]);
        Assert.Equal("PVTSU_existing_4", matches[4]);
        Assert.DoesNotContain(matches.Values, id => id.StartsWith("PVTSU_unrelated", StringComparison.Ordinal));
    }

    [Fact]
    public void Fixture_status_history_matching_fails_closed_for_reversed_oldest_and_newest_entries()
    {
        var expected = FixtureStatusUpdates();
        var actual = new[]
        {
            Existing(expected, 4, "oldest"),
            Existing(expected, 0, "newest"),
        };

        AssertUnsafeFixtureHistory(expected, actual);
    }

    [Fact]
    public void Fixture_status_reconciliation_uses_actual_incomplete_prefix_instead_of_completed_log()
    {
        var expected = FixtureStatusUpdates();
        var log = FixtureLog(expected);
        var actual = new[]
        {
            Unrelated(expected, "PVTSU_newer"),
            Existing(expected, 3, "next"),
            Unrelated(expected, "PVTSU_interleaved"),
            Existing(expected, 4, "oldest"),
        };

        var reconciliation = FixtureProjectBuilder.ReconcileFixtureStatusUpdates(expected, actual, log);

        Assert.True(reconciliation.ImportRequired);
        Assert.True(reconciliation.LogChanged);
        Assert.Equal(["3", "4"], log.StatusUpdates.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("PVTSU_next", log.StatusUpdates["3"]);
        Assert.Equal("PVTSU_oldest", log.StatusUpdates["4"]);
    }

    [Fact]
    public void Fixture_status_reconciliation_validates_interrupted_history_even_when_log_claims_completion()
    {
        var expected = FixtureStatusUpdates();
        var log = FixtureLog(expected);
        var actual = new[]
        {
            Existing(expected, 2, "middle"),
            Existing(expected, 4, "oldest"),
        };

        Assert.Throws<InvalidOperationException>(
            () => FixtureProjectBuilder.ReconcileFixtureStatusUpdates(expected, actual, log));
        Assert.Equal(expected.Count, log.StatusUpdates.Count);
    }

    [Fact]
    public void Fixture_status_history_matching_fails_closed_for_a_hole_in_the_created_prefix()
    {
        var expected = FixtureStatusUpdates();
        var actual = new[]
        {
            Existing(expected, 2, "middle"),
            Existing(expected, 4, "oldest"),
        };

        AssertUnsafeFixtureHistory(expected, actual);
    }

    [Fact]
    public void Fixture_status_history_matching_fails_closed_when_a_later_entry_exists_without_the_earlier_prefix()
    {
        var expected = FixtureStatusUpdates();

        AssertUnsafeFixtureHistory(expected, [Existing(expected, 3, "later")]);
    }

    [Fact]
    public void Complete_fixture_history_with_legacy_duplicate_uses_one_canonical_id_and_requires_no_import()
    {
        var expected = FixtureStatusUpdates();
        var actual = expected
            .Select((update, index) => Existing(expected, index, $"canonical_{index}"))
            .ToList();
        actual.Insert(1, Existing(expected, 0, "legacy_duplicate"));

        var reconciliation = FixtureProjectBuilder.ReconcileFixtureStatusUpdates(
            expected,
            actual,
            log: null);

        Assert.False(reconciliation.ImportRequired);
        Assert.Equal(expected.Count, reconciliation.CanonicalMatches.Count);
        Assert.Equal("PVTSU_canonical_0", reconciliation.CanonicalMatches[0]);
        Assert.DoesNotContain("PVTSU_legacy_duplicate", reconciliation.CanonicalMatches.Values);
    }

    [Fact]
    public void Fixture_status_reconciliation_does_not_claim_an_exact_match_for_a_pending_create()
    {
        var expected = FixtureStatusUpdates();
        var log = FixtureLog(expected);
        log.StatusUpdates.Remove("0");
        log.PendingStatusUpdates["0"] = new PendingStatusUpdateOperation
        {
            OperationId = "ambiguous-operation",
            ProjectId = log.ProjectId,
        };
        var actual = expected
            .Select((update, index) => Existing(expected, index, $"canonical_{index}"))
            .ToArray();

        var reconciliation = FixtureProjectBuilder.ReconcileFixtureStatusUpdates(expected, actual, log);

        Assert.True(reconciliation.ImportRequired);
        Assert.True(reconciliation.LogChanged);
        Assert.False(log.StatusUpdates.ContainsKey("0"));
        Assert.Equal("ambiguous-operation", log.PendingStatusUpdates["0"].OperationId);
        Assert.Equal(expected.Count - 1, log.StatusUpdates.Count);
    }

    private static FixtureProjectBuilder.FixtureStatusUpdate Existing(
        IReadOnlyList<StatusUpdateSnapshot> expected,
        int index,
        string suffix)
        => new($"PVTSU_{suffix}", expected[index]);

    private static FixtureProjectBuilder.FixtureStatusUpdate Unrelated(
        IReadOnlyList<StatusUpdateSnapshot> expected,
        string id)
        => new(id, expected[0] with { Body = $"Unrelated history: {id}" });

    private static ImportLog FixtureLog(IReadOnlyList<StatusUpdateSnapshot> expected)
    {
        var snapshot = FixtureProjectBuilder.CreateSnapshot(
            "Fixture",
            "example/fixture",
            "octocat",
            pullRequestNumber: 2);
        var log = new ImportLog
        {
            ProjectId = "PVT_fixture",
            SourceSnapshotFingerprint = ImportLog.ComputeSnapshotFingerprint(snapshot),
        };
        for (var index = 0; index < expected.Count; index++)
        {
            log.StatusUpdates[index.ToString(CultureInfo.InvariantCulture)] =
                $"PVTSU_logged_{index.ToString(CultureInfo.InvariantCulture)}";
        }

        return log;
    }

    private static void AssertUnsafeFixtureHistory(
        IReadOnlyList<StatusUpdateSnapshot> expected,
        IReadOnlyList<FixtureProjectBuilder.FixtureStatusUpdate> actual)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => FixtureProjectBuilder.MatchFixtureStatusUpdates(expected, actual));
        Assert.Contains("not an append-safe contiguous history", exception.Message, StringComparison.Ordinal);
        Assert.Contains("No status updates were changed", exception.Message, StringComparison.Ordinal);
    }

    private static IReadOnlyList<StatusUpdateSnapshot> FixtureStatusUpdates()
    {
        var snapshot = FixtureProjectBuilder.CreateSnapshot(
            "Fixture",
            "example/fixture",
            "octocat",
            pullRequestNumber: 2);

        Assert.NotNull(snapshot.StatusUpdates);
        return snapshot.StatusUpdates;
    }

    [Fact]
    public void Demo_fixture_exercises_every_status_update_status()
    {
        var updates = FixtureStatusUpdates();

        Assert.Equal(5, updates.Count);
        var statuses = updates.Select(update => update.Status).ToList();
        Assert.Equal(
            ["COMPLETE", "OFF_TRACK", "AT_RISK", "ON_TRACK", "INACTIVE"],
            statuses);
        Assert.Equal(statuses.Count, statuses.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Demo_fixture_status_updates_are_in_strictly_descending_created_at_order()
    {
        var updates = FixtureStatusUpdates();

        var timestamps = updates
            .Select(update => DateTimeOffset.Parse(update.CreatedAt, CultureInfo.InvariantCulture))
            .ToList();

        // Export order is reverse chronological (newest first), which the importer relies
        // on when it re-orders to oldest-first for creation.
        for (var index = 1; index < timestamps.Count; index++)
        {
            Assert.True(
                timestamps[index] < timestamps[index - 1],
                $"status update {index} ({updates[index].CreatedAt}) is not older than {updates[index - 1].CreatedAt}");
        }

        Assert.Equal(
            DateTimeOffset.Parse("2026-01-05T09:00:00Z", CultureInfo.InvariantCulture),
            timestamps[0]);
        Assert.Equal(
            DateTimeOffset.Parse("2026-01-01T09:00:00Z", CultureInfo.InvariantCulture),
            timestamps[^1]);
    }

    [Fact]
    public void Demo_fixture_status_updates_mix_null_and_populated_dates()
    {
        var updates = FixtureStatusUpdates();

        Assert.Contains(updates, update => update.StartDate is null);
        Assert.Contains(updates, update => update.TargetDate is null);

        var inactive = Assert.Single(updates, update => update.Status == "INACTIVE");
        Assert.Null(inactive.StartDate);
        Assert.Null(inactive.TargetDate);

        var complete = Assert.Single(updates, update => update.Status == "COMPLETE");
        Assert.Equal("2026-01-01", complete.StartDate);
        Assert.Equal("2026-04-15", complete.TargetDate);
    }

    [Fact]
    public void Demo_fixture_status_update_bodies_include_multi_line_and_markdown_content()
    {
        var updates = FixtureStatusUpdates();

        var multiLine = Assert.Single(updates, update => update.Status == "ON_TRACK");
        Assert.Contains("\n", multiLine.Body, StringComparison.Ordinal);

        var markdown = Assert.Single(updates, update => update.Status == "INACTIVE");
        Assert.Contains("**", markdown.Body, StringComparison.Ordinal);

        Assert.All(updates, update => Assert.False(string.IsNullOrWhiteSpace(update.Body)));
    }

    [Fact]
    public void Demo_fixture_status_updates_populate_every_snapshot_property_somewhere()
    {
        var updates = FixtureStatusUpdates();

        foreach (var property in typeof(StatusUpdateSnapshot).GetProperties())
        {
            Assert.Contains(updates, update => property.GetValue(update) is not null);
        }

        Assert.All(updates, update => Assert.Equal("octocat", update.Creator));
        Assert.Contains(
            updates,
            update => !string.Equals(update.UpdatedAt, update.CreatedAt, StringComparison.Ordinal));
    }

    [Fact]
    public void Require_new_resources_allows_only_an_explicitly_empty_repository()
    {
        FixtureProjectBuilder.ValidateRepositoryRequirement(
            "example/fixture",
            requireNewResources: true,
            allowExistingEmptyRepository: true,
            repositoryExists: true,
            repositoryIsEmpty: true);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            FixtureProjectBuilder.ValidateRepositoryRequirement(
                "example/fixture",
                requireNewResources: true,
                allowExistingEmptyRepository: true,
                repositoryExists: true,
                repositoryIsEmpty: false));
        Assert.Equal("Fixture repository 'example/fixture' is not empty.", exception.Message);
    }

    [Fact]
    public async Task Require_new_resources_rejects_existing_project_without_mutations()
    {
        using var graphQlHandler = new RecordingHandler(JsonResponse(
            """
            {"data":{"organization":{"projectsV2":{"nodes":[{"id":"PVT_1","number":1,"title":"Fixture","url":"https://github.com/orgs/example/projects/1"}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
            """));
        using var restHandler = new RecordingHandler();
        using var graphQl = new GitHubGraphQLClient("token", baseUrl: null, graphQlHandler, (_, _) => Task.CompletedTask);
        using var rest = new GitHubRestClient("token", baseUri: null, restHandler);
        var builder = CreateRequireNewBuilder(graphQl, rest);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.CreateAsync("example", "Fixture", "fixture", TestContext.Current.CancellationToken));

        Assert.DoesNotContain(graphQlHandler.RequestBodies, body => body.Contains("mutation", StringComparison.Ordinal));
        Assert.Empty(restHandler.RequestMethods);
    }

    [Fact]
    public async Task Require_new_resources_resumes_project_owned_by_operation()
    {
        var logRoot = Directory.CreateTempSubdirectory("ghpmv-fixture-project-resume-").FullName;
        try
        {
            var operationDirectory = GetOperationDirectory(logRoot, "example", "Fixture", "fixture");
            await new ProjectImportLog { CreatedProjectId = "PVT_1" }
                .SaveAsync(operationDirectory, TestContext.Current.CancellationToken);
            using var graphQlHandler = new RecordingHandler(
                JsonResponse(
                    """
                    {"data":{"organization":{"projectsV2":{"nodes":[{"id":"PVT_1","number":1,"title":"Fixture","url":"https://github.com/orgs/example/projects/1"}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                    """),
                JsonResponse("""{"data":{"viewer":{"login":"octocat"}}}"""));
            using var restHandler = new RecordingHandler(JsonResponse("""{"id":1,"name":"fixture"}"""));
            using var graphQl = new GitHubGraphQLClient("token", baseUrl: null, graphQlHandler, (_, _) => Task.CompletedTask);
            using var rest = new GitHubRestClient("token", baseUri: null, restHandler);
            var builder = CreateRequireNewBuilder(graphQl, rest, operationLogDirectory: logRoot);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                builder.CreateAsync("example", "Fixture", "fixture", TestContext.Current.CancellationToken));

            Assert.Equal("Fixture repository 'example/fixture' already exists.", exception.Message);
            Assert.DoesNotContain(graphQlHandler.RequestBodies, body => body.Contains("mutation", StringComparison.Ordinal));
            Assert.Equal([HttpMethod.Get], restHandler.RequestMethods);
        }
        finally
        {
            Directory.Delete(logRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Require_new_resources_rejects_unrelated_duplicate_of_owned_project()
    {
        var logRoot = Directory.CreateTempSubdirectory("ghpmv-fixture-project-duplicate-").FullName;
        try
        {
            var operationDirectory = GetOperationDirectory(logRoot, "example", "Fixture", "fixture");
            await new ProjectImportLog { CreatedProjectId = "PVT_owned" }
                .SaveAsync(operationDirectory, TestContext.Current.CancellationToken);
            using var graphQlHandler = new RecordingHandler(JsonResponse(
                """
                {"data":{"organization":{"projectsV2":{"nodes":[{"id":"PVT_other","number":2,"title":"Fixture","url":"https://github.com/orgs/example/projects/2"},{"id":"PVT_owned","number":1,"title":"Fixture","url":"https://github.com/orgs/example/projects/1"}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                """));
            using var restHandler = new RecordingHandler();
            using var graphQl = new GitHubGraphQLClient("token", baseUrl: null, graphQlHandler, (_, _) => Task.CompletedTask);
            using var rest = new GitHubRestClient("token", baseUri: null, restHandler);
            var builder = CreateRequireNewBuilder(graphQl, rest, operationLogDirectory: logRoot);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                builder.CreateAsync("example", "Fixture", "fixture", TestContext.Current.CancellationToken));

            Assert.Contains("unrelated same-title Project", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(graphQlHandler.RequestBodies, body => body.Contains("mutation", StringComparison.Ordinal));
            Assert.Empty(restHandler.RequestMethods);
        }
        finally
        {
            Directory.Delete(logRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Require_new_resources_rejects_duplicate_raced_after_initial_project_lookup()
    {
        var logRoot = Directory.CreateTempSubdirectory("ghpmv-fixture-project-race-").FullName;
        try
        {
            var operationDirectory = GetOperationDirectory(logRoot, "example", "Fixture", "fixture");
            await new ProjectImportLog { CreatedProjectId = "PVT_owned" }
                .SaveAsync(operationDirectory, TestContext.Current.CancellationToken);
            using var graphQlHandler = new RecordingHandler(
                JsonResponse(
                    """
                    {"data":{"organization":{"projectsV2":{"nodes":[{"id":"PVT_owned","number":1,"title":"Fixture","url":"https://github.com/orgs/example/projects/1"}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                    """),
                JsonResponse("""{"data":{"viewer":{"login":"octocat"}}}"""),
                JsonResponse(
                    """
                    {"data":{"organization":{"projectsV2":{"nodes":[{"id":"PVT_other","number":2,"title":"Fixture","url":"https://github.com/orgs/example/projects/2"},{"id":"PVT_owned","number":1,"title":"Fixture","url":"https://github.com/orgs/example/projects/1"}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                    """));
            using var restHandler = new RecordingHandler(NotFoundResponse());
            using var graphQl = new GitHubGraphQLClient("token", baseUrl: null, graphQlHandler, (_, _) => Task.CompletedTask);
            using var rest = new GitHubRestClient("token", baseUri: null, restHandler);
            var builder = CreateRequireNewBuilder(graphQl, rest, operationLogDirectory: logRoot);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                builder.CreateAsync("example", "Fixture", "fixture", TestContext.Current.CancellationToken));

            Assert.Contains("unrelated same-title Project", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(graphQlHandler.RequestBodies, body => body.Contains("mutation", StringComparison.Ordinal));
            Assert.Equal([HttpMethod.Get], restHandler.RequestMethods);
        }
        finally
        {
            Directory.Delete(logRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Require_new_resources_rejects_existing_repository_without_mutations()
    {
        using var graphQlHandler = new RecordingHandler(
            JsonResponse(
                """
                {"data":{"organization":{"projectsV2":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                """),
            JsonResponse("""{"data":{"viewer":{"login":"octocat"}}}"""));
        using var restHandler = new RecordingHandler(JsonResponse("""{"id":1,"name":"fixture"}"""));
        using var graphQl = new GitHubGraphQLClient("token", baseUrl: null, graphQlHandler, (_, _) => Task.CompletedTask);
        using var rest = new GitHubRestClient("token", baseUri: null, restHandler);
        var builder = CreateRequireNewBuilder(graphQl, rest);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.CreateAsync("example", "Fixture", "fixture", TestContext.Current.CancellationToken));

        Assert.DoesNotContain(graphQlHandler.RequestBodies, body => body.Contains("mutation", StringComparison.Ordinal));
        Assert.Equal([HttpMethod.Get], restHandler.RequestMethods);
    }

    [Theory]
    [InlineData(true, "[]")]
    [InlineData(false, """[{"number":1}]""")]
    public async Task Existing_nonempty_repository_is_rejected_without_mutations(
        bool hasContents,
        string issuesBody)
    {
        using var graphQlHandler = new RecordingHandler(
            JsonResponse(
                """
                {"data":{"organization":{"projectsV2":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                """),
            JsonResponse("""{"data":{"viewer":{"login":"octocat"}}}"""));
        using var restHandler = new RecordingHandler(
            JsonResponse("""{"id":1,"name":"fixture","private":true}"""),
            hasContents ? JsonResponse("""[{"name":"README.md"}]""") : NotFoundResponse(),
            JsonResponse(issuesBody));
        using var graphQl = new GitHubGraphQLClient("token", baseUrl: null, graphQlHandler, (_, _) => Task.CompletedTask);
        using var rest = new GitHubRestClient("token", baseUri: null, restHandler);
        var builder = CreateRequireNewBuilder(graphQl, rest, allowExistingEmptyRepository: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.CreateAsync("example", "Fixture", "fixture", TestContext.Current.CancellationToken));

        Assert.DoesNotContain(graphQlHandler.RequestBodies, body => body.Contains("mutation", StringComparison.Ordinal));
        Assert.Equal([HttpMethod.Get, HttpMethod.Get, HttpMethod.Get], restHandler.RequestMethods);
        Assert.Equal(
            ["/repos/example/fixture", "/repos/example/fixture/contents", "/repos/example/fixture/issues?state=all&per_page=1"],
            restHandler.RequestPaths);
    }

    [Fact]
    public async Task Existing_public_empty_repository_is_rejected_before_writes()
    {
        using var graphQlHandler = new RecordingHandler(
            JsonResponse(
                """
                {"data":{"organization":{"projectsV2":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                """),
            JsonResponse("""{"data":{"viewer":{"login":"octocat"}}}"""));
        using var restHandler = new RecordingHandler(
            JsonResponse("""{"id":1,"name":"fixture","private":false}"""));
        using var graphQl = new GitHubGraphQLClient("token", baseUrl: null, graphQlHandler, (_, _) => Task.CompletedTask);
        using var rest = new GitHubRestClient("token", baseUri: null, restHandler);
        var builder = CreateRequireNewBuilder(graphQl, rest, allowExistingEmptyRepository: true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.CreateAsync("example", "Fixture", "fixture", TestContext.Current.CancellationToken));

        Assert.Equal("Fixture repository 'example/fixture' must be private.", exception.Message);
        Assert.DoesNotContain(graphQlHandler.RequestBodies, body => body.Contains("mutation", StringComparison.Ordinal));
        Assert.Equal([HttpMethod.Get], restHandler.RequestMethods);
    }

    [Fact]
    public async Task Concurrent_project_title_is_rejected_before_repository_writes()
    {
        using var graphQlHandler = new RecordingHandler(
            JsonResponse(
                """
                {"data":{"organization":{"projectsV2":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                """),
            JsonResponse("""{"data":{"viewer":{"login":"octocat"}}}"""),
            JsonResponse(
                """
                {"data":{"organization":{"projectsV2":{"nodes":[{"id":"PVT_other","number":2,"title":"Fixture","url":"https://github.com/orgs/example/projects/2"}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                """));
        using var restHandler = new RecordingHandler(NotFoundResponse());
        using var graphQl = new GitHubGraphQLClient("token", baseUrl: null, graphQlHandler, (_, _) => Task.CompletedTask);
        using var rest = new GitHubRestClient("token", baseUri: null, restHandler);
        var builder = CreateRequireNewBuilder(graphQl, rest);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.CreateAsync("example", "Fixture", "fixture", TestContext.Current.CancellationToken));

        Assert.Contains("already exists", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(graphQlHandler.RequestBodies, body => body.Contains("mutation", StringComparison.Ordinal));
        Assert.Equal([HttpMethod.Get], restHandler.RequestMethods);
    }

    [Fact]
    public async Task Repository_creation_conflict_releases_project_reserved_by_this_call()
    {
        var logRoot = Directory.CreateTempSubdirectory("ghpmv-fixture-project-compensation-").FullName;
        try
        {
            using var graphQlHandler = new RecordingHandler(
                JsonResponse(
                    """
                    {"data":{"organization":{"projectsV2":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                    """),
                JsonResponse("""{"data":{"viewer":{"login":"octocat"}}}"""),
                JsonResponse(
                    """
                    {"data":{"organization":{"projectsV2":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                    """),
                JsonResponse("""{"data":{"organization":{"id":"O_example"}}}"""),
                JsonResponse(
                    """
                    {"data":{"createProjectV2":{"projectV2":{"id":"PVT_1","number":1,"title":"Fixture","url":"https://github.com/orgs/example/projects/1","public":false}}}}
                    """),
                JsonResponse(
                    """
                    {"data":{"organization":{"projectsV2":{"nodes":[{"id":"PVT_1","number":1,"title":"Fixture","url":"https://github.com/orgs/example/projects/1"}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                    """),
                JsonResponse("""{"data":{"deleteProjectV2":{"projectV2":{"id":"PVT_1"}}}}"""));
            using var restHandler = new RecordingHandler(
                NotFoundResponse(),
                ErrorResponse(HttpStatusCode.UnprocessableEntity));
            using var graphQl = new GitHubGraphQLClient("token", baseUrl: null, graphQlHandler, (_, _) => Task.CompletedTask);
            using var rest = new GitHubRestClient("token", baseUri: null, restHandler);
            var builder = CreateRequireNewBuilder(graphQl, rest, operationLogDirectory: logRoot);

            await Assert.ThrowsAsync<HttpRequestException>(() =>
                builder.CreateAsync("example", "Fixture", "fixture", TestContext.Current.CancellationToken));

            Assert.Equal([HttpMethod.Get, HttpMethod.Post], restHandler.RequestMethods);
            Assert.Contains(graphQlHandler.RequestBodies, body => body.Contains("createProjectV2(", StringComparison.Ordinal));
            Assert.Contains(graphQlHandler.RequestBodies, body => body.Contains("deleteProjectV2(", StringComparison.Ordinal));
            var operationDirectory = GetOperationDirectory(logRoot, "example", "Fixture", "fixture");
            var projectLog = await ProjectImportLog.LoadAsync(
                operationDirectory,
                TestContext.Current.CancellationToken);
            Assert.Null(projectLog.CreatedProjectId);
            Assert.False(File.Exists(Path.Combine(operationDirectory, "fixture-repository.txt")));
        }
        finally
        {
            Directory.Delete(logRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Ambiguous_project_release_clears_claim_when_exact_project_is_absent()
    {
        var logRoot = Directory.CreateTempSubdirectory("ghpmv-fixture-project-release-").FullName;
        try
        {
            await new ProjectImportLog { CreatedProjectId = "PVT_1" }
                .SaveAsync(logRoot, TestContext.Current.CancellationToken);
            using var graphQlHandler = new RecordingHandler(
                new HttpRequestException("response lost after delete"),
                JsonResponse("""{"data":{"node":null}}"""));
            using var graphQl = new GitHubGraphQLClient(
                "token",
                baseUrl: null,
                graphQlHandler,
                (_, _) => Task.CompletedTask);
            var importer = new ProjectImporter(graphQl)
            {
                OperationLogDirectory = logRoot,
            };

            await importer.ReleaseReservedProjectAsync(TestContext.Current.CancellationToken);

            var projectLog = await ProjectImportLog.LoadAsync(
                logRoot,
                TestContext.Current.CancellationToken);
            Assert.Null(projectLog.CreatedProjectId);
            Assert.Single(
                graphQlHandler.RequestBodies,
                body => body.Contains("deleteProjectV2(", StringComparison.Ordinal));
            Assert.Single(
                graphQlHandler.RequestBodies,
                body => body.Contains("node(id:", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(logRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Pending_view_prevents_project_release()
    {
        var logRoot = Directory.CreateTempSubdirectory("ghpmv-fixture-project-pending-view-").FullName;
        try
        {
            var log = new ProjectImportLog { CreatedProjectId = "PVT_1" };
            log.PendingViews[2] = new PendingViewOperation
            {
                OperationId = "pending-view",
                ProjectId = "PVT_1",
                SourceNumber = 2,
                Name = "Board",
                Layout = "BOARD_LAYOUT",
                ExistingViewIds = [],
            };
            await log.SaveAsync(logRoot, TestContext.Current.CancellationToken);
            using var graphQlHandler = new RecordingHandler();
            using var graphQl = new GitHubGraphQLClient(
                "token",
                baseUrl: null,
                graphQlHandler,
                (_, _) => Task.CompletedTask);
            var importer = new ProjectImporter(graphQl) { OperationLogDirectory = logRoot };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => importer.ReleaseReservedProjectAsync(TestContext.Current.CancellationToken));

            Assert.Contains("pending import operations", exception.Message, StringComparison.Ordinal);
            Assert.Empty(graphQlHandler.RequestBodies);
            var persisted = await ProjectImportLog.LoadAsync(
                logRoot,
                TestContext.Current.CancellationToken);
            Assert.Equal("PVT_1", persisted.CreatedProjectId);
            Assert.Single(persisted.PendingViews);
        }
        finally
        {
            Directory.Delete(logRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Pending_project_release_is_cleared_when_exact_project_is_already_absent()
    {
        var logRoot = Directory.CreateTempSubdirectory("ghpmv-fixture-project-pending-release-").FullName;
        try
        {
            await new ProjectImportLog
            {
                CreatedProjectId = "PVT_1",
                PendingProjectDeletionId = "PVT_1",
            }.SaveAsync(logRoot, TestContext.Current.CancellationToken);
            using var graphQlHandler = new RecordingHandler(
                JsonResponse("""{"data":{"node":null}}"""));
            using var graphQl = new GitHubGraphQLClient(
                "token",
                baseUrl: null,
                graphQlHandler,
                (_, _) => Task.CompletedTask);
            var importer = new ProjectImporter(graphQl)
            {
                OperationLogDirectory = logRoot,
            };

            await importer.ReleaseReservedProjectAsync(TestContext.Current.CancellationToken);

            var projectLog = await ProjectImportLog.LoadAsync(
                logRoot,
                TestContext.Current.CancellationToken);
            Assert.Null(projectLog.CreatedProjectId);
            Assert.Null(projectLog.PendingProjectDeletionId);
            Assert.DoesNotContain(
                graphQlHandler.RequestBodies,
                body => body.Contains("deleteProjectV2(", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(logRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Definitive_project_release_failure_preserves_claim_without_visibility_probe()
    {
        var logRoot = Directory.CreateTempSubdirectory("ghpmv-fixture-project-release-forbidden-").FullName;
        try
        {
            await new ProjectImportLog { CreatedProjectId = "PVT_1" }
                .SaveAsync(logRoot, TestContext.Current.CancellationToken);
            using var graphQlHandler = new RecordingHandler(JsonResponse(
                """
                {"errors":[{"type":"FORBIDDEN","message":"forbidden"}]}
                """));
            using var graphQl = new GitHubGraphQLClient(
                "token",
                baseUrl: null,
                graphQlHandler,
                (_, _) => Task.CompletedTask);
            var importer = new ProjectImporter(graphQl)
            {
                OperationLogDirectory = logRoot,
            };

            await Assert.ThrowsAsync<GitHubGraphQLException>(() =>
                importer.ReleaseReservedProjectAsync(TestContext.Current.CancellationToken));

            var projectLog = await ProjectImportLog.LoadAsync(
                logRoot,
                TestContext.Current.CancellationToken);
            Assert.Equal("PVT_1", projectLog.CreatedProjectId);
            Assert.Equal("PVT_1", projectLog.PendingProjectDeletionId);
            Assert.Single(graphQlHandler.RequestBodies);
            Assert.DoesNotContain(
                graphQlHandler.RequestBodies,
                body => body.Contains("node(id:", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(logRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Existing_empty_repository_is_claimed_before_fixture_writes()
    {
        var logRoot = Directory.CreateTempSubdirectory("ghpmv-fixture-fallback-write-").FullName;
        try
        {
            using var graphQlHandler = new RecordingHandler(
                JsonResponse(
                    """
                    {"data":{"organization":{"projectsV2":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                    """),
                JsonResponse("""{"data":{"viewer":{"login":"octocat"}}}"""),
                JsonResponse(
                    """
                    {"data":{"organization":{"projectsV2":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                    """),
                JsonResponse("""{"data":{"organization":{"id":"O_example"}}}"""),
                JsonResponse(
                    """
                    {"data":{"createProjectV2":{"projectV2":{"id":"PVT_1","number":1,"title":"Fixture","url":"https://github.com/orgs/example/projects/1","public":false}}}}
                    """),
                JsonResponse(
                    """
                    {"data":{"organization":{"projectsV2":{"nodes":[{"id":"PVT_1","number":1,"title":"Fixture","url":"https://github.com/orgs/example/projects/1"}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                    """));
            using var restHandler = new RecordingHandler(
                JsonResponse("""{"id":1,"name":"fixture","private":true}"""),
                ErrorResponse(HttpStatusCode.Conflict),
                JsonResponse("[]"),
                NotFoundResponse(),
                ErrorResponse(HttpStatusCode.UnprocessableEntity));
            using var graphQl = new GitHubGraphQLClient("token", baseUrl: null, graphQlHandler, (_, _) => Task.CompletedTask);
            using var rest = new GitHubRestClient("token", baseUri: null, restHandler);
            var builder = CreateRequireNewBuilder(
                graphQl,
                rest,
                allowExistingEmptyRepository: true,
                operationLogDirectory: logRoot);

            await Assert.ThrowsAsync<HttpRequestException>(() =>
                builder.CreateAsync("example", "Fixture", "fixture", TestContext.Current.CancellationToken));

            Assert.Equal(
                [HttpMethod.Get, HttpMethod.Get, HttpMethod.Get, HttpMethod.Get, HttpMethod.Put],
                restHandler.RequestMethods);
            Assert.Equal("/repos/example/fixture/contents/README.md", restHandler.RequestPaths[^2]);
            Assert.Equal("/repos/example/fixture/contents/README.md", restHandler.RequestPaths[^1]);
            var operationDirectory = GetOperationDirectory(logRoot, "example", "Fixture", "fixture");
            var state = await File.ReadAllLinesAsync(
                Path.Combine(operationDirectory, "fixture-repository.txt"),
                TestContext.Current.CancellationToken);
            Assert.Equal("claimed", state[2]);
        }
        finally
        {
            Directory.Delete(logRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Fallback_repository_claim_is_revalidated_before_retry_writes()
    {
        var logRoot = Directory.CreateTempSubdirectory("ghpmv-fixture-fallback-retry-").FullName;
        try
        {
            var operationDirectory = GetOperationDirectory(logRoot, "example", "Fixture", "fixture");
            Directory.CreateDirectory(operationDirectory);
            await File.WriteAllLinesAsync(
                Path.Combine(operationDirectory, "fixture-repository.txt"),
                ["https://api.github.com", "example/fixture", "fallback-pending", "1"],
                TestContext.Current.CancellationToken);
            using var graphQlHandler = new RecordingHandler(
                JsonResponse(
                    """
                    {"data":{"organization":{"projectsV2":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                    """),
                JsonResponse("""{"data":{"viewer":{"login":"octocat"}}}"""));
            using var restHandler = new RecordingHandler(
                JsonResponse("""{"id":1,"name":"fixture","private":true}"""),
                JsonResponse("""[{"name":"README.md"}]"""),
                JsonResponse("[]"));
            using var graphQl = new GitHubGraphQLClient("token", baseUrl: null, graphQlHandler, (_, _) => Task.CompletedTask);
            using var rest = new GitHubRestClient("token", baseUri: null, restHandler);
            var builder = CreateRequireNewBuilder(
                graphQl,
                rest,
                allowExistingEmptyRepository: true,
                operationLogDirectory: logRoot);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                builder.CreateAsync("example", "Fixture", "fixture", TestContext.Current.CancellationToken));

            Assert.Equal("Fixture repository 'example/fixture' is not empty.", exception.Message);
            Assert.Equal([HttpMethod.Get, HttpMethod.Get, HttpMethod.Get], restHandler.RequestMethods);
            Assert.DoesNotContain(graphQlHandler.RequestBodies, body => body.Contains("mutation", StringComparison.Ordinal));
            var state = await File.ReadAllLinesAsync(
                Path.Combine(operationDirectory, "fixture-repository.txt"),
                TestContext.Current.CancellationToken);
            Assert.Equal("fallback-pending", state[2]);
        }
        finally
        {
            Directory.Delete(logRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Repository_created_by_operation_is_reused_on_retry()
    {
        var logRoot = Directory.CreateTempSubdirectory("ghpmv-fixture-repository-resume-").FullName;
        try
        {
            using (var firstGraphQlHandler = new RecordingHandler(
                       JsonResponse(
                           """
                           {"data":{"organization":{"projectsV2":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                           """),
                       JsonResponse("""{"data":{"viewer":{"login":"octocat"}}}"""),
                       JsonResponse(
                           """
                           {"data":{"organization":{"projectsV2":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                           """),
                       JsonResponse("""{"data":{"organization":{"id":"O_example"}}}"""),
                       JsonResponse(
                           """
                           {"data":{"createProjectV2":{"projectV2":{"id":"PVT_1","number":1,"title":"Fixture","url":"https://github.com/orgs/example/projects/1","public":false}}}}
                           """),
                       JsonResponse(
                           """
                           {"data":{"organization":{"projectsV2":{"nodes":[{"id":"PVT_1","number":1,"title":"Fixture","url":"https://github.com/orgs/example/projects/1"}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                           """)))
            using (var firstRestHandler = new RecordingHandler(
                       NotFoundResponse(),
                       JsonResponse("""{"id":1,"name":"fixture","private":true}"""),
                       NotFoundResponse(),
                       ErrorResponse(HttpStatusCode.UnprocessableEntity)))
            using (var firstGraphQl = new GitHubGraphQLClient("token", baseUrl: null, firstGraphQlHandler, (_, _) => Task.CompletedTask))
            using (var firstRest = new GitHubRestClient("token", baseUri: null, firstRestHandler))
            {
                var builder = CreateRequireNewBuilder(firstGraphQl, firstRest, operationLogDirectory: logRoot);

                await Assert.ThrowsAsync<HttpRequestException>(() =>
                    builder.CreateAsync("example", "Fixture", "fixture", TestContext.Current.CancellationToken));
            }

            using var retryGraphQlHandler = new RecordingHandler(
                JsonResponse(
                    """
                    {"data":{"organization":{"projectsV2":{"nodes":[{"id":"PVT_1","number":1,"title":"Fixture","url":"https://github.com/orgs/example/projects/1"}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                    """),
                JsonResponse("""{"data":{"viewer":{"login":"octocat"}}}"""),
                JsonResponse(
                    """
                    {"data":{"organization":{"projectsV2":{"nodes":[{"id":"PVT_1","number":1,"title":"Fixture","url":"https://github.com/orgs/example/projects/1","public":false}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                    """));
            using var retryRestHandler = new RecordingHandler(
                JsonResponse("""{"id":1,"name":"fixture","private":true}"""),
                NotFoundResponse(),
                ErrorResponse(HttpStatusCode.UnprocessableEntity));
            using var retryGraphQl = new GitHubGraphQLClient("token", baseUrl: null, retryGraphQlHandler, (_, _) => Task.CompletedTask);
            using var retryRest = new GitHubRestClient("token", baseUri: null, retryRestHandler);
            var retryBuilder = CreateRequireNewBuilder(retryGraphQl, retryRest, operationLogDirectory: logRoot);

            await Assert.ThrowsAsync<HttpRequestException>(() =>
                retryBuilder.CreateAsync("example", "Fixture", "fixture", TestContext.Current.CancellationToken));

            Assert.Equal(
                ["/repos/example/fixture", "/repos/example/fixture/contents/README.md", "/repos/example/fixture/contents/README.md"],
                retryRestHandler.RequestPaths);
            Assert.Equal([HttpMethod.Get, HttpMethod.Get, HttpMethod.Put], retryRestHandler.RequestMethods);

            var operationDirectory = GetOperationDirectory(logRoot, "example", "Fixture", "fixture");
            await new ImportLog
            {
                ProjectId = "PVT_1",
                SourceSnapshotFingerprint = "fingerprint",
            }.SaveAsync(operationDirectory, TestContext.Current.CancellationToken);
            using var replacedGraphQlHandler = new RecordingHandler(JsonResponse(
                """
                {"data":{"organization":{"projectsV2":{"nodes":[{"id":"PVT_1","number":1,"title":"Fixture","url":"https://github.com/orgs/example/projects/1"}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                """));
            using var replacedRestHandler = new RecordingHandler(JsonResponse("""{"id":2,"name":"fixture"}"""));
            using var replacedGraphQl = new GitHubGraphQLClient("token", baseUrl: null, replacedGraphQlHandler, (_, _) => Task.CompletedTask);
            using var replacedRest = new GitHubRestClient("token", baseUri: null, replacedRestHandler);
            var replacedBuilder = CreateRequireNewBuilder(replacedGraphQl, replacedRest, operationLogDirectory: logRoot);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                replacedBuilder.CreateAsync("example", "Fixture", "fixture", TestContext.Current.CancellationToken));

            Assert.Contains("no longer matches", exception.Message, StringComparison.Ordinal);
            Assert.Equal([HttpMethod.Get], replacedRestHandler.RequestMethods);
        }
        finally
        {
            Directory.Delete(logRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Pending_repository_operation_is_reconciled_before_fixture_writes()
    {
        var logRoot = Directory.CreateTempSubdirectory("ghpmv-fixture-repository-pending-").FullName;
        try
        {
            var operationDirectory = GetOperationDirectory(logRoot, "example", "Fixture", "fixture");
            Directory.CreateDirectory(operationDirectory);
            await File.WriteAllLinesAsync(
                Path.Combine(operationDirectory, "fixture-repository.txt"),
                ["https://api.github.com", "example/fixture", "pending", "operation-id"],
                TestContext.Current.CancellationToken);
            using var graphQlHandler = new RecordingHandler(
                JsonResponse(
                    """
                    {"data":{"organization":{"projectsV2":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                    """),
                JsonResponse("""{"data":{"viewer":{"login":"octocat"}}}"""),
                JsonResponse(
                    """
                    {"data":{"organization":{"projectsV2":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                    """),
                JsonResponse("""{"data":{"organization":{"id":"O_example"}}}"""),
                JsonResponse(
                    """
                    {"data":{"createProjectV2":{"projectV2":{"id":"PVT_1","number":1,"title":"Fixture","url":"https://github.com/orgs/example/projects/1","public":false}}}}
                    """),
                JsonResponse(
                    """
                    {"data":{"organization":{"projectsV2":{"nodes":[{"id":"PVT_1","number":1,"title":"Fixture","url":"https://github.com/orgs/example/projects/1"}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                    """));
            using var restHandler = new RecordingHandler(
                JsonResponse("""{"id":1,"name":"fixture","private":true,"description":"ghpmv fixture operation operation-id"}"""),
                NotFoundResponse(),
                ErrorResponse(HttpStatusCode.UnprocessableEntity));
            using var graphQl = new GitHubGraphQLClient("token", baseUrl: null, graphQlHandler, (_, _) => Task.CompletedTask);
            using var rest = new GitHubRestClient("token", baseUri: null, restHandler);
            var builder = CreateRequireNewBuilder(graphQl, rest, operationLogDirectory: logRoot);

            await Assert.ThrowsAsync<HttpRequestException>(() =>
                builder.CreateAsync("example", "Fixture", "fixture", TestContext.Current.CancellationToken));

            Assert.Equal([HttpMethod.Get, HttpMethod.Get, HttpMethod.Put], restHandler.RequestMethods);
            var state = await File.ReadAllLinesAsync(
                Path.Combine(operationDirectory, "fixture-repository.txt"),
                TestContext.Current.CancellationToken);
            Assert.Equal("claimed", state[2]);
            Assert.Equal("1", state[3]);
        }
        finally
        {
            Directory.Delete(logRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Pending_repository_operation_rejects_unmarked_repository()
    {
        var logRoot = Directory.CreateTempSubdirectory("ghpmv-fixture-repository-unmarked-").FullName;
        try
        {
            var operationDirectory = GetOperationDirectory(logRoot, "example", "Fixture", "fixture");
            Directory.CreateDirectory(operationDirectory);
            await File.WriteAllLinesAsync(
                Path.Combine(operationDirectory, "fixture-repository.txt"),
                ["https://api.github.com", "example/fixture", "pending", "operation-id"],
                TestContext.Current.CancellationToken);
            using var graphQlHandler = new RecordingHandler(
                JsonResponse(
                    """
                    {"data":{"organization":{"projectsV2":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                    """),
                JsonResponse("""{"data":{"viewer":{"login":"octocat"}}}"""));
            using var restHandler = new RecordingHandler(JsonResponse("""{"id":1,"name":"fixture","description":null}"""));
            using var graphQl = new GitHubGraphQLClient("token", baseUrl: null, graphQlHandler, (_, _) => Task.CompletedTask);
            using var rest = new GitHubRestClient("token", baseUri: null, restHandler);
            var builder = CreateRequireNewBuilder(graphQl, rest, operationLogDirectory: logRoot);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                builder.CreateAsync("example", "Fixture", "fixture", TestContext.Current.CancellationToken));

            Assert.Contains("does not match pending operation", exception.Message, StringComparison.Ordinal);
            Assert.Equal([HttpMethod.Get], restHandler.RequestMethods);
        }
        finally
        {
            Directory.Delete(logRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Claimed_repository_that_became_public_is_rejected_before_fixture_writes()
    {
        var logRoot = Directory.CreateTempSubdirectory("ghpmv-fixture-repository-public-").FullName;
        try
        {
            var operationDirectory = GetOperationDirectory(logRoot, "example", "Fixture", "fixture");
            Directory.CreateDirectory(operationDirectory);
            await File.WriteAllLinesAsync(
                Path.Combine(operationDirectory, "fixture-repository.txt"),
                ["https://api.github.com", "example/fixture", "claimed", "1"],
                TestContext.Current.CancellationToken);
            using var graphQlHandler = new RecordingHandler(
                JsonResponse(
                    """
                    {"data":{"organization":{"projectsV2":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                    """),
                JsonResponse("""{"data":{"viewer":{"login":"octocat"}}}"""));
            using var restHandler = new RecordingHandler(
                JsonResponse("""{"id":1,"name":"fixture","private":false}"""));
            using var graphQl = new GitHubGraphQLClient("token", baseUrl: null, graphQlHandler, (_, _) => Task.CompletedTask);
            using var rest = new GitHubRestClient("token", baseUri: null, restHandler);
            var builder = CreateRequireNewBuilder(graphQl, rest, operationLogDirectory: logRoot);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                builder.CreateAsync("example", "Fixture", "fixture", TestContext.Current.CancellationToken));

            Assert.Equal("Fixture repository 'example/fixture' must be private.", exception.Message);
            Assert.Equal([HttpMethod.Get], restHandler.RequestMethods);
            Assert.DoesNotContain(graphQlHandler.RequestBodies, body => body.Contains("mutation", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(logRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Item_log_retry_rejects_claimed_repository_that_became_public()
    {
        var logRoot = Directory.CreateTempSubdirectory("ghpmv-fixture-item-log-public-").FullName;
        try
        {
            var operationDirectory = GetOperationDirectory(logRoot, "example", "Fixture", "fixture");
            Directory.CreateDirectory(operationDirectory);
            await File.WriteAllLinesAsync(
                Path.Combine(operationDirectory, "fixture-repository.txt"),
                ["https://api.github.com", "example/fixture", "claimed", "1"],
                TestContext.Current.CancellationToken);
            await new ImportLog
            {
                ProjectId = "PVT_1",
                SourceSnapshotFingerprint = "fingerprint",
            }.SaveAsync(operationDirectory, TestContext.Current.CancellationToken);
            using var graphQlHandler = new RecordingHandler(JsonResponse(
                """
                {"data":{"organization":{"projectsV2":{"nodes":[{"id":"PVT_1","number":1,"title":"Fixture","url":"https://github.com/orgs/example/projects/1"}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                """));
            using var restHandler = new RecordingHandler(
                JsonResponse("""{"id":1,"name":"fixture","private":false}"""));
            using var graphQl = new GitHubGraphQLClient("token", baseUrl: null, graphQlHandler, (_, _) => Task.CompletedTask);
            using var rest = new GitHubRestClient("token", baseUri: null, restHandler);
            var builder = CreateRequireNewBuilder(graphQl, rest, operationLogDirectory: logRoot);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                builder.CreateAsync("example", "Fixture", "fixture", TestContext.Current.CancellationToken));

            Assert.Equal("Fixture repository 'example/fixture' must be private.", exception.Message);
            Assert.Equal([HttpMethod.Get], restHandler.RequestMethods);
            Assert.DoesNotContain(graphQlHandler.RequestBodies, body => body.Contains("mutation", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(logRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Repository_ownership_does_not_cross_api_hosts()
    {
        var logRoot = Directory.CreateTempSubdirectory("ghpmv-fixture-repository-host-").FullName;
        try
        {
            var githubOperationDirectory = GetOperationDirectory(logRoot, "example", "Fixture", "fixture");
            Directory.CreateDirectory(githubOperationDirectory);
            await File.WriteAllLinesAsync(
                Path.Combine(githubOperationDirectory, "fixture-repository.txt"),
                ["https://api.github.com", "example/fixture", "claimed", "1"],
                TestContext.Current.CancellationToken);
            using var graphQlHandler = new RecordingHandler(
                JsonResponse(
                    """
                    {"data":{"organization":{"projectsV2":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                    """),
                JsonResponse("""{"data":{"viewer":{"login":"octocat"}}}"""));
            using var restHandler = new RecordingHandler(JsonResponse("""{"id":1,"name":"fixture"}"""));
            using var graphQl = new GitHubGraphQLClient(
                "token",
                new Uri("https://api.tenant.ghe.com/graphql"),
                graphQlHandler,
                (_, _) => Task.CompletedTask);
            using var rest = new GitHubRestClient(
                "token",
                new Uri("https://api.tenant.ghe.com/"),
                restHandler);
            var builder = CreateRequireNewBuilder(graphQl, rest, operationLogDirectory: logRoot);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                builder.CreateAsync("example", "Fixture", "fixture", TestContext.Current.CancellationToken));

            Assert.Equal("Fixture repository 'example/fixture' already exists.", exception.Message);
            Assert.Equal([HttpMethod.Get], restHandler.RequestMethods);
        }
        finally
        {
            Directory.Delete(logRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Repository_claim_rejects_mismatched_api_host()
    {
        var logRoot = Directory.CreateTempSubdirectory("ghpmv-fixture-repository-claim-host-").FullName;
        try
        {
            const string tenantApiHost = "https://api.tenant.ghe.com";
            var tenantOperationDirectory = GetOperationDirectory(
                logRoot,
                "example",
                "Fixture",
                "fixture",
                tenantApiHost);
            Directory.CreateDirectory(tenantOperationDirectory);
            await File.WriteAllLinesAsync(
                Path.Combine(tenantOperationDirectory, "fixture-repository.txt"),
                ["https://api.github.com", "example/fixture", "claimed", "1"],
                TestContext.Current.CancellationToken);
            using var graphQlHandler = new RecordingHandler(
                JsonResponse(
                    """
                    {"data":{"organization":{"projectsV2":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                    """),
                JsonResponse("""{"data":{"viewer":{"login":"octocat"}}}"""));
            using var restHandler = new RecordingHandler();
            using var graphQl = new GitHubGraphQLClient(
                "token",
                new Uri(tenantApiHost + "/graphql"),
                graphQlHandler,
                (_, _) => Task.CompletedTask);
            using var rest = new GitHubRestClient(
                "token",
                new Uri(tenantApiHost + "/"),
                restHandler);
            var builder = CreateRequireNewBuilder(graphQl, rest, operationLogDirectory: logRoot);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                builder.CreateAsync("example", "Fixture", "fixture", TestContext.Current.CancellationToken));

            Assert.Contains("belongs to API host", exception.Message, StringComparison.Ordinal);
            Assert.Empty(restHandler.RequestMethods);
        }
        finally
        {
            Directory.Delete(logRoot, recursive: true);
        }
    }

    private static FixtureProjectBuilder CreateRequireNewBuilder(
        GitHubGraphQLClient graphQl,
        GitHubRestClient rest,
        bool allowExistingEmptyRepository = false,
        string? operationLogDirectory = null)
        => new(graphQl, rest)
        {
            OperationLogDirectory = operationLogDirectory
                ?? Path.Combine(Path.GetTempPath(), "ghpmv-tests", Guid.NewGuid().ToString("N")),
            RequireNewResources = true,
            AllowExistingEmptyRepository = allowExistingEmptyRepository,
        };

    private static string GetOperationDirectory(
        string logRoot,
        string organization,
        string title,
        string repositoryName,
        string apiHost = "https://api.github.com")
    {
        var operationKey = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{apiHost.ToLowerInvariant()}\n{organization.ToLowerInvariant()}\n{title}\n{repositoryName.ToLowerInvariant()}")))[..16]
            .ToLowerInvariant();
        return Path.Combine(logRoot, operationKey);
    }

    private static string GetRepositoryLockPath(
        string logRoot,
        string repositoryFullName,
        string apiHost = "https://api.github.com")
    {
        var repositoryKey = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{apiHost.ToLowerInvariant()}\n{repositoryFullName.ToLowerInvariant()}")))[..16]
            .ToLowerInvariant();
        return Path.Combine(logRoot, "repository-locks", repositoryKey + ".lock");
    }

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage NotFoundResponse()
        => new(HttpStatusCode.NotFound);

    private static HttpResponseMessage ErrorResponse(HttpStatusCode statusCode)
        => new(statusCode)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };

    private sealed class RecordingHandler(params object[] responses) : HttpMessageHandler
    {
        private readonly Queue<object> _responses = new(responses);

        public List<string> RequestBodies { get; } = [];

        public List<HttpMethod> RequestMethods { get; } = [];

        public List<string> RequestPaths { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestMethods.Add(request.Method);
            RequestPaths.Add(request.RequestUri!.PathAndQuery);
            if (request.Content is not null)
            {
                RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            var response = _responses.Dequeue();
            if (response is Exception exception)
            {
                throw exception;
            }

            return (HttpResponseMessage)response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                while (_responses.Count > 0)
                {
                    if (_responses.Dequeue() is HttpResponseMessage response)
                    {
                        response.Dispose();
                    }
                }
            }

            base.Dispose(disposing);
        }
    }
}
