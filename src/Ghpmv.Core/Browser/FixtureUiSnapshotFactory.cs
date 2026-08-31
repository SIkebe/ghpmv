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
                Template = false,
            },
            Fields = CreateFields(),
            Views = CreateViews(),
            Workflows = CreateWorkflows(repositoryName),
            Items = [],
            StatusUpdates = [],
            LinkedRepositories = [],
            LinkedTeams = [],
        };
    }

    /// <summary>
    /// Creates the standard fixture UI snapshot with deliberate field-sum and Board
    /// column-limit drift used by the browser E2E negative test.
    /// </summary>
    public static ProjectSnapshot CreateFieldSumDrift(string repositoryName = "fixture-repo")
    {
        var snapshot = Create(repositoryName);
        return snapshot with
        {
            Views = snapshot.Views.Select(view => view.Name switch
            {
                "View 1" => view with
                {
                    Ui = view.Ui! with { FieldSum = ["Count", "Fixture Number"] },
                },
                "Fixture Board" => view with
                {
                    Ui = view.Ui! with
                    {
                        BoardColumnLimits =
                        [
                            new BoardColumnLimitSnapshot
                            {
                                FieldName = "Fixture Select",
                                SingleSelectOptionName = "Alpha",
                                Limit = 5,
                            },
                        ],
                    },
                },
                _ => view,
            }).ToList(),
        };
    }

    /// <summary>Creates title-only Roadmap display drift for every Roadmap View.</summary>
    public static ProjectSnapshot CreateRoadmapDisplayDrift(string repositoryName = "fixture-repo")
        => CreateRoadmapDisplayDrift(repositoryName, truncateTitles: false, showDateFields: false);

    /// <summary>Creates date-only Roadmap display drift for every Roadmap View.</summary>
    public static ProjectSnapshot CreateRoadmapDateDisplayDrift(string repositoryName = "fixture-repo")
        => CreateRoadmapDisplayDrift(repositoryName, truncateTitles: true, showDateFields: true);

    private static ProjectSnapshot CreateRoadmapDisplayDrift(
        string repositoryName,
        bool truncateTitles,
        bool showDateFields)
    {
        var snapshot = Create(repositoryName);
        return snapshot with
        {
            Views = snapshot.Views.Select(view =>
                string.Equals(view.Layout, "ROADMAP_LAYOUT", StringComparison.Ordinal)
                    ? view with
                    {
                        Ui = view.Ui! with
                        {
                            Roadmap = view.Ui.Roadmap! with
                            {
                                TruncateTitles = truncateTitles,
                                ShowDateFields = showDateFields,
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
        new FieldSnapshot
        {
            Name = "Fixture Sprint",
            DataType = "ITERATION",
            IterationConfiguration = new IterationConfigurationSnapshot
            {
                Duration = 14,
                StartDay = 1,
                CompletedIterations =
                [
                    new IterationSnapshot
                    {
                        Id = "sprint-0",
                        Title = "Sprint 0",
                        StartDate = "2026-01-05",
                        Duration = 14,
                    },
                ],
                Iterations =
                [
                    new IterationSnapshot
                    {
                        Id = "sprint-1",
                        Title = "Sprint 1",
                        StartDate = "2026-01-19",
                        Duration = 14,
                    },
                    new IterationSnapshot
                    {
                        Id = "sprint-2",
                        Title = "Sprint 2",
                        StartDate = "2026-02-02",
                        Duration = 14,
                    },
                    new IterationSnapshot
                    {
                        Id = "sprint-3",
                        Title = "Sprint 3",
                        StartDate = "2026-02-16",
                        Duration = 14,
                    },
                ],
            },
        },
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
                BoardColumnLimits =
                [
                    new BoardColumnLimitSnapshot
                    {
                        FieldName = "Fixture Select",
                        SingleSelectOptionName = "Alpha",
                        Limit = 1,
                    },
                    new BoardColumnLimitSnapshot
                    {
                        FieldName = "Fixture Select",
                        SingleSelectOptionName = "Beta",
                        Limit = 2,
                    },
                ],
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
                    ShowDateFields = false,
                },
            },
        },
        new ViewSnapshot
        {
            Number = 4,
            TabPosition = 4,
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
            TabPosition = 5,
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
        new ViewSnapshot
        {
            Number = 6,
            TabPosition = 3,
            Name = "Fixture Iteration Board",
            Layout = "BOARD_LAYOUT",
            Filter = null,
            GroupByFields = [],
            SortByFields = [],
            VerticalGroupByFields = ["Fixture Sprint"],
            VisibleFields = [],
            Ui = new ViewUiSnapshot
            {
                FieldSum = [],
                BoardColumnLimits =
                [
                    new BoardColumnLimitSnapshot
                    {
                        FieldName = "Fixture Sprint",
                        IterationTitle = "Sprint 0",
                        Limit = 1,
                    },
                    new BoardColumnLimitSnapshot
                    {
                        FieldName = "Fixture Sprint",
                        IterationTitle = "Sprint 1",
                        Limit = 3,
                    },
                ],
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
