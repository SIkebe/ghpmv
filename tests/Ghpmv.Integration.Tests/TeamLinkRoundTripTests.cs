using Ghpmv.Core.Export;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Verify;
using System.Runtime.ExceptionServices;

namespace Ghpmv.Integration.Tests;

/// <summary>Real-API Project-to-Team link round trip with dedicated, disposable resources.</summary>
public class TeamLinkRoundTripTests
{
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
    public async Task Team_links_round_trip_with_mapping_idempotence_and_target_only_reporting()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var sourceProjectTitle = $"ghpmv-team-source-{suffix}";
        var targetProjectTitle = $"ghpmv-team-target-{suffix}";
        var sourceTeamNames = new[] { $"ghpmv-src-platform-{suffix}", $"ghpmv-src-sdk-{suffix}" };
        var targetTeamNames = new[] { $"ghpmv-dst-engineering-{suffix}", $"ghpmv-dst-sdk-{suffix}", $"ghpmv-dst-extra-{suffix}" };
        var createdTeams = new List<(string Organization, string Slug)>();
        var operationDirectory = IntegrationTestSettings.CreateOperationLogDirectory();

        using var graphQl = IntegrationTestSettings.CreateClient(Token);
        using var rest = IntegrationTestSettings.CreateRestClient(Token);
        var cleanupFailures = new List<Exception>();
        Exception? testFailure = null;
        try
        {
            var sourceTeams = new List<(string Id, string Slug)>();
            foreach (var name in sourceTeamNames)
            {
                sourceTeams.Add(await CreateTeamAsync(rest, SourceOrg, name, createdTeams, cancellationToken));
            }

            var targetTeams = new List<(string Id, string Slug)>();
            foreach (var name in targetTeamNames)
            {
                targetTeams.Add(await CreateTeamAsync(rest, TargetOrg, name, createdTeams, cancellationToken));
            }

            var sourceProject = await TemporaryProjectFixture.CreateAsync(
                graphQl,
                SourceOrg,
                sourceProjectTitle,
                cancellationToken);
            var exporter = new ProjectExporter(graphQl);

            var empty = await exporter.ExportAsync(SourceOrg, sourceProject.Number, cancellationToken);
            Assert.Empty(empty.LinkedTeams!);

            await LinkTeamAsync(graphQl, sourceProject.Id, sourceTeams[0].Id, cancellationToken);
            var single = await exporter.ExportAsync(SourceOrg, sourceProject.Number, cancellationToken);
            Assert.Single(single.LinkedTeams!);

            await LinkTeamAsync(graphQl, sourceProject.Id, sourceTeams[1].Id, cancellationToken);
            var source = await exporter.ExportAsync(SourceOrg, sourceProject.Number, cancellationToken);
            Assert.Equal(2, source.LinkedTeams!.Count);

            var teamMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [$"{SourceOrg}/{sourceTeams[0].Slug}"] = $"{TargetOrg}/{targetTeams[0].Slug}",
                [$"{SourceOrg}/{sourceTeams[1].Slug}"] = $"{TargetOrg}/{targetTeams[1].Slug}",
            };
            var snapshot = source with { Project = source.Project with { Title = targetProjectTitle } };
            var unresolvedTitle = targetProjectTitle + "-unresolved";
            var unresolvedSnapshot = snapshot with
            {
                Project = snapshot.Project with { Title = unresolvedTitle },
            };
            var unresolvedException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new ProjectImporter(graphQl)
                {
                    TeamMapping = new Dictionary<string, string>(teamMapping, StringComparer.OrdinalIgnoreCase)
                    {
                        [$"{SourceOrg}/{sourceTeams[0].Slug}"] = $"{TargetOrg}/missing-{suffix}",
                    },
                    OperationLogDirectory = operationDirectory + "-unresolved",
                }.ImportAsync(unresolvedSnapshot, TargetOrg, cancellationToken));
            Assert.Contains("unresolved:", unresolvedException.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(
                await new ProjectExporter(graphQl).ListProjectsAsync(TargetOrg, includeClosed: true, cancellationToken),
                project => string.Equals(project.Title, unresolvedTitle, StringComparison.Ordinal));

            var importer = new ProjectImporter(graphQl)
            {
                TeamMapping = teamMapping,
                OperationLogDirectory = operationDirectory,
            };
            var imported = await importer.ImportAsync(snapshot, TargetOrg, cancellationToken);
            var importedSnapshot = await exporter.ExportAsync(TargetOrg, imported.ProjectNumber, cancellationToken);
            Assert.Equal(
                teamMapping.Values.Order(StringComparer.OrdinalIgnoreCase),
                importedSnapshot.LinkedTeams!.Select(team => team.Identity).Order(StringComparer.OrdinalIgnoreCase));

            var rerun = await new ProjectImporter(graphQl)
            {
                OnConflict = ConflictAction.Update,
                TeamMapping = teamMapping,
                OperationLogDirectory = operationDirectory,
            }.ImportAsync(snapshot, TargetOrg, cancellationToken);
            Assert.Equal(imported.ProjectId, rerun.ProjectId);
            var afterRerun = await exporter.ExportAsync(TargetOrg, imported.ProjectNumber, cancellationToken);
            Assert.Equal(2, afterRerun.LinkedTeams!.Count);

            await LinkTeamAsync(graphQl, imported.ProjectId, targetTeams[2].Id, cancellationToken);
            var report = await new ProjectVerifier(graphQl)
            {
                TeamMapping = teamMapping,
            }.VerifyAsync(snapshot, TargetOrg, imported.ProjectNumber, cancellationToken);
            Assert.Contains(report.Categories, category =>
                category.Category == "TeamLink" && category.Status == VerifyStatus.PartialMatch);
            Assert.Contains(report.Categories, category =>
                category.Category == "Collaborator" && category.Status == VerifyStatus.NotVerified);
            Assert.Contains(report.Differences, difference =>
                difference.Category == "TeamLink"
                && difference.Severity == VerifySeverity.Warning
                && difference.Message.Contains($"{TargetOrg}/{targetTeams[2].Slug}", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(report.Differences, difference =>
                difference.Category == "TeamLink" && difference.Severity == VerifySeverity.Error);
        }
        catch (Exception exception)
        {
            testFailure = exception;
        }
        finally
        {
            foreach (var cleanup in (Func<Task>[])[
                () => TemporaryProjectFixture.DeleteAllByTitleAsync(
                    graphQl,
                    TargetOrg,
                    targetProjectTitle,
                    CancellationToken.None),
                () => TemporaryProjectFixture.DeleteAllByTitleAsync(
                    graphQl,
                    TargetOrg,
                    targetProjectTitle + "-unresolved",
                    CancellationToken.None),
                () => TemporaryProjectFixture.DeleteAllByTitleAsync(
                    graphQl,
                    SourceOrg,
                    sourceProjectTitle,
                    CancellationToken.None),
                .. createdTeams.AsEnumerable().Reverse().Select<(string Organization, string Slug), Func<Task>>(
                    team => () => rest.DeleteAsync(
                        $"orgs/{team.Organization}/teams/{team.Slug}",
                        CancellationToken.None)),
            ])
            {
                try
                {
                    await cleanup();
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception);
                }
            }

            foreach (var directory in (string[])[operationDirectory, operationDirectory + "-unresolved"])
            {
                if (Directory.Exists(directory))
                {
                    try
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                    catch (Exception exception)
                    {
                        cleanupFailures.Add(exception);
                    }
                }
            }
        }

        if (testFailure is not null)
        {
            if (cleanupFailures.Count > 0)
            {
                throw new AggregateException(
                    "The Team-link E2E test failed and one or more resources could not be cleaned up.",
                    [testFailure, .. cleanupFailures]);
            }

            ExceptionDispatchInfo.Capture(testFailure).Throw();
        }

        if (cleanupFailures.Count > 0)
        {
            throw new AggregateException("One or more Team-link E2E resources could not be cleaned up.", cleanupFailures);
        }
    }

    private static async Task<(string Id, string Slug)> CreateTeamAsync(
        GitHubRestClient rest,
        string organization,
        string name,
        List<(string Organization, string Slug)> createdTeams,
        CancellationToken cancellationToken)
    {
        createdTeams.Add((organization, name));
        var team = await rest.PostAsync(
            $"orgs/{organization}/teams",
            new { name, privacy = "closed" },
            cancellationToken);
        var slug = team.GetProperty("slug").GetString()
            ?? throw new InvalidOperationException($"Created Team '{organization}/{name}' returned no slug.");
        if (!string.Equals(slug, name, StringComparison.Ordinal))
        {
            createdTeams.Add((organization, slug));
        }
        return (
            team.GetProperty("node_id").GetString()
                ?? throw new InvalidOperationException($"Created Team '{organization}/{slug}' returned no node id."),
            slug);
    }

    private static async Task LinkTeamAsync(
        GitHubGraphQLClient client,
        string projectId,
        string teamId,
        CancellationToken cancellationToken)
    {
        await client.MutationAsync(
            "linkProjectV2ToTeam",
            """
            mutation($projectId: ID!, $teamId: ID!, $clientMutationId: String!) {
              linkProjectV2ToTeam(input: { projectId: $projectId, teamId: $teamId, clientMutationId: $clientMutationId }) {
                team { id }
              }
            }
            """,
            new { projectId, teamId },
            MutationRetryPolicy.Idempotent,
            target: projectId,
            requiredResultPath: "team.id",
            cancellationToken: cancellationToken);
    }
}
