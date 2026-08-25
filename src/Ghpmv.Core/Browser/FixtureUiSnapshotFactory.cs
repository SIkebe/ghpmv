using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Browser;

/// <summary>
/// Creates the View and Workflow portion of the standard integration-test fixture.
/// The returned snapshot is intentionally minimal: it contains just enough field,
/// View and Workflow metadata for the GraphQL View importer and the browser enrichment
/// importers to configure a project whose fields and repository were created by
/// <c>ghpmv setup --fixture</c>.
/// </summary>
public static class FixtureUiSnapshotFactory
{
    public static ProjectSnapshot Create(string repositoryName = "fixture-repo")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryName);

        return new ProjectSnapshot
        {
            SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
            Project = new ProjectInfoSnapshot
            {
                Title = "gpm-fixture-ui",
                ShortDescription = "UI fixture settings for ghpmv integration tests",
                Readme = null,
                Public = false,
                Closed = false,
            },
            Fields = CreateFields(),
            Views = CreateViews(),
            Workflows = CreateWorkflows(repositoryName),
            Items = [],
        };
    }

    /// <summary>
    /// Creates the standard fixture UI snapshot with the deliberate field-sum drift
    /// used by the browser E2E negative test.
    /// </summary>
    public static ProjectSnapshot CreateFieldSumDrift(string repositoryName = "fixture-repo")
    {
        var snapshot = Create(repositoryName);
        return snapshot with
        {
            Views = snapshot.Views.Select(view =>
                string.Equals(view.Name, "View 1", StringComparison.Ordinal)
                    ? view with { Ui = view.Ui! with { FieldSum = ["Count", "Fixture Number"] } }
                    : view).ToList(),
        };
    }

    /// <summary>Creates deliberate title-truncation drift while preserving Roadmap date display.</summary>
    public static ProjectSnapshot CreateRoadmapDisplayDrift(string repositoryName = "fixture-repo")
    {
        var snapshot = Create(repositoryName);
        return snapshot with
        {
            Views = snapshot.Views.Select(view =>
                string.Equals(view.Name, "Fixture Roadmap", StringComparison.Ordinal)
                    ? view with
                    {
                        Ui = view.Ui! with
                        {
                            Roadmap = view.Ui.Roadmap! with
                            {
                                TruncateTitles = false,
                                ShowDateFields = true,
                            },
                        },
                    }
                    : view).ToList(),
        };
    }

    /// <summary>
    /// Creates deliberate target drift for every supported field-default type. The negative
    /// Number default is cleared so the same operation also exercises explicit removal.
    /// </summary>
    public static ProjectSnapshot CreateFieldDefaultDrift(string repositoryName = "fixture-repo")
    {
        var snapshot = Create(repositoryName);
        return snapshot with
        {
            Fields = snapshot.Fields.Select(field => field.Name switch
            {
                "Fixture Text" => field with
                {
                    DefaultValue = new FieldDefaultValueSnapshot { Text = "drifted text" },
                },
                "Fixture Number" => field with
                {
                    DefaultValue = new FieldDefaultValueSnapshot(),
                },
                "Fixture Number 2" => field with
                {
                    DefaultValue = new FieldDefaultValueSnapshot { Number = 99 },
                },
                "Fixture Select" => field with
                {
                    DefaultValue = new FieldDefaultValueSnapshot { SingleSelectOptionName = "Gamma" },
                },
                _ => field,
            }).ToList(),
        };
    }

    private static IReadOnlyList<FieldSnapshot> CreateFields() =>
    [
        new FieldSnapshot { Name = "Title", DataType = "TITLE" },
        new FieldSnapshot { Name = "Assignees", DataType = "ASSIGNEES" },
        new FieldSnapshot { Name = "Status", DataType = "SINGLE_SELECT" },
        new FieldSnapshot
        {
            Name = "Fixture Text",
            DataType = "TEXT",
            DefaultValue = new FieldDefaultValueSnapshot { Text = "既定値 🌏" },
        },
        new FieldSnapshot
        {
            Name = "Fixture Number",
            DataType = "NUMBER",
            DefaultValue = new FieldDefaultValueSnapshot { Number = -7 },
        },
        new FieldSnapshot
        {
            Name = "Fixture Number 2",
            DataType = "NUMBER",
            DefaultValue = new FieldDefaultValueSnapshot { Number = 0 },
        },
        new FieldSnapshot { Name = "Fixture Date", DataType = "DATE" },
        new FieldSnapshot
        {
            Name = "Fixture Select",
            DataType = "SINGLE_SELECT",
            Options =
            [
                new SingleSelectOptionSnapshot { Id = "alpha", Name = "Alpha", Color = "RED", Description = "First" },
                new SingleSelectOptionSnapshot { Id = "beta", Name = "Beta", Color = "BLUE", Description = "Second" },
                new SingleSelectOptionSnapshot { Id = "gamma", Name = "Gamma", Color = "GREEN", Description = "Third" },
            ],
            DefaultValue = new FieldDefaultValueSnapshot { SingleSelectOptionName = "Beta" },
        },
        new FieldSnapshot { Name = "Fixture Sprint", DataType = "ITERATION" },
        new FieldSnapshot
        {
            Name = "Fixture Teams",
            DataType = "MULTI_SELECT",
            IssueField = new IssueFieldConfigurationSnapshot
            {
                Description = "Teams involved in the issue",
                Visibility = "ALL",
            },
        },
    ];

    private static IReadOnlyList<ViewSnapshot> CreateViews() =>
    [
        new ViewSnapshot
        {
            Number = 1,
            TabPosition = 1,
            Name = "View 1",
            Layout = "TABLE_LAYOUT",
            Filter = "status:Todo",
            GroupByFields = ["Status"],
            SortByFields = [new SortByFieldSnapshot { Field = "Fixture Number", Direction = "ASC" }],
            VerticalGroupByFields = [],
            VisibleFields = ["Title", "Assignees", "Status", "Fixture Text", "Fixture Date", "Fixture Select", "Fixture Sprint"],
            Ui = new ViewUiSnapshot
            {
                SliceBy = "Fixture Select",
                FieldSum = ["Count", "Fixture Number", "Fixture Number 2"],
            },
        },
        new ViewSnapshot
        {
            Number = 2,
            TabPosition = 2,
            Name = "Fixture Board",
            Layout = "BOARD_LAYOUT",
            Filter = null,
            GroupByFields = ["Status"],
            SortByFields = [],
            VerticalGroupByFields = ["Fixture Select"],
            VisibleFields = [],
            Ui = new ViewUiSnapshot
            {
                FieldSum = ["Fixture Number"],
            },
        },
        new ViewSnapshot
        {
            Number = 3,
            TabPosition = 0,
            Name = "Fixture Roadmap",
            Layout = "ROADMAP_LAYOUT",
            Filter = null,
            GroupByFields = ["Status"],
            SortByFields = [],
            VerticalGroupByFields = [],
            VisibleFields = [],
            Ui = new ViewUiSnapshot
            {
                FieldSum = ["Fixture Number 2"],
                Roadmap = new RoadmapSettingsSnapshot
                {
                    StartField = "Fixture Date",
                    TargetField = "Fixture Sprint end",
                    Zoom = "Quarter",
                    Markers = ["Fixture Date"],
                    TruncateTitles = true,
                    ShowDateFields = true,
                },
            },
        },
        new ViewSnapshot
        {
            Number = 4,
            TabPosition = 3,
            Name = "Fixture Empty Sums",
            Layout = "TABLE_LAYOUT",
            Filter = null,
            GroupByFields = ["Status"],
            SortByFields = [],
            VerticalGroupByFields = [],
            VisibleFields = [],
            Ui = new ViewUiSnapshot
            {
                FieldSum = [],
            },
        },
        new ViewSnapshot
        {
            Number = 5,
            TabPosition = 4,
            Name = "Fixture Roadmap Dates Hidden",
            Layout = "ROADMAP_LAYOUT",
            Filter = null,
            GroupByFields = ["Status"],
            SortByFields = [],
            VerticalGroupByFields = [],
            VisibleFields = [],
            Ui = new ViewUiSnapshot
            {
                FieldSum = ["Fixture Number 2"],
                Roadmap = new RoadmapSettingsSnapshot
                {
                    StartField = "Fixture Date",
                    TargetField = "Fixture Sprint end",
                    Zoom = "Quarter",
                    Markers = ["Fixture Date"],
                    TruncateTitles = true,
                    ShowDateFields = false,
                },
            },
        },
    ];

    private static IReadOnlyList<WorkflowSnapshot> CreateWorkflows(string repositoryName) =>
    [
        new WorkflowSnapshot
        {
            Number = 1,
            Name = "Item added to project",
            Enabled = true,
            Ui = new WorkflowUiSnapshot
            {
                ContentTypes = ["ISSUE", "PULL_REQUEST"],
                StatusValue = "Todo",
            },
        },
        new WorkflowSnapshot
        {
            Number = 2,
            Name = "Item reopened",
            Enabled = true,
            Ui = new WorkflowUiSnapshot
            {
                ContentTypes = ["ISSUE", "PULL_REQUEST"],
                StatusValue = "Todo",
            },
        },
        new WorkflowSnapshot
        {
            Number = 3,
            Name = "Item closed",
            Enabled = true,
            Ui = new WorkflowUiSnapshot
            {
                ContentTypes = ["ISSUE", "PULL_REQUEST"],
                StatusValue = "Done",
            },
        },
        new WorkflowSnapshot
        {
            Number = 4,
            Name = "Code changes requested",
            Enabled = false,
            Ui = new WorkflowUiSnapshot
            {
                StatusValue = "In Progress",
            },
        },
        new WorkflowSnapshot
        {
            Number = 5,
            Name = "Code review approved",
            Enabled = true,
            Ui = new WorkflowUiSnapshot
            {
                StatusValue = "In Progress",
            },
        },
        new WorkflowSnapshot
        {
            Number = 6,
            Name = "Pull request merged",
            Enabled = true,
            Ui = new WorkflowUiSnapshot
            {
                StatusValue = "Done",
            },
        },
        new WorkflowSnapshot
        {
            Number = 7,
            Name = "Auto-add to project",
            Enabled = true,
            Ui = new WorkflowUiSnapshot
            {
                Repository = repositoryName,
                Filter = "is:issue is:open",
            },
        },
        new WorkflowSnapshot
        {
            Number = 8,
            Name = "Auto-add secondary",
            Enabled = true,
            Ui = new WorkflowUiSnapshot
            {
                Repository = repositoryName,
                Filter = "is:issue label:bug",
            },
        },
    ];
}
