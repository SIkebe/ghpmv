using Ghpmv.Core.Browser;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Browser.Tests;

public class CollaboratorUiExporterTests
{
    [Fact]
    public void ParseAccessSnapshot_reads_explicit_user_collaborators_and_roles()
    {
        const string snapshot = """
        - heading "Manage access" [level=3]
        - checkbox "Select all collaborators. 1 member"
        - text: Select all collaborators. 1 member
        - checkbox "Select ravel-maurice-uo_sde"
        - img "ravel-maurice-uo_sde"
        - link "Ravel Maurice":
          - /url: /ravel-maurice-uo_sde
        - text: ravel-maurice-uo_sde
        - 'button "Role: Write"'
        - button "Remove"
        """;

        var collaborator = Assert.Single(CollaboratorUiExporter.ParseAccessSnapshot(snapshot, "gpm-source"));

        Assert.Equal("USER", collaborator.Type);
        Assert.Equal("ravel-maurice-uo_sde", collaborator.Login);
        Assert.Equal("WRITER", collaborator.Role);
    }

    [Fact]
    public void ParseAccessSnapshot_reads_team_slug_when_team_url_is_present()
    {
        const string snapshot = """
        - heading "Manage access" [level=3]
        - checkbox "Select Roadmap Team"
        - link "Roadmap Team":
          - /url: /orgs/gpm-source/teams/roadmap-team
        - text: Roadmap Team
        - 'button "Role: Admin"'
        """;

        var collaborator = Assert.Single(CollaboratorUiExporter.ParseAccessSnapshot(snapshot, "gpm-source"));

        Assert.Equal("TEAM", collaborator.Type);
        Assert.Equal("roadmap-team", collaborator.Login);
        Assert.Equal("ADMIN", collaborator.Role);
    }

    [Fact]
    public void ParseAccessSnapshot_ignores_select_all_checkbox()
    {
        const string snapshot = """
        - checkbox "Select all collaborators. 0 members"
        - text: Select all collaborators. 0 members
        - heading "You don't have any collaborators yet." [level=3]
        """;

        Assert.Empty(CollaboratorUiExporter.ParseAccessSnapshot(snapshot, "gpm-source"));
    }

    [Fact]
    public void Linked_team_reader_access_is_not_exported_as_an_explicit_collaborator()
    {
        CollaboratorSnapshot[] collaborators =
        [
            new CollaboratorSnapshot { Type = "TEAM", Login = "linked-team", Role = "READER" },
            new CollaboratorSnapshot { Type = "TEAM", Login = "admin-team", Role = "ADMIN" },
            new CollaboratorSnapshot { Type = "USER", Login = "octocat", Role = "READER" },
        ];
        LinkedTeamSnapshot[] linkedTeams =
        [
            new LinkedTeamSnapshot { Organization = "gpm-source", Slug = "linked-team", Name = "Linked Team" },
            new LinkedTeamSnapshot { Organization = "gpm-source", Slug = "admin-team", Name = "Admin Team" },
        ];

        var explicitCollaborators = CollaboratorUiExporter.ExcludeLinkDerivedTeamAccess(
            collaborators,
            linkedTeams,
            "gpm-source");

        Assert.DoesNotContain(explicitCollaborators, collaborator => collaborator.Login == "linked-team");
        Assert.Contains(explicitCollaborators, collaborator =>
            collaborator.Type == "TEAM" && collaborator.Login == "admin-team" && collaborator.Role == "ADMIN");
        Assert.Contains(explicitCollaborators, collaborator => collaborator.Type == "USER" && collaborator.Login == "octocat");
    }
}
