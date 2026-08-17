using System.Text.Json;
using Ghpmv.Core.GitHub;

namespace Ghpmv.Core.Import;

public static class ImportCapabilityPreflight
{
    public static async Task ValidateAsync(
        ImportCapabilityPlan plan,
        string targetOrganization,
        IReadOnlyDictionary<string, string> repositoryMapping,
        GitHubRestClient rest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetOrganization);
        ArgumentNullException.ThrowIfNull(repositoryMapping);
        ArgumentNullException.ThrowIfNull(rest);

        if (plan.RequiresOrganizationAdministrator)
        {
            var response = await rest.PostValidationProbeAsync(
                $"orgs/{targetOrganization}/issue-fields",
                cancellationToken).ConfigureAwait(false);
            var accepted = response.AcceptedPermissions?.Contains(
                "issue_fields=write",
                StringComparison.OrdinalIgnoreCase) is true;
            var missingInput = response.Body.Contains(
                    "Invalid input: data cannot be null",
                    StringComparison.OrdinalIgnoreCase)
                || response.Body.Contains("missing_field", StringComparison.OrdinalIgnoreCase)
                || response.Body.Contains("Validation Failed", StringComparison.OrdinalIgnoreCase);
            if (response.StatusCode != System.Net.HttpStatusCode.UnprocessableEntity
                || !accepted
                || !missingInput)
            {
                var diagnosticBody = (response.Body.Length <= 300
                        ? response.Body
                        : response.Body[..300])
                    .Replace('\r', ' ')
                    .Replace('\n', ' ');
                throw new InvalidOperationException(
                    $"Importing organization Issue Fields requires an administrator-owned token with Issue Fields write permission for organization '{targetOrganization}' "
                    + $"(preflight returned HTTP {(int)response.StatusCode}, accepted permissions '{response.AcceptedPermissions ?? "<none>"}', body '{diagnosticBody}').");
            }
        }

        if (plan.RequiresMembersRead)
        {
            var teams = await rest.GetAsync(
                $"orgs/{targetOrganization}/teams?per_page=1",
                cancellationToken).ConfigureAwait(false);
            if (teams is not { ValueKind: JsonValueKind.Array })
            {
                throw new InvalidOperationException(
                    $"Importing team collaborators requires Members read access in organization '{targetOrganization}'.");
            }
        }

        foreach (var requirement in plan.Repositories)
        {
            if (!repositoryMapping.TryGetValue(requirement.SourceRepository, out var targetRepository)
                || string.IsNullOrWhiteSpace(targetRepository))
            {
                throw new InvalidOperationException(
                    $"Repository capability preflight requires a mapping for '{requirement.SourceRepository}'.");
            }

            var separator = targetRepository.IndexOf('/', StringComparison.Ordinal);
            if (separator <= 0 || separator == targetRepository.Length - 1)
            {
                throw new InvalidOperationException(
                    $"Repository mapping '{targetRepository}' for '{requirement.SourceRepository}' is not in 'owner/name' form.");
            }

            var targetOwner = targetRepository[..separator];
            if (requirement.Capabilities.HasFlag(RepositoryCapability.SameOwner)
                && !string.Equals(targetOwner, targetOrganization, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Repository '{targetRepository}' must belong to target organization '{targetOrganization}' for linked repository or Auto-add workflow import.");
            }

            var repository = await rest.GetAsync(
                $"repos/{targetRepository}",
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Mapped target repository '{targetRepository}' was not found or is not visible to the authenticated user.");
            ValidateRepositoryRole(requirement, targetRepository, repository);
        }
    }

    public static void ValidateProjectCapabilities(
        ImportCapabilityPlan plan,
        int projectNumber,
        bool viewerCanUpdate,
        bool visibilityChangeRequired)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if ((plan.RequiresProjectAdministrator || (plan.RequiresVisibilityManagement && visibilityChangeRequired))
            && !viewerCanUpdate)
        {
            throw new InvalidOperationException(
                $"Target Project #{projectNumber} requires Project administrator or organization owner access for collaborators or visibility changes.");
        }
    }

    private static void ValidateRepositoryRole(
        RepositoryCapabilityRequirement requirement,
        string targetRepository,
        JsonElement repository)
    {
        if (!repository.TryGetProperty("permissions", out var permissions)
            || permissions.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Repository '{targetRepository}' did not report permissions for the authenticated user.");
        }

        var canRead = GetPermission(permissions, "pull")
            || GetPermission(permissions, "triage")
            || GetPermission(permissions, "push")
            || GetPermission(permissions, "maintain")
            || GetPermission(permissions, "admin");
        var canManageIssues = GetPermission(permissions, "triage")
            || GetPermission(permissions, "push")
            || GetPermission(permissions, "maintain")
            || GetPermission(permissions, "admin");
        var canWriteContents = GetPermission(permissions, "push")
            || GetPermission(permissions, "maintain")
            || GetPermission(permissions, "admin");
        if (!canRead
            || (requirement.Capabilities.HasFlag(RepositoryCapability.IssuesWrite) && !canManageIssues)
            || (requirement.Capabilities.HasFlag(RepositoryCapability.ContentsWrite) && !canWriteContents))
        {
            throw new InvalidOperationException(
                $"The authenticated user lacks the repository role required for '{targetRepository}' ({Format(requirement.Capabilities)}).");
        }
    }

    private static bool GetPermission(JsonElement permissions, string name)
        => permissions.TryGetProperty(name, out var value)
            && value.ValueKind is JsonValueKind.True;

    public static string Format(RepositoryCapability capabilities)
        => string.Join(
            ',',
            Enum.GetValues<RepositoryCapability>()
                .Where(capability => capability is not RepositoryCapability.None
                    && capabilities.HasFlag(capability))
                .Select(capability => capability.ToString()));
}
