using System.Runtime.ExceptionServices;
using System.Text.Json;
using Ghpmv.Core.Export;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Integration.Tests;

public class ImportCapabilityPreflightIntegrationTests
{
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
    public async Task Production_preflight_validates_organization_repository_and_members_capabilities()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var rest = IntegrationTestSettings.CreateRestClient(Token);
        var sourceRepository = IntegrationTestSettings.FixtureRepositoryFullName;
        var plan = new ImportCapabilityPlan(
            RequiresOrganizationAdministrator: true,
            RequiresProjectAdministrator: false,
            RequiresMembersRead: true,
            RequiresVisibilityManagement: false,
            Repositories:
            [
                new RepositoryCapabilityRequirement(
                    sourceRepository,
                    RepositoryCapability.MetadataRead
                        | RepositoryCapability.IssuesWrite
                        | RepositoryCapability.ContentsWrite
                        | RepositoryCapability.SameOwner),
            ]);
        var repositoryMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [sourceRepository] = IntegrationTestSettings.TargetFixtureRepositoryFullName,
        };

        var exception = await Record.ExceptionAsync(() =>
            ImportCapabilityPreflight.ValidateAsync(
                plan,
                IntegrationTestSettings.TargetOrg,
                repositoryMapping,
                rest,
                cancellationToken));

        Assert.Null(exception);

        var repository = await rest.GetAsync(
            $"repos/{IntegrationTestSettings.TargetFixtureRepositoryFullName}",
            cancellationToken);
        Assert.NotNull(repository);
        Assert.Equal(
            IntegrationTestSettings.TargetFixtureRepositoryFullName,
            repository.Value.GetProperty("full_name").GetString(),
            ignoreCase: true);
        var permissions = repository.Value.GetProperty("permissions");
        Assert.True(
            permissions.GetProperty("push").GetBoolean()
                || permissions.GetProperty("maintain").GetBoolean()
                || permissions.GetProperty("admin").GetBoolean(),
            "The credential must have the repository role required by the preflight plan.");

        var teams = await rest.GetAsync(
            $"orgs/{IntegrationTestSettings.TargetOrg}/teams?per_page=1",
            cancellationToken);
        Assert.NotNull(teams);
        Assert.Equal(JsonValueKind.Array, teams.Value.ValueKind);
    }

    [Fact]
    public async Task Failed_production_preflight_runs_before_first_project_write()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N");
        var targetTitle = $"ghpmv-preflight-failure-{suffix}";
        var sourceRepository = IntegrationTestSettings.FixtureRepositoryFullName;
        var targetRepository = $"{IntegrationTestSettings.TargetOrg}/ghpmv-missing-{suffix}";
        var operationLogDirectory = IntegrationTestSettings.CreateOperationLogDirectory();
        var snapshot = MinimalSnapshot(targetTitle) with
        {
            LinkedRepositories = [sourceRepository],
        };
        var plan = ImportCapabilityAnalyzer.Analyze(snapshot);
        var repositoryMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [sourceRepository] = targetRepository,
        };

        using var graphQl = IntegrationTestSettings.CreateClient(Token);
        using var rest = IntegrationTestSettings.CreateRestClient(Token);
        var cleanupFailures = new List<Exception>();
        Exception? testFailure = null;
        try
        {
            var preflightInvocationCount = 0;
            var importer = new ProjectImporter(graphQl)
            {
                RepositoryMapping = repositoryMapping,
                OperationLogDirectory = operationLogDirectory,
                BeforeWriteAsync = async callbackCancellationToken =>
                {
                    preflightInvocationCount++;
                    await ImportCapabilityPreflight.ValidateAsync(
                        plan,
                        IntegrationTestSettings.TargetOrg,
                        repositoryMapping,
                        rest,
                        callbackCancellationToken);
                },
            };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                importer.ImportAsync(
                    snapshot,
                    IntegrationTestSettings.TargetOrg,
                    cancellationToken));

            Assert.Equal(1, preflightInvocationCount);
            Assert.Contains(targetRepository, exception.Message, StringComparison.Ordinal);
            Assert.Contains("not found or is not visible", exception.Message, StringComparison.Ordinal);
            var projects = await new ProjectExporter(graphQl).ListProjectsAsync(
                IntegrationTestSettings.TargetOrg,
                includeClosed: true,
                cancellationToken);
            Assert.DoesNotContain(
                projects,
                project => string.Equals(project.Title, targetTitle, StringComparison.Ordinal));
        }
        catch (Exception exception)
        {
            testFailure = exception;
        }
        finally
        {
            try
            {
                await TemporaryProjectFixture.DeleteAllByTitleAsync(
                    graphQl,
                    IntegrationTestSettings.TargetOrg,
                    targetTitle,
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }

            if (Directory.Exists(operationLogDirectory))
            {
                try
                {
                    Directory.Delete(operationLogDirectory, recursive: true);
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception);
                }
            }
        }

        if (testFailure is not null)
        {
            if (cleanupFailures.Count > 0)
            {
                throw new AggregateException(
                    "The preflight before-write test failed and one or more resources could not be cleaned up.",
                    [testFailure, .. cleanupFailures]);
            }

            ExceptionDispatchInfo.Capture(testFailure).Throw();
        }

        if (cleanupFailures.Count > 0)
        {
            throw new AggregateException(
                "One or more preflight before-write resources could not be cleaned up.",
                cleanupFailures);
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
}
