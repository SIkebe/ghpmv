using Ghpmv.Core.Export;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Import;

[Flags]
public enum RepositoryCapability
{
    None = 0,
    MetadataRead = 1,
    IssuesRead = 2,
    PullRequestsRead = 4,
    IssuesWrite = 8,
    ContentsWrite = 16,
    BrowserAccess = 32,
    SameOwner = 64,
}

public sealed record RepositoryCapabilityRequirement(
    string SourceRepository,
    RepositoryCapability Capabilities);

public sealed record ImportCapabilityPlan(
    bool RequiresOrganizationAdministrator,
    bool RequiresProjectAdministrator,
    bool RequiresMembersRead,
    bool RequiresVisibilityManagement,
    IReadOnlyList<RepositoryCapabilityRequirement> Repositories,
    bool RequiresTeamAdministrator = false);

public static class ImportCapabilityAnalyzer
{
    public static ImportCapabilityPlan Analyze(
        ProjectSnapshot snapshot,
        bool includeBrowserAutomation = false,
        ProjectOwnerType ownerType = ProjectOwnerType.Organization)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var requirements = new Dictionary<string, RepositoryCapability>(StringComparer.OrdinalIgnoreCase);
        var hasOrganizationIssueFields = snapshot.Fields.Any(field => field.IssueField is not null);
        var hasApplicableCollaborators = snapshot.Collaborators?.Any(collaborator =>
            ownerType == ProjectOwnerType.Organization
            || !string.Equals(collaborator.Type, "TEAM", StringComparison.OrdinalIgnoreCase)) is true;
        var hasTeamCollaborators = ownerType == ProjectOwnerType.Organization
            && snapshot.Collaborators?.Any(collaborator =>
                string.Equals(collaborator.Type, "TEAM", StringComparison.OrdinalIgnoreCase)) is true;
        var hasLinkedTeams = ownerType == ProjectOwnerType.Organization
            && snapshot.LinkedTeams is { Count: > 0 };
        if (includeBrowserAutomation)
        {
            foreach (var repository in MappingTemplates.ExtractSourceRepositories([snapshot]))
            {
                Add(requirements, repository, RepositoryCapability.MetadataRead);
            }
        }

        foreach (var item in snapshot.Items)
        {
            if (item.Repository is not { Length: > 0 } repository)
            {
                continue;
            }

            var capability = item.Type switch
            {
                "ISSUE" => RepositoryCapability.MetadataRead | RepositoryCapability.IssuesRead,
                "PULL_REQUEST" => RepositoryCapability.MetadataRead | RepositoryCapability.PullRequestsRead,
                _ => RepositoryCapability.MetadataRead,
            };
            if (item.Type == "ISSUE" && hasOrganizationIssueFields)
            {
                capability |= RepositoryCapability.IssuesWrite;
            }

            Add(requirements, repository, capability);
        }

        foreach (var repository in snapshot.LinkedRepositories)
        {
            Add(
                requirements,
                repository,
                RepositoryCapability.MetadataRead
                    | RepositoryCapability.ContentsWrite
                    | RepositoryCapability.SameOwner);
        }

        foreach (var repository in includeBrowserAutomation
                     ? snapshot.Workflows
                         .Select(workflow => workflow.Ui?.Repository)
                         .Where(repository => !string.IsNullOrWhiteSpace(repository))
                     : [])
        {
            Add(
                requirements,
                repository!,
                RepositoryCapability.MetadataRead
                    | RepositoryCapability.BrowserAccess
                    | RepositoryCapability.SameOwner);
        }

        return new ImportCapabilityPlan(
            RequiresOrganizationAdministrator: hasOrganizationIssueFields,
            RequiresProjectAdministrator: hasApplicableCollaborators || hasLinkedTeams,
            RequiresMembersRead: hasTeamCollaborators || hasLinkedTeams,
            RequiresVisibilityManagement: snapshot.Project.Public,
            Repositories: requirements
                .OrderBy(requirement => requirement.Key, StringComparer.OrdinalIgnoreCase)
                .Select(requirement => new RepositoryCapabilityRequirement(requirement.Key, requirement.Value))
                .ToArray(),
            RequiresTeamAdministrator: hasLinkedTeams);
    }

    private static void Add(
        Dictionary<string, RepositoryCapability> requirements,
        string repository,
        RepositoryCapability capabilities)
    {
        requirements.TryGetValue(repository, out var existing);
        requirements[repository] = existing | capabilities;
    }
}
