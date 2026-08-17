using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Import;

public enum TeamLinkMappingStatus
{
    Mapped,
    Unresolved,
    Ambiguous,
}

public sealed record TeamLinkMappingResolution
{
    public required LinkedTeamSnapshot Source { get; init; }

    public string? TargetOrganization { get; init; }

    public string? TargetSlug { get; init; }

    public required TeamLinkMappingStatus Status { get; init; }

    public string? Message { get; init; }

    public string? TargetIdentity => TargetOrganization is null || TargetSlug is null
        ? null
        : $"{TargetOrganization}/{TargetSlug}";
}

/// <summary>Resolves stable Team identities through the shared source/target CSV mapping shape.</summary>
public static class TeamLinkMapping
{
    public static IReadOnlyList<TeamLinkMappingResolution> Resolve(
        IReadOnlyList<LinkedTeamSnapshot> teams,
        IReadOnlyDictionary<string, string> mapping,
        string defaultTargetOrganization)
    {
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultTargetOrganization);

        var resolutions = teams.Select(team => ResolveOne(team, mapping, defaultTargetOrganization)).ToList();
        var duplicateSources = resolutions
            .GroupBy(resolution => resolution.Source.Identity, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Skip(1).Any())
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var duplicateTargets = resolutions
            .Where(resolution => resolution.Status == TeamLinkMappingStatus.Mapped)
            .GroupBy(resolution => resolution.TargetIdentity!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(item => item.Source.Identity).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return resolutions.Select(resolution =>
        {
            if (duplicateSources.Contains(resolution.Source.Identity))
            {
                return resolution with
                {
                    Status = TeamLinkMappingStatus.Ambiguous,
                    Message = $"source Team '{resolution.Source.Identity}' appears more than once",
                };
            }

            if (resolution.TargetIdentity is { } target && duplicateTargets.Contains(target))
            {
                return resolution with
                {
                    Status = TeamLinkMappingStatus.Ambiguous,
                    Message = $"multiple source Teams map to target Team '{target}'",
                };
            }

            return resolution;
        }).ToList();
    }

    public static bool TryParseIdentity(string value, out string organization, out string slug)
    {
        organization = string.Empty;
        slug = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var separator = value.IndexOf('/', StringComparison.Ordinal);
        if (separator <= 0
            || separator == value.Length - 1
            || value.IndexOf('/', separator + 1) >= 0)
        {
            return false;
        }

        organization = value[..separator].Trim();
        slug = value[(separator + 1)..].Trim();
        return organization.Length > 0 && slug.Length > 0;
    }

    private static TeamLinkMappingResolution ResolveOne(
        LinkedTeamSnapshot team,
        IReadOnlyDictionary<string, string> mapping,
        string defaultTargetOrganization)
    {
        if (!TryParseIdentity(team.Identity, out _, out var sourceSlug))
        {
            return new TeamLinkMappingResolution
            {
                Source = team,
                Status = TeamLinkMappingStatus.Unresolved,
                Message = $"source Team '{team.Identity}' is not in 'organization/slug' form",
            };
        }

        var target = mapping.TryGetValue(team.Identity, out var mapped)
            ? mapped
            : $"{defaultTargetOrganization}/{sourceSlug}";
        if (!TryParseIdentity(target, out var targetOrganization, out var targetSlug))
        {
            return new TeamLinkMappingResolution
            {
                Source = team,
                Status = TeamLinkMappingStatus.Unresolved,
                Message = $"target Team '{target}' is not in 'organization/slug' form",
            };
        }

        if (!string.Equals(targetOrganization, defaultTargetOrganization, StringComparison.OrdinalIgnoreCase))
        {
            return new TeamLinkMappingResolution
            {
                Source = team,
                Status = TeamLinkMappingStatus.Unresolved,
                Message =
                    $"target Team '{target}' belongs to organization '{targetOrganization}', but the target Project belongs to '{defaultTargetOrganization}'",
            };
        }

        return new TeamLinkMappingResolution
        {
            Source = team,
            TargetOrganization = targetOrganization,
            TargetSlug = targetSlug,
            Status = TeamLinkMappingStatus.Mapped,
        };
    }
}
