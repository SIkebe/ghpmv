using Ghpmv.Core.Export;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;
using Ghpmv.Core.Verify;

namespace Ghpmv.Integration.Tests;

/// <summary>
/// M3 integration tests: imports snapshots into the target org (gpm-target) via the real
/// GraphQL API. The full import test exports the fixture project, imports it under a unique
/// title, and validates target metadata plus the field/option/iteration IDs returned by GitHub.
/// BrowserRoundTripTests independently read back and compare complete target field definitions.
/// Every created project is deleted in a finally block.
/// Requires the GHPMV_TEST_TOKEN environment variable (SSO-authorized for the test orgs).
/// </summary>
public class ProjectImporterTests
{
    private static int FixtureProjectNumber => IntegrationTestSettings.FixtureProjectNumber;

    private static string SourceOrg => IntegrationTestSettings.SourceOrg;

    private static string TargetOrg => IntegrationTestSettings.TargetOrg;

    private static string Token
    {
        get
        {
            var token = Environment.GetEnvironmentVariable("GHPMV_TEST_TOKEN");
            Assert.SkipWhen(string.IsNullOrWhiteSpace(token), "GHPMV_TEST_TOKEN is not set; skipping real-API test.");
            return token!;
        }
    }

    private static string NewTestTitle() => "ghpmv-import-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Full_round_trip_recreates_all_custom_fields_and_status_options()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new GitHubGraphQLClient(Token);

        // Use the known fixture contract because the public field connection cannot
        // enumerate projects linked to a multi-select Issue Field.
        var source = await IntegrationFixtureSnapshot.CreateKnownAsync(client, cancellationToken);
        var title = NewTestTitle();
        var snapshot = source with { Project = source.Project with { Title = title } };

        var importer = new ProjectImporter(client)
        {
            OperationLogDirectory = IntegrationTestSettings.CreateOperationLogDirectory(),
        };
        var result = await importer.ImportAsync(snapshot, TargetOrg, cancellationToken);
        try
        {
            Assert.True(result.Created);
            Assert.Equal(ProjectImportOutcome.Created, result.Outcome);
            Assert.False(string.IsNullOrWhiteSpace(result.ProjectId));
            Assert.True(result.ProjectNumber > 0);
            Assert.Contains(TargetOrg, result.Url, StringComparison.OrdinalIgnoreCase);

            var imported = await ReadProjectInfoAsync(client, TargetOrg, result.ProjectNumber, cancellationToken);

            Assert.Equal(title, imported.Title);
            Assert.Equal(snapshot.Project.ShortDescription, imported.ShortDescription);
            Assert.Equal(snapshot.Project.Readme, imported.Readme);
            Assert.Equal(snapshot.Project.Public, imported.Public);
            Assert.Equal(snapshot.Project.Closed, imported.Closed);

            string[] creatable = ["TEXT", "NUMBER", "DATE", "SINGLE_SELECT", "MULTI_SELECT", "ITERATION"];
            foreach (var sourceField in snapshot.Fields.Where(f =>
                         f.IssueField is null && creatable.Contains(f.DataType)))
            {
                if (sourceField.Options is { Count: > 0 })
                {
                    // Fresh option ids must have been issued and mapped.
                    Assert.True(result.OptionIds.ContainsKey(sourceField.Name));
                    Assert.Equal(
                        sourceField.Options.Select(o => o.Name).Order(StringComparer.Ordinal),
                        result.OptionIds[sourceField.Name].Keys.Order(StringComparer.Ordinal));
                }

                if (sourceField.IterationConfiguration is { } sourceConfig)
                {
                    static IEnumerable<(string Title, string StartDate, int Duration)> Union(IterationConfigurationSnapshot c)
                        => c.Iterations.Concat(c.CompletedIterations)
                            .Select(i => (i.Title, i.StartDate, i.Duration))
                            .OrderBy(i => i.StartDate, StringComparer.Ordinal)
                            .ThenBy(i => i.Title, StringComparer.Ordinal);

                    Assert.True(result.IterationIds.ContainsKey(sourceField.Name));
                    Assert.Equal(
                        Union(sourceConfig).Select(i => i.Title).Order(StringComparer.Ordinal),
                        result.IterationIds[sourceField.Name].Keys.Order(StringComparer.Ordinal));
                }

                // All created fields must be present in the id map for M4.
                Assert.True(result.FieldIds.ContainsKey(sourceField.Name));
            }

            foreach (var sourceField in snapshot.Fields.Where(field => field.IssueField is not null))
            {
                Assert.True(result.IssueFieldIds.ContainsKey(sourceField.Name));
                Assert.Equal(
                    (sourceField.Options ?? []).Select(option => option.Name).Order(StringComparer.Ordinal),
                    result.IssueFieldOptionIds[sourceField.Name].Keys.Order(StringComparer.Ordinal));
            }
        }
        finally
        {
            await DeleteProjectAsync(client, result.ProjectId);
        }
    }

    [Fact]
    public async Task Import_with_conflict_fail_throws_when_title_exists()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new GitHubGraphQLClient(Token);
        var title = NewTestTitle();
        var snapshot = MinimalSnapshot(title);

        var importer = new ProjectImporter(client)
        {
            OperationLogDirectory = IntegrationTestSettings.CreateOperationLogDirectory(),
        }; // OnConflict defaults to Fail.
        var first = await importer.ImportAsync(snapshot, TargetOrg, cancellationToken);
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => importer.ImportAsync(snapshot, TargetOrg, cancellationToken));
            Assert.Contains(title, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteProjectAsync(client, first.ProjectId);
        }
    }

    [Fact]
    public async Task Import_with_conflict_skip_returns_existing_without_duplicating()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new GitHubGraphQLClient(Token);
        var title = NewTestTitle();
        var snapshot = MinimalSnapshot(title);

        var first = await new ProjectImporter(client)
        {
            OperationLogDirectory = IntegrationTestSettings.CreateOperationLogDirectory(),
        }.ImportAsync(snapshot, TargetOrg, cancellationToken);
        try
        {
            var second = await new ProjectImporter(client)
            {
                OnConflict = ConflictAction.Skip,
                OperationLogDirectory = IntegrationTestSettings.CreateOperationLogDirectory(),
            }
                .ImportAsync(snapshot, TargetOrg, cancellationToken);

            Assert.False(second.Created);
            Assert.Equal(ProjectImportOutcome.Skipped, second.Outcome);
            Assert.Equal(first.ProjectId, second.ProjectId);
            Assert.Equal(first.ProjectNumber, second.ProjectNumber);
            Assert.Empty(second.FieldIds);
        }
        finally
        {
            await DeleteProjectAsync(client, first.ProjectId);
        }
    }

    [Fact]
    public async Task Import_into_existing_project_by_number_merges_fields_and_items()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new GitHubGraphQLClient(Token);

        // Create an empty target project directly through the API.
        var title = NewTestTitle();
        var orgData = await client.QueryAsync(
            "query($login: String!) { organization(login: $login) { id } }",
            new { login = TargetOrg },
            cancellationToken);
        var created = await client.QueryAsync(
            """
            mutation($ownerId: ID!, $title: String!) {
              createProjectV2(input: { ownerId: $ownerId, title: $title }) {
                projectV2 { id number }
              }
            }
            """,
            new { ownerId = orgData.GetProperty("organization").GetProperty("id").GetString(), title },
            cancellationToken);
        var emptyProject = created.GetProperty("createProjectV2").GetProperty("projectV2");
        var emptyProjectId = emptyProject.GetProperty("id").GetString()!;
        var emptyProjectNumber = emptyProject.GetProperty("number").GetInt32();

        var logDirectory = Path.Combine(Path.GetTempPath(), "ghpmv-into-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logDirectory);
        try
        {
            // Apply the known fixture contract to the existing project by number.
            var source = await IntegrationFixtureSnapshot.CreateKnownAsync(client, cancellationToken);
            var snapshot = source with
            {
                // The target fixture repository mirrors issues but does not contain the
                // source fixture pull request.
                Items = source.Items
                    .Where(item => item.Type != "PULL_REQUEST")
                    .Select((item, position) => item with { Position = position })
                    .ToArray(),
            };
            var repositoryMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [IntegrationTestSettings.FixtureRepositoryFullName] =
                    IntegrationTestSettings.TargetFixtureRepositoryFullName,
            };

            var importer = new ProjectImporter(client)
            {
                RepositoryMapping = repositoryMapping,
                OperationLogDirectory = logDirectory,
            };
            var result = await importer.ImportIntoAsync(snapshot, TargetOrg, emptyProjectNumber, cancellationToken);

            Assert.False(result.Created);
            Assert.Equal(ProjectImportOutcome.Updated, result.Outcome);
            Assert.Equal(emptyProjectId, result.ProjectId);
            Assert.Equal(emptyProjectNumber, result.ProjectNumber);

            // Issue Fields belong to the repository owner's organization, so relink fixture
            // issues before applying their organization-scoped values.
            var itemImporter = new ItemImporter(client)
            {
                RepositoryMapping = repositoryMapping,
            };
            var itemResult = await itemImporter.ImportAsync(snapshot, result, logDirectory, cancellationToken);
            Assert.Equal(snapshot.Items.Count, itemResult.Created);

            // The existing project keeps its own title but gains the snapshot's custom fields.
            var readBackProject = await ReadProjectInfoAsync(client, TargetOrg, emptyProjectNumber, cancellationToken);
            Assert.Equal(title, readBackProject.Title);
            string[] creatable = ["TEXT", "NUMBER", "DATE", "SINGLE_SELECT", "ITERATION"];
            var expectedFields = snapshot.Fields.Where(field => creatable.Contains(field.DataType)).ToArray();
            var readBackFields = await ReadFieldsByIdAsync(
                client,
                expectedFields.Select(field => result.FieldIds[field.Name]).ToArray(),
                cancellationToken);
            foreach (var field in expectedFields)
            {
                Assert.Contains(readBackFields, actual =>
                    actual.Name == field.Name && actual.DataType == field.DataType);
            }
        }
        finally
        {
            await DeleteProjectAsync(client, emptyProjectId);
            Directory.Delete(logDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_into_missing_project_number_throws()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new GitHubGraphQLClient(Token);

        var importer = new ProjectImporter(client)
        {
            OperationLogDirectory = IntegrationTestSettings.CreateOperationLogDirectory(),
        };
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => importer.ImportIntoAsync(MinimalSnapshot(NewTestTitle()), TargetOrg, 999_999, cancellationToken));
        Assert.Contains("999999", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_with_overridden_title_creates_project_with_new_title()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new GitHubGraphQLClient(Token);

        // Same rewrite the CLI applies for --project-title.
        var overriddenTitle = NewTestTitle();
        var snapshot = MinimalSnapshot("ghpmv-original-title");
        snapshot = snapshot with { Project = snapshot.Project with { Title = overriddenTitle } };

        var result = await new ProjectImporter(client)
        {
            OperationLogDirectory = IntegrationTestSettings.CreateOperationLogDirectory(),
        }.ImportAsync(snapshot, TargetOrg, cancellationToken);
        try
        {
            Assert.True(result.Created);
            var readBack = await new ProjectExporter(client).ExportAsync(TargetOrg, result.ProjectNumber, cancellationToken);
            Assert.Equal(overriddenTitle, readBack.Project.Title);
        }
        finally
        {
            await DeleteProjectAsync(client, result.ProjectId);
        }
    }

    private static ProjectSnapshot MinimalSnapshot(string title) => new()
    {
        SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
        Project = new ProjectInfoSnapshot
        {
            Title = title,
            Public = false,
            Closed = false,
        },
        Fields = [],
        Views = [],
        Workflows = [],
        Items = [],
    };

    private static async Task<ProjectInfoSnapshot> ReadProjectInfoAsync(
        GitHubGraphQLClient client,
        string org,
        int projectNumber,
        CancellationToken cancellationToken)
    {
        var data = await client.QueryAsync(
            """
            query($org: String!, $number: Int!) {
              organization(login: $org) {
                projectV2(number: $number) {
                  title shortDescription readme public closed
                }
              }
            }
            """,
            new { org, number = projectNumber },
            cancellationToken);
        var project = data.GetProperty("organization").GetProperty("projectV2");
        return new ProjectInfoSnapshot
        {
            Title = project.GetProperty("title").GetString() ?? string.Empty,
            ShortDescription = project.GetProperty("shortDescription").GetString(),
            Readme = project.GetProperty("readme").GetString(),
            Public = project.GetProperty("public").GetBoolean(),
            Closed = project.GetProperty("closed").GetBoolean(),
        };
    }

    private static async Task<IReadOnlyList<FieldSnapshot>> ReadFieldsByIdAsync(
        GitHubGraphQLClient client,
        IReadOnlyList<string> fieldIds,
        CancellationToken cancellationToken)
    {
        var data = await client.QueryAsync(
            """
            query($fieldIds: [ID!]!) {
              nodes(ids: $fieldIds) {
                ... on ProjectV2FieldCommon { name dataType }
              }
            }
            """,
            new { fieldIds },
            cancellationToken);
        return
        [
            .. data.GetProperty("nodes").EnumerateArray().Select(field => new FieldSnapshot
            {
                Name = field.GetProperty("name").GetString() ?? string.Empty,
                DataType = field.GetProperty("dataType").GetString() ?? string.Empty,
            }),
        ];
    }

    private static async Task DeleteProjectAsync(GitHubGraphQLClient client, string projectId)
    {
        await client.QueryAsync(
            "mutation($projectId: ID!) { deleteProjectV2(input: { projectId: $projectId }) { projectV2 { id } } }",
            new { projectId },
            CancellationToken.None);
    }

    /// <summary>
    /// Status update history round trip. GitHub exposes no delete for an individual status
    /// update, so this test writes into a throwaway project created for this run only —
    /// never the shared fixture — and deletes it in <c>finally</c>.
    /// </summary>
    [Fact]
    public async Task Status_updates_round_trip_into_a_temporary_project_with_every_status_and_date_shape()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new GitHubGraphQLClient(Token);

        var sourceTitle = "ghpmv-status-source-" + Guid.NewGuid().ToString("N");
        var targetTitle = "ghpmv-status-target-" + Guid.NewGuid().ToString("N");
        string? sourceProjectId = null;
        string? targetProjectId = null;
        var sourceCreationAttempted = false;
        var targetCreationAttempted = false;
        var testBodyCompleted = false;
        var sourceLogDirectory = IntegrationTestSettings.CreateOperationLogDirectory();
        var targetLogDirectory = IntegrationTestSettings.CreateOperationLogDirectory();
        try
        {
            Directory.CreateDirectory(sourceLogDirectory);
            Directory.CreateDirectory(targetLogDirectory);
            sourceCreationAttempted = true;
            var sourceProject = await TemporaryProjectFixture.CreateAsync(
                client, IntegrationTestSettings.SourceOrg, sourceTitle, cancellationToken);
            sourceProjectId = sourceProject.Id;
            var sourceProjectNumber = sourceProject.Number;
            var sourceSeed = StatusUpdateSnapshot(sourceTitle);
            var sourceSeedResult = await new StatusUpdateImporter(client)
            {
                AddAttributionNote = false,
            }.ImportAsync(
                sourceSeed,
                TemporaryTarget(sourceProjectId, sourceProjectNumber),
                sourceLogDirectory,
                cancellationToken);
            Assert.Equal(5, sourceSeedResult.Created);

            var source = await new ProjectExporter(client).ExportAsync(
                IntegrationTestSettings.SourceOrg,
                sourceProjectNumber,
                cancellationToken);
            var sourceUpdates = source.StatusUpdates;
            Assert.NotNull(sourceUpdates);
            Assert.Equal(5, sourceUpdates.Count);
            Assert.Equal(2, sourceUpdates.Count(update => update.Body == "Repeated **Markdown** body."));

            targetCreationAttempted = true;
            var targetProject = await TemporaryProjectFixture.CreateAsync(
                client, TargetOrg, targetTitle, cancellationToken);
            targetProjectId = targetProject.Id;
            var projectId = targetProject.Id;
            var projectNumber = targetProject.Number;
            var target = TemporaryTarget(projectId, projectNumber);
            var importer = new StatusUpdateImporter(client);

            var first = await importer.ImportAsync(source, target, targetLogDirectory, cancellationToken);
            Assert.Equal(5, first.Created);
            Assert.Equal(0, first.Resumed);
            Assert.Equal(0, first.AlreadyComplete);

            var log = await ImportLog.LoadAsync(targetLogDirectory, cancellationToken);
            Assert.NotNull(log);
            Assert.Equal(5, log.StatusUpdates.Count);
            Assert.Empty(log.PendingStatusUpdates);
            // One distinct target node id per source sequence index.
            Assert.Equal(5, log.StatusUpdates.Values.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(projectId, log.ProjectId);

            var reExported = await new ProjectExporter(client).ExportAsync(TargetOrg, projectNumber, cancellationToken);
            var targetUpdates = reExported.StatusUpdates;
            Assert.NotNull(targetUpdates);
            Assert.Equal(sourceUpdates.Count, targetUpdates.Count);

            // Creation is oldest-first, so the server's reverse-chronological history
            // lines up with the source sequence index for index.
            for (var index = 0; index < sourceUpdates.Count; index++)
            {
                var expected = sourceUpdates[index];
                var actual = targetUpdates[index];
                Assert.Equal(expected.Status, actual.Status);
                Assert.Equal(expected.StartDate, actual.StartDate);
                Assert.Equal(expected.TargetDate, actual.TargetDate);
                Assert.Equal(
                    NormalizeBody(StatusUpdateImporter.BuildImportedBody(expected)),
                    NormalizeBody(actual.Body));
                // The attribution note names the original creator and source timestamp,
                // and the original Markdown body survives below it.
                Assert.Contains($"@{expected.Creator}", actual.Body, StringComparison.Ordinal);
                Assert.Contains(expected.CreatedAt, actual.Body, StringComparison.Ordinal);
                Assert.Contains(NormalizeBody(expected.Body), NormalizeBody(actual.Body), StringComparison.Ordinal);
            }

            Assert.Equal(
                ["COMPLETE", "OFF_TRACK", "AT_RISK", "ON_TRACK", "INACTIVE"],
                targetUpdates.Select(update => update.Status));
            Assert.Contains(targetUpdates, update => update.StartDate is null);
            Assert.Contains(targetUpdates, update => update.TargetDate is null);
            Assert.Contains(targetUpdates, update => update.StartDate is not null && update.TargetDate is not null);
            Assert.Equal(2, targetUpdates.Count(update =>
                NormalizeBody(update.Body).EndsWith("Repeated **Markdown** body.", StringComparison.Ordinal)));

            var verifyReport = await new ProjectVerifier(client).VerifyAsync(
                source,
                TargetOrg,
                projectNumber,
                cancellationToken);
            Assert.Equal(
                VerifyStatus.Match,
                verifyReport.Categories.Single(category => category.Category == "StatusUpdate").Status);

            // Re-running against the same log resumes by persisted node id: nothing is
            // created and nothing is deduplicated by content.
            var second = await importer.ImportAsync(source, target, targetLogDirectory, cancellationToken);
            Assert.Equal(0, second.Created);
            Assert.Equal(0, second.Resumed);
            Assert.Equal(5, second.AlreadyComplete);

            var afterRerun = await new ProjectExporter(client).ExportAsync(TargetOrg, projectNumber, cancellationToken);
            var rerunUpdates = afterRerun.StatusUpdates;
            Assert.NotNull(rerunUpdates);
            Assert.Equal(5, rerunUpdates.Count);
            Assert.Equal(
                targetUpdates.Select(update => NormalizeBody(update.Body)),
                rerunUpdates.Select(update => NormalizeBody(update.Body)));

            var rerunLog = await ImportLog.LoadAsync(targetLogDirectory, cancellationToken);
            Assert.NotNull(rerunLog);
            Assert.Equal(log.StatusUpdates, rerunLog.StatusUpdates);
            testBodyCompleted = true;
        }
        finally
        {
            try
            {
                try
                {
                    if (targetProjectId is not null)
                    {
                        await DeleteProjectAsync(client, targetProjectId);
                    }
                    else if (targetCreationAttempted)
                    {
                        await TemporaryProjectFixture.DeleteAllByTitleAsync(
                            client,
                            TargetOrg,
                            targetTitle,
                            CancellationToken.None);
                    }
                }
                catch (Exception) when (!testBodyCompleted)
                {
                    // Preserve the creation/test failure rather than replacing it with cleanup failure.
                }
            }
            finally
            {
                try
                {
                    try
                    {
                        if (sourceProjectId is not null)
                        {
                            await DeleteProjectAsync(client, sourceProjectId);
                        }
                        else if (sourceCreationAttempted)
                        {
                            await TemporaryProjectFixture.DeleteAllByTitleAsync(
                                client,
                                IntegrationTestSettings.SourceOrg,
                                sourceTitle,
                                CancellationToken.None);
                        }
                    }
                    catch (Exception) when (!testBodyCompleted)
                    {
                        // Preserve the creation/test failure rather than replacing it with cleanup failure.
                    }
                }
                finally
                {
                    try
                    {
                        try
                        {
                            TryDeleteDirectory(sourceLogDirectory);
                        }
                        catch (Exception) when (!testBodyCompleted)
                        {
                            // Preserve the creation/test failure rather than replacing it with cleanup failure.
                        }
                    }
                    finally
                    {
                        try
                        {
                            TryDeleteDirectory(targetLogDirectory);
                        }
                        catch (Exception) when (!testBodyCompleted)
                        {
                            // Preserve the creation/test failure rather than replacing it with cleanup failure.
                        }
                    }
                }
            }
        }
    }

    private static ProjectSnapshot StatusUpdateSnapshot(string title) => new()
    {
        SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
        Project = new ProjectInfoSnapshot
        {
            Title = title,
            Public = false,
            Closed = false,
        },
        Fields = [],
        Views = [],
        Workflows = [],
        Items = [],
        StatusUpdates =
        [
            StatusUpdate("Complete.", "COMPLETE", "2026-01-01", "2026-04-15", "2026-01-05T09:00:00Z"),
            StatusUpdate("Repeated **Markdown** body.", "OFF_TRACK", null, "2026-04-15", "2026-01-04T09:00:00Z"),
            StatusUpdate("Repeated **Markdown** body.", "AT_RISK", "2026-01-01", null, "2026-01-03T09:00:00Z"),
            StatusUpdate("On track.\n\n- API\n- Import", "ON_TRACK", "2026-01-01", "2026-03-31", "2026-01-02T09:00:00Z"),
            StatusUpdate("Inactive.", "INACTIVE", null, null, "2026-01-01T09:00:00Z"),
        ],
    };

    private static StatusUpdateSnapshot StatusUpdate(
        string body,
        string status,
        string? startDate,
        string? targetDate,
        string createdAt) => new()
    {
        Body = body,
        Status = status,
        StartDate = startDate,
        TargetDate = targetDate,
        Creator = null,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
    };

    private static ImportResult TemporaryTarget(string projectId, int projectNumber) => new()
    {
        ProjectId = projectId,
        ProjectNumber = projectNumber,
        Url = "https://github.com/orgs/" + TargetOrg + "/projects/"
            + projectNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Outcome = ProjectImportOutcome.Created,
        FieldIds = new Dictionary<string, string>(StringComparer.Ordinal),
        OptionIds = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal),
        IterationIds = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal),
    };

    private static string NormalizeBody(string body) => body.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp log directory.
        }
    }
}
