using Ghpmv.TestSupport;
using Ghpmv.Core.GitHub;

namespace Ghpmv.Integration.Tests;

internal static class IntegrationTestSettings
{
    public const int FixturePullRequestNumber = 3;

    public static string SourceOrg => E2eTestEnvironment.SourceOrganization;

    public static string TargetOrg => E2eTestEnvironment.TargetOrganization;

    public static string FixtureRepositoryName => E2eTestEnvironment.IntegrationSourceRepository;

    public static string TargetFixtureRepositoryName => E2eTestEnvironment.IntegrationTargetRepository;

    public static string FixtureRepositoryFullName => $"{SourceOrg}/{FixtureRepositoryName}";

    public static string TargetFixtureRepositoryFullName => $"{TargetOrg}/{TargetFixtureRepositoryName}";

    public static string CreateOperationLogDirectory()
        => Path.Combine(Path.GetTempPath(), $"ghpmv-project-import-{Guid.NewGuid():N}");

    public static int FixtureProjectNumber => E2eTestEnvironment.IntegrationProjectNumber;

    public static GitHubGraphQLClient CreateClient(string token)
        => new(token, GitHubGraphQLClient.NormalizeBaseUrl(E2eTestEnvironment.IntegrationApiBaseUrl.AbsoluteUri));

    public static GitHubRestClient CreateRestClient(string token)
    {
        var graphQlEndpoint = GitHubGraphQLClient.NormalizeBaseUrl(
            E2eTestEnvironment.IntegrationApiBaseUrl.AbsoluteUri);
        return new GitHubRestClient(token, GitHubRestClient.ToRestBaseUri(graphQlEndpoint));
    }
}
