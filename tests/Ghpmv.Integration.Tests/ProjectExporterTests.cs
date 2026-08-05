using Ghpmv.Core.Export;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Integration.Tests;

/// <summary>
/// M2 integration tests: exports the fixture project through the real GraphQL metadata
/// and item connections while supplying the known field catalog that the public field
/// connection cannot enumerate. API-only fail-closed behavior is covered separately.
/// Requires the GHPMV_TEST_TOKEN environment variable (SSO-authorized for the test orgs).
/// Skipped when the variable is not set (e.g. fork PRs without secrets).
/// </summary>
public class ProjectExporterTests
{
    private static int FixtureProjectNumber => IntegrationTestSettings.FixtureProjectNumber;

    private static string Org => IntegrationTestSettings.SourceOrg;

    private static string Token
    {
        get
        {
            var token = Environment.GetEnvironmentVariable("GHPMV_TEST_TOKEN");
            Assert.SkipWhen(string.IsNullOrWhiteSpace(token), "GHPMV_TEST_TOKEN is not set; skipping real-API test.");
            return token!;
        }
    }

    private static async Task<ProjectSnapshot> ExportFixtureAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new GitHubGraphQLClient(Token);
        var catalog = await CreateFixtureCatalogAsync(client, cancellationToken);
        var snapshot = await new ProjectExporter(client)
        {
            CompleteFieldCatalogProviderAsync = (_, _) => Task.FromResult(catalog),
        }.ExportAsync(Org, FixtureProjectNumber, cancellationToken);
        return IntegrationFixtureSnapshot.SelectCanonicalItems(snapshot);
    }

    [Fact]
    public async Task Listed_project_export_writes_a_numbered_snapshot_directory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new GitHubGraphQLClient(Token);
        var catalog = await CreateFixtureCatalogAsync(client, cancellationToken);
        var exporter = new ProjectExporter(client)
        {
            CompleteFieldCatalogProviderAsync = (_, _) => Task.FromResult(catalog),
        };

        var entries = await exporter.ListProjectsAsync(Org, includeClosed: false, cancellationToken);
        var entry = Assert.Single(entries, candidate =>
            candidate.Number == FixtureProjectNumber && !candidate.Closed);

        var outDirectory = Path.Combine(Path.GetTempPath(), "ghpmv-bulk-" + Guid.NewGuid().ToString("N"));
        try
        {
            var snapshot = await exporter.ExportAsync(Org, entry.Number, cancellationToken);
            var directory = Path.Combine(outDirectory, entry.Number.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await SnapshotFile.SaveAsync(snapshot, directory, cancellationToken);
            await MappingTemplates.WriteAsync([snapshot], outDirectory, cancellationToken: cancellationToken);

            Assert.True(File.Exists(Path.Combine(outDirectory, FixtureProjectNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), SnapshotFile.FileName)));
            Assert.True(File.Exists(Path.Combine(outDirectory, MappingTemplates.RepositoryMappingFileName)));

            var reloaded = await SnapshotFile.LoadAsync(Path.Combine(outDirectory, FixtureProjectNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)), cancellationToken);
            Assert.Equal(ProjectSnapshot.CurrentSchemaVersion, reloaded.SchemaVersion);
        }
        finally
        {
            try
            {
                Directory.Delete(outDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup in the temp folder; transient locks (AV scans
                // during parallel test runs) must not fail the test.
            }
        }
    }

    [Fact]
    public async Task Export_has_schema_version_and_project_metadata()
    {
        var snapshot = await ExportFixtureAsync();

        Assert.Equal(ProjectSnapshot.CurrentSchemaVersion, snapshot.SchemaVersion);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Project.Title));
        Assert.False(snapshot.Project.Closed);

        // Enriched fixture metadata: short description and a multiline README with emoji.
        Assert.Equal("gpm fixture project", snapshot.Project.ShortDescription);
        Assert.NotNull(snapshot.Project.Readme);
        Assert.Contains("\n", snapshot.Project.Readme, StringComparison.Ordinal);
        Assert.Contains("\uD83D\uDCE6", snapshot.Project.Readme, StringComparison.Ordinal); // 📦
    }

    [Fact]
    public async Task Export_captures_linked_repositories_and_leaves_collaborators_null()
    {
        var snapshot = await ExportFixtureAsync();

        Assert.NotNull(snapshot.LinkedRepositories);
        Assert.Contains(IntegrationTestSettings.FixtureRepositoryFullName, snapshot.LinkedRepositories, StringComparer.OrdinalIgnoreCase);

        // The GraphQL API has no read field for project collaborators, so exports leave them null.
        Assert.Null(snapshot.Collaborators);
    }

    [Fact]
    public async Task Export_enriches_the_fixture_issue_field_from_the_live_organization_catalog()
    {
        var snapshot = await ExportFixtureAsync();

        var teams = snapshot.Fields.Single(f => f.Name == "Fixture Teams");
        Assert.Equal("MULTI_SELECT", teams.DataType);
        Assert.NotNull(teams.IssueField);
        Assert.Equal("ALL", teams.IssueField.Visibility);
        Assert.Equal("Teams involved in the issue", teams.IssueField.Description);
        Assert.Equal(["Platform", "SDK", "Docs"], teams.Options!.Select(o => o.Name));
    }

    [Fact]
    public async Task Export_contains_three_views_with_expected_layouts()
    {
        var snapshot = await ExportFixtureAsync();

        Assert.Equal(3, snapshot.Views.Count);
        var table = Assert.Single(snapshot.Views, v => v.Name == "View 1");
        Assert.Equal("TABLE_LAYOUT", table.Layout);
        Assert.Equal("status:Todo", table.Filter);
        var sort = Assert.Single(table.SortByFields);
        Assert.Equal("Fixture Number", sort.Field);
        Assert.Equal("ASC", sort.Direction);
        Assert.Contains("Fixture Text", table.VisibleFields);
        Assert.Contains("Fixture Date", table.VisibleFields);

        var board = Assert.Single(snapshot.Views, v => v.Name == "Fixture Board");
        Assert.Equal("BOARD_LAYOUT", board.Layout);
        Assert.Equal("Fixture Select", Assert.Single(board.VerticalGroupByFields));
        Assert.Equal("Status", Assert.Single(board.GroupByFields)); // board swimlanes
        Assert.NotEmpty(board.VisibleFields);

        var roadmap = Assert.Single(snapshot.Views, v => v.Name == "Fixture Roadmap");
        Assert.Equal("ROADMAP_LAYOUT", roadmap.Layout);
        Assert.Empty(roadmap.VisibleFields);

        foreach (var view in snapshot.Views)
        {
            Assert.True(view.Number > 0);
            Assert.Null(view.Ui); // browser-only (M6)
        }
    }

    [Fact]
    public async Task Export_contains_expected_fixture_workflows_including_the_disabled_one()
    {
        var snapshot = await ExportFixtureAsync();

        string[] expectedWorkflows =
        [
            "Item closed",
            "Item reopened",
            "Pull request merged",
            "Auto-close issue",
            "Auto-add sub-issues to project",
            "Pull request linked to issue",
            "Item added to project",
            "Auto-add to project",
            "Auto-add secondary",
        ];

        var workflowNames = snapshot.Workflows.Select(w => w.Name).ToList();
        Assert.True(snapshot.Workflows.Count >= expectedWorkflows.Length);
        foreach (var name in expectedWorkflows)
        {
            Assert.Contains(name, workflowNames);
        }

        // Saved-but-disabled workflows are visible to GraphQL (unsaved ones are not).
        Assert.Contains(snapshot.Workflows, w => !w.Enabled && w.Name == "Code changes requested");

        Assert.All(snapshot.Workflows, w => Assert.Null(w.Ui)); // browser-only (M7)
    }

    [Fact]
    public async Task Export_contains_the_seven_canonical_fixture_items_with_positions()
    {
        var snapshot = await ExportFixtureAsync();

        Assert.Equal(7, snapshot.Items.Count);
        Assert.Equal(Enumerable.Range(0, 7), snapshot.Items.Select(i => i.Position));
        Assert.Equal(5, snapshot.Items.Count(i => i.Type == "DRAFT_ISSUE"));

        // Issue and PR items carry their repository and number.
        var issue = Assert.Single(snapshot.Items, i => i.Type == "ISSUE");
        Assert.Equal(IntegrationTestSettings.FixtureRepositoryFullName, issue.Repository);
        Assert.Equal(1, issue.Number);
        Assert.False(issue.IsArchived);

        var pullRequest = Assert.Single(snapshot.Items, i => i.Type == "PULL_REQUEST");
        Assert.Equal(IntegrationTestSettings.FixtureRepositoryFullName, pullRequest.Repository);
        Assert.True(pullRequest.Number > 0);

        // The archived draft is exported with its archived state.
        var archived = Assert.Single(snapshot.Items, i => i.IsArchived);
        Assert.Equal("DRAFT_ISSUE", archived.Type);
        Assert.Equal("Fixture archived draft", archived.Draft?.Title);

        // The assigned draft carries its assignee login.
        var assigned = Assert.Single(snapshot.Items, i => i.Draft?.Title == "Fixture assigned draft");
        var assignee = Assert.Single(assigned.Draft!.Assignees);
        Assert.False(string.IsNullOrWhiteSpace(assignee));

        // Every draft carries its Title as a text field value.
        Assert.All(snapshot.Items.Where(i => i.Type == "DRAFT_ISSUE"), item =>
            Assert.Contains(item.FieldValues, v => v.FieldName == "Title" && !string.IsNullOrEmpty(v.Text)));
    }

    [Fact]
    public async Task Export_captures_all_field_value_types_on_the_fixture_drafts()
    {
        var snapshot = await ExportFixtureAsync();

        var draft1 = Assert.Single(snapshot.Items, i => i.Draft?.Title == "Fixture draft 1");
        var draft2 = Assert.Single(snapshot.Items, i => i.Draft?.Title == "Fixture draft 2");
        var draft3 = Assert.Single(snapshot.Items, i => i.Draft?.Title == "Fixture draft 3");

        // TEXT round-trips non-ASCII (Japanese, accents, emoji) and markup-like characters.
        Assert.Equal("日本語テキスト & <special> chars", ValueOf(draft1, "Fixture Text")?.Text);
        Assert.Equal("Café emoji 🚀 – em dash", ValueOf(draft2, "Fixture Text")?.Text);
        Assert.Equal("plain ascii text", ValueOf(draft3, "Fixture Text")?.Text);

        // NUMBER covers fractional, negative and zero values (zero must not export as null).
        Assert.Equal(3.14, ValueOf(draft1, "Fixture Number")?.Number);
        Assert.Equal(-42d, ValueOf(draft2, "Fixture Number")?.Number);
        Assert.Equal(0d, ValueOf(draft3, "Fixture Number")?.Number);

        // DATE values are exported as yyyy-MM-dd.
        foreach (var draft in (ItemSnapshot[])[draft1, draft2, draft3])
        {
            var date = ValueOf(draft, "Fixture Date")?.Date;
            Assert.Matches("^\\d{4}-\\d{2}-\\d{2}$", date);
        }

        // SINGLE_SELECT covers every option once.
        Assert.Equal("Alpha", ValueOf(draft1, "Fixture Select")?.SingleSelectOptionName);
        Assert.Equal("Beta", ValueOf(draft2, "Fixture Select")?.SingleSelectOptionName);
        Assert.Equal("Gamma", ValueOf(draft3, "Fixture Select")?.SingleSelectOptionName);

        // ITERATION includes a completed iteration (Sprint 0) as a value.
        Assert.Equal("Sprint 0", ValueOf(draft1, "Fixture Sprint")?.IterationTitle);
        Assert.Equal("Sprint 1", ValueOf(draft2, "Fixture Sprint")?.IterationTitle);
        Assert.Equal("Sprint 2", ValueOf(draft3, "Fixture Sprint")?.IterationTitle);

        var issue = Assert.Single(snapshot.Items, item => item.Type == "ISSUE");
        Assert.Equal(["Platform", "SDK"], ValueOf(issue, "Fixture Teams")?.MultiSelectOptionNames);
    }

    private static FieldValueSnapshot? ValueOf(ItemSnapshot item, string fieldName)
        => item.FieldValues.FirstOrDefault(v => v.FieldName == fieldName);

    private static async Task<ProjectFieldCatalog> CreateFixtureCatalogAsync(
        GitHubGraphQLClient client,
        CancellationToken cancellationToken)
    {
        var knownSnapshot = await IntegrationFixtureSnapshot.CreateKnownAsync(client, cancellationToken);
        var catalog = IntegrationFixtureSnapshot.CreateFieldCatalog(knownSnapshot);
        return catalog with
        {
            Entries = catalog.Entries.Select(entry =>
                string.Equals(entry.Field.Name, "Fixture Teams", StringComparison.Ordinal)
                    ? entry with { Field = entry.Field with { Options = [], IssueField = null } }
                    : entry).ToArray(),
        };
    }
}
