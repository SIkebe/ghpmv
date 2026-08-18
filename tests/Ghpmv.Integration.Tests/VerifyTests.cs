using System.Diagnostics;
using System.Globalization;
using Ghpmv.Core.Export;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;
using Ghpmv.Core.Verify;

namespace Ghpmv.Integration.Tests;

/// <summary>
/// M5 integration tests: exports the fixture project, imports it into gpm-target and
/// verifies that <see cref="ProjectVerifier"/> reports no differences beyond the
/// Views/Workflows omitted from this API-only fixture snapshot. Then drifts the target
/// on purpose — deletes one
/// custom field via <c>deleteProjectV2Field</c> and changes an item's Status value —
/// and asserts both differences are detected as errors. The target project is deleted
/// in a finally block. Requires the GHPMV_TEST_TOKEN environment variable (SSO-authorized).
/// </summary>
public class VerifyTests
{
    private static readonly TimeSpan VerificationPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan VerificationTimeout = TimeSpan.FromMinutes(2);

    private static int FixtureProjectNumber => IntegrationTestSettings.FixtureProjectNumber;
    private static string FixtureRepo => IntegrationTestSettings.FixtureRepositoryFullName;
    private const string StatusFieldName = "Status";

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

    [Fact]
    public async Task Verify_matches_after_import_then_detects_deleted_field_and_changed_status()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = IntegrationTestSettings.CreateClient(Token);
        var source = await IntegrationFixtureSnapshot.CreateKnownAsync(client, cancellationToken);
        source = source with
        {
            Items = source.Items
                .Where(item => item.Type != "PULL_REQUEST")
                .Select((item, position) => item with { Position = position })
                .ToArray(),
        };

        // Guard against silent null==null passes: the enriched fixture must actually carry
        // the elements this test claims to verify end-to-end.
        Assert.Equal("gpm fixture project", source.Project.ShortDescription);
        Assert.False(string.IsNullOrWhiteSpace(source.Project.Readme));
        Assert.Contains(
            source.Fields.Single(f => f.Name == "Fixture Sprint").IterationConfiguration!.CompletedIterations,
            i => i.Title == "Sprint 0");
        Assert.Equal(6, source.Items.Count);
        Assert.Contains(source.Items, i => i.Type == "ISSUE");
        Assert.Contains(source.Items, i => i.IsArchived);
        Assert.Contains(source.Items, i => i.Draft?.Assignees is { Count: > 0 });
        Assert.NotNull(source.StatusUpdates);
        Assert.Equal(5, source.StatusUpdates.Count);
        Assert.Equal(
            ["Platform", "SDK"],
            source.Items.Single(i => i.Type == "ISSUE").FieldValues
                .Single(value => value.FieldName == "Fixture Teams").MultiSelectOptionNames);
        Assert.NotNull(source.LinkedRepositories);

        var snapshot = source with { Project = source.Project with { Title = "ghpmv-verify-test-" + Guid.NewGuid().ToString("N") } };
        var targetFixtureRepo = IntegrationTestSettings.TargetFixtureRepositoryFullName;
        var repoMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [FixtureRepo] = targetFixtureRepo,
        };
        var userMapping = snapshot.Items
            .SelectMany(item => item.Draft?.Assignees ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(login => login, login => login, StringComparer.OrdinalIgnoreCase);

        var logDirectory = Directory.CreateTempSubdirectory("ghpmv-m5-").FullName;
        ImportResult? result = null;
        var testBodyCompleted = false;
        try
        {
            result = await new ProjectImporter(client)
            {
                RepositoryMapping = repoMapping,
                OperationLogDirectory = logDirectory,
            }
                .ImportAsync(snapshot, TargetOrg, cancellationToken);
            var itemResult = await new ItemImporter(client)
            {
                RepositoryMapping = repoMapping,
                UserMapping = userMapping,
            }
                .ImportAsync(snapshot, result, logDirectory, cancellationToken);
            Assert.Equal(snapshot.Items.Count, itemResult.Created);
            Assert.Empty(itemResult.Warnings);
            var statusUpdateResult = await new StatusUpdateImporter(client)
                .ImportAsync(snapshot, result, logDirectory, cancellationToken);
            Assert.Equal(source.StatusUpdates.Count, statusUpdateResult.Created);
            var importLog = await ImportLog.LoadAsync(logDirectory, cancellationToken);
            Assert.NotNull(importLog);
            Assert.Equal(snapshot.Items.Count, importLog.Items.Count);
            Assert.Equal(source.StatusUpdates.Count, importLog.StatusUpdates.Count);
            var verificationSnapshot = snapshot with
            {
                LinkedRepositories = snapshot.LinkedRepositories?.Select(repository =>
                    string.Equals(repository, FixtureRepo, StringComparison.OrdinalIgnoreCase)
                        ? targetFixtureRepo
                        : repository).ToList(),
                Items = snapshot.Items.Select(item =>
                    string.Equals(item.Repository, FixtureRepo, StringComparison.OrdinalIgnoreCase)
                        ? item with { Repository = targetFixtureRepo }
                        : item).ToList(),
            };
            await IntegrationFixtureSnapshot.RemoveUnexpectedItemsAsync(
                client, TargetOrg, result.ProjectNumber, verificationSnapshot, cancellationToken);
            await ProjectTemplateWriteSession.SetFinalStateAsync(
                client,
                result.ProjectId,
                snapshot.Project.Template!.Value,
                cancellationToken: cancellationToken);

            var postExportCalled = false;
            var verifier = new ProjectVerifier(client)
            {
                PostExportAsync = (target, _) =>
                {
                    postExportCalled = true;
                    return Task.FromResult(target);
                },
            };

            // 1) Right after a full API import the target matches the snapshot except for
            //    Views/Workflows omitted by the known API fixture. Items are eventually
            //    consistent, so poll until no other error remains.
            var matchReport = await VerifyUntilAsync(verifier, verificationSnapshot, result.ProjectNumber, r => !HasNonBrowserError(r), cancellationToken);
            Assert.True(postExportCalled);
            Assert.False(HasNonBrowserError(matchReport), Describe(matchReport));
            Assert.Contains(matchReport.Differences, d => d.Severity == VerifySeverity.Error && d.Category == "View");
            Assert.Contains(matchReport.Differences, d => d.Severity == VerifySeverity.Error && d.Category == "Workflow");

            // 2) Drift the target: delete one custom TEXT field...
            var fieldName = verificationSnapshot.Fields.First(f => f.DataType == "TEXT").Name;
            await client.QueryAsync(
                """
                mutation($fieldId: ID!) {
                  deleteProjectV2Field(input: { fieldId: $fieldId }) { clientMutationId }
                }
                """,
                new { fieldId = result.FieldIds[fieldName] },
                cancellationToken);

            // ...and flip the Status value of one imported (non-archived) item.
            var statusItem = verificationSnapshot.Items
                .OrderBy(i => i.Position)
                .First(i => !i.IsArchived && i.FieldValues.Any(v => v.FieldName == StatusFieldName && v.SingleSelectOptionName is not null));
            var itemId = importLog.Items[statusItem.Position.ToString(CultureInfo.InvariantCulture)];
            var currentStatus = statusItem.FieldValues.First(v => v.FieldName == StatusFieldName).SingleSelectOptionName!;
            var otherOptionId = result.OptionIds[StatusFieldName].First(kvp => !string.Equals(kvp.Key, currentStatus, StringComparison.Ordinal)).Value;
            await client.QueryAsync(
                """
                mutation($projectId: ID!, $itemId: ID!, $fieldId: ID!, $optionId: String!) {
                  updateProjectV2ItemFieldValue(input: {
                    projectId: $projectId, itemId: $itemId, fieldId: $fieldId,
                    value: { singleSelectOptionId: $optionId }
                  }) { projectV2Item { id } }
                }
                """,
                new { projectId = result.ProjectId, itemId, fieldId = result.FieldIds[StatusFieldName], optionId = otherOptionId },
                cancellationToken);

            // 3) Both drifts are reported as errors.
            var driftReport = await VerifyUntilAsync(verifier, verificationSnapshot, result.ProjectNumber, r =>
                r.Differences.Any(d => d.Severity == VerifySeverity.Error && d.Category == "Field" && d.Message.Contains($"'{fieldName}'", StringComparison.Ordinal))
                && r.Differences.Any(d => d.Severity == VerifySeverity.Error && d.Category == "Item" && d.Message.Contains($"'{StatusFieldName}' value mismatch", StringComparison.Ordinal)),
                cancellationToken);

            Assert.False(driftReport.IsMatch, Describe(driftReport));
            Assert.Contains(driftReport.Differences, d =>
                d.Severity == VerifySeverity.Error
                && d.Category == "Field"
                && d.Message.Contains($"'{fieldName}'", StringComparison.Ordinal)
                && d.Message.Contains("missing in the target", StringComparison.Ordinal));
            Assert.Contains(driftReport.Differences, d =>
                d.Severity == VerifySeverity.Error
                && d.Category == "Item"
                && d.Message.Contains($"'{StatusFieldName}' value mismatch", StringComparison.Ordinal));
            testBodyCompleted = true;
        }
        finally
        {
            try
            {
                if (result is not null)
                {
                    await DeleteProjectAsync(client, result.ProjectId);
                }
                else
                {
                    await TemporaryProjectFixture.DeleteAllByTitleAsync(
                        client,
                        TargetOrg,
                        snapshot.Project.Title,
                        CancellationToken.None);
                }
            }
            catch (Exception) when (!testBodyCompleted)
            {
                // Preserve the test/import failure rather than replacing it with cleanup failure.
            }
            finally
            {
                TryDeleteDirectory(logDirectory);
            }
        }
    }

    /// <summary>The target is eventually consistent after writes; re-verify until the predicate holds.</summary>
    private static async Task<VerifyReport> VerifyUntilAsync(
        ProjectVerifier verifier, ProjectSnapshot snapshot, int projectNumber, Func<VerifyReport, bool> predicate, CancellationToken cancellationToken)
    {
        VerifyReport report = null!;
        var startedAt = Stopwatch.GetTimestamp();
        while (true)
        {
            report = await verifier.VerifyAsync(snapshot, TargetOrg, projectNumber, cancellationToken);
            if (predicate(report))
            {
                return report;
            }

            var remaining = VerificationTimeout - Stopwatch.GetElapsedTime(startedAt);
            if (remaining <= TimeSpan.Zero)
            {
                return report;
            }

            await Task.Delay(
                remaining < VerificationPollInterval ? remaining : VerificationPollInterval,
                cancellationToken);
            if (Stopwatch.GetElapsedTime(startedAt) >= VerificationTimeout)
            {
                return report;
            }
        }
    }

    private static string Describe(VerifyReport report)
        => string.Join(Environment.NewLine, report.Differences.Select(d => $"{d.Severity} {d.Category}: {d.Message}"));

    /// <summary>The known API fixture omits Views/Workflows, while the target has defaults.</summary>
    private static bool IsBrowserCategory(VerifyDifference difference)
        => difference.Category is "View" or "Workflow";

    private static bool HasNonBrowserError(VerifyReport report)
        => report.Differences.Any(d => d.Severity == VerifySeverity.Error && !IsBrowserCategory(d));

    private static async Task DeleteProjectAsync(GitHubGraphQLClient client, string projectId)
    {
        await client.QueryAsync(
            "mutation($projectId: ID!) { deleteProjectV2(input: { projectId: $projectId }) { projectV2 { id } } }",
            new { projectId },
            CancellationToken.None);
    }

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

    /// <summary>
    /// The StatusUpdate category matches right after a status update import and turns into a
    /// mismatch once the target drifts. Drift is additive (one EXTRA update) because GitHub
    /// exposes no delete for an individual status update — hence the throwaway project.
    /// </summary>
    [Fact]
    public async Task Status_update_category_matches_after_import_and_mismatches_after_drift()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = IntegrationTestSettings.CreateClient(Token);
        var source = await IntegrationFixtureSnapshot.CreateKnownAsync(client, cancellationToken);

        // Guard against silent null==null passes: the fixture must actually carry history.
        Assert.Equal(5, source.StatusUpdates!.Count);

        var title = "ghpmv-status-update-test-" + Guid.NewGuid().ToString("N");
        string? projectId = null;
        var creationAttempted = false;
        var testBodyCompleted = false;
        var logDirectory = Directory.CreateTempSubdirectory("ghpmv-status-").FullName;
        try
        {
            creationAttempted = true;
            var createdProject = await TemporaryProjectFixture.CreateAsync(
                client, TargetOrg, title, cancellationToken);
            projectId = createdProject.Id;
            var projectNumber = createdProject.Number;
            var target = new ImportResult
            {
                ProjectId = projectId,
                ProjectNumber = projectNumber,
                Url = "https://github.com/orgs/" + TargetOrg + "/projects/"
                    + projectNumber.ToString(CultureInfo.InvariantCulture),
                Outcome = ProjectImportOutcome.Created,
                FieldIds = new Dictionary<string, string>(StringComparer.Ordinal),
                OptionIds = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal),
                IterationIds = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal),
            };
            var importResult = await new StatusUpdateImporter(client)
                .ImportAsync(source, target, logDirectory, cancellationToken);
            Assert.Equal(5, importResult.Created);

            // Only the StatusUpdate category is under test here: the throwaway project has
            // none of the snapshot's fields, items, views or workflows.
            var verificationSnapshot = source with { Project = source.Project with { Title = title } };
            var verifier = new ProjectVerifier(client);

            var matchReport = await VerifyUntilAsync(
                verifier,
                verificationSnapshot,
                projectNumber,
                report => StatusUpdateStatus(report) == VerifyStatus.Match,
                cancellationToken);
            Assert.Equal(VerifyStatus.Match, StatusUpdateStatus(matchReport));
            Assert.DoesNotContain(matchReport.Differences, d => d.Category == "StatusUpdate");

            // Drift the target with one EXTRA status update created directly.
            await client.MutationAsync(
                "createProjectV2StatusUpdate",
                """
                mutation($projectId: ID!, $body: String!, $status: ProjectV2StatusUpdateStatus!, $clientMutationId: String!) {
                  createProjectV2StatusUpdate(input: { projectId: $projectId, body: $body, status: $status, clientMutationId: $clientMutationId }) {
                    statusUpdate { id }
                  }
                }
                """,
                new { projectId, body = "Drifted target-only status update.", status = "ON_TRACK" },
                target: projectId,
                requiredResultPath: "statusUpdate.id",
                cancellationToken: cancellationToken);

            var driftReport = await VerifyUntilAsync(
                verifier,
                verificationSnapshot,
                projectNumber,
                report => StatusUpdateStatus(report) == VerifyStatus.Mismatch,
                cancellationToken);
            Assert.Equal(VerifyStatus.Mismatch, StatusUpdateStatus(driftReport));
            Assert.Contains(driftReport.Differences, d =>
                d.Severity == VerifySeverity.Error
                && d.Category == "StatusUpdate"
                && d.Message.Contains("status update count mismatch (source 5, target 6)", StringComparison.Ordinal));
            Assert.False(driftReport.IsMatch, Describe(driftReport));
            testBodyCompleted = true;
        }
        finally
        {
            try
            {
                if (projectId is not null)
                {
                    await DeleteProjectAsync(client, projectId);
                }
                else if (creationAttempted)
                {
                    await TemporaryProjectFixture.DeleteAllByTitleAsync(
                        client,
                        TargetOrg,
                        title,
                        CancellationToken.None);
                }
            }
            catch (Exception) when (!testBodyCompleted)
            {
                // Preserve the creation/test failure rather than replacing it with cleanup failure.
            }
            finally
            {
                try
                {
                    TryDeleteDirectory(logDirectory);
                }
                catch (Exception) when (!testBodyCompleted)
                {
                    // Preserve the creation/test failure rather than replacing it with cleanup failure.
                }
            }
        }
    }

    private static VerifyStatus StatusUpdateStatus(VerifyReport report)
        => report.Categories
            .Single(category => string.Equals(category.Category, "StatusUpdate", StringComparison.Ordinal))
            .Status;
}
