using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Tests;

public class TeamLinkMappingTests
{
    [Fact]
    public void Resolve_defaults_to_target_organization_and_preserves_slug()
    {
        var resolution = Assert.Single(TeamLinkMapping.Resolve(
            [Team("source", "platform")],
            System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty,
            "target"));

        Assert.Equal(TeamLinkMappingStatus.Mapped, resolution.Status);
        Assert.Equal("target/platform", resolution.TargetIdentity);
    }

    [Fact]
    public void Resolve_applies_renamed_team_mapping()
    {
        var resolution = Assert.Single(TeamLinkMapping.Resolve(
            [Team("source", "platform")],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["source/platform"] = "target/engineering",
            },
            "target"));

        Assert.Equal("target/engineering", resolution.TargetIdentity);
    }

    [Fact]
    public void Resolve_reports_malformed_target_as_unresolved()
    {
        var resolution = Assert.Single(TeamLinkMapping.Resolve(
            [Team("source", "platform")],
            new Dictionary<string, string> { ["source/platform"] = "not-qualified" },
            "target"));

        Assert.Equal(TeamLinkMappingStatus.Unresolved, resolution.Status);
    }

    [Fact]
    public void Resolve_reports_many_to_one_mapping_as_ambiguous()
    {
        var resolutions = TeamLinkMapping.Resolve(
            [Team("source", "platform"), Team("source", "sdk")],
            new Dictionary<string, string>
            {
                ["source/platform"] = "target/engineering",
                ["source/sdk"] = "target/engineering",
            },
            "target");

        Assert.All(resolutions, resolution => Assert.Equal(TeamLinkMappingStatus.Ambiguous, resolution.Status));
    }

    [Fact]
    public void Resolve_rejects_team_in_a_different_target_organization()
    {
        var resolution = Assert.Single(TeamLinkMapping.Resolve(
            [Team("source", "platform")],
            new Dictionary<string, string>
            {
                ["source/platform"] = "other-org/platform",
            },
            "target"));

        Assert.Equal(TeamLinkMappingStatus.Unresolved, resolution.Status);
        Assert.Contains("other-org", resolution.Message, StringComparison.Ordinal);
        Assert.Contains("target Project belongs to 'target'", resolution.Message, StringComparison.Ordinal);
    }

    private static LinkedTeamSnapshot Team(string organization, string slug) => new()
    {
        Organization = organization,
        Slug = slug,
        Name = slug,
    };
}
