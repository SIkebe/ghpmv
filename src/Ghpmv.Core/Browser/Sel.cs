using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Ghpmv.Core.Browser;

/// <summary>
/// Selector registry for the GitHub Projects web UI — the single source of truth
/// (no selectors inline in logic). All entries were confirmed against the real UI
/// during D0 (docs/ui-maps/projects-ui-discovery.md, 2026-07-05) unless noted.
/// </summary>
internal static class Sel
{
    // Logged-in header avatar button (github.com, D0 login detection).
    private static readonly Regex AvatarButtonName = new("Open user navigation menu");

    // Enterprise SSO interstitial heading ("Single sign-on to <enterprise>", M7 discovery).
    private static readonly Regex SsoHeadingName = new("^Single sign-on to ");

    // Filter-bar "View" button. D0: once a setting is changed the accessible name
    // becomes "Unsaved changes View", so an exact "View" match only works before edits.
    private static readonly Regex ViewMenuButtonName = new("^(Unsaved changes )?View$");

    /// <summary>Filter-bar "View" button that opens the view configuration menu.</summary>
    public static ILocator ViewMenuButton(IPage page)
        => page.GetByRole(AriaRole.Button, new() { NameRegex = ViewMenuButtonName }).First;

    /// <summary>The most recently opened menu.</summary>
    public static ILocator OpenMenu(IPage page) => page.GetByRole(AriaRole.Menu).Last;

    /// <summary>
    /// Configuration menu item. D0: label and current value are combined in the accessible
    /// name ("Group by: &lt;value&gt;"), so the item is located by label prefix.
    /// </summary>
    public static ILocator ConfigurationMenuItem(ILocator menu, string label)
        => menu.GetByRole(AriaRole.Menuitem, new() { NameRegex = new Regex($"^{Regex.Escape(label)}:") });

    /// <summary>Checkable entries in a View configuration child menu.</summary>
    public static ILocator CheckboxOptions(ILocator menu)
        => menu.GetByRole(AriaRole.Menuitemcheckbox).Or(menu.GetByRole(AriaRole.Option));

    /// <summary>A direct checkbox in the parent View configuration menu.</summary>
    public static ILocator ViewOptionCheckbox(ILocator menu, string name)
        => menu.GetByRole(AriaRole.Menuitemcheckbox, new() { Name = name, Exact = true });

    /// <summary>View tab by name (prefix match — an unsaved-changes dot can alter the suffix).</summary>
    public static ILocator ViewTab(IPage page, string name)
        => page.GetByRole(AriaRole.Tab, new() { NameRegex = new Regex($"^{Regex.Escape(name)}") });

    /// <summary>A saved View tab used as a drag source or drop target, identified by its stable URL number.</summary>
    public static ILocator DraggableViewTab(IPage page, int viewNumber)
    {
        var number = viewNumber.ToString(CultureInfo.InvariantCulture);
        return page.Locator(
            $"[role='tab'][href$='/views/{number}'], [role='tab'][href*='/views/{number}?']").First;
    }

    /// <summary>All saved View tabs in their current DOM order (excludes the New view control).</summary>
    public static ILocator SavedViewTabs(IPage page)
        => page.GetByRole(AriaRole.Navigation, new() { Name = "Select view", Exact = true })
            .Locator("[role='tab'][href*='/views/']");

    /// <summary>"Save view" button (settings changes require an explicit save, D0).</summary>
    public static ILocator SaveViewButton(IPage page)
        => page.GetByRole(AriaRole.Button, new() { Name = "Save view", Exact = true }).Last;

    /// <summary>Confirmation alertdialog ("Save display options for &lt;view&gt;?", D0).</summary>
    public static ILocator SaveConfirmDialog(IPage page) => page.GetByRole(AriaRole.Alertdialog).Last;

    /// <summary>Status exposed while the current View has client-side changes that are not saved.</summary>
    public static ILocator UnsavedChangesStatus(IPage page)
        => page.GetByRole(AriaRole.Status, new() { Name = "Unsaved changes", Exact = true }).Last;

    /// <summary>Visible grouped Table/Roadmap header contents containing count and aggregate labels.</summary>
    public static ILocator GroupHeaderContents(IPage page)
        => page.Locator("[class*='group-header-module__groupHeaderContent']:visible");

    /// <summary>Visible numeric Field sum labels rendered inside grouped Table/Roadmap headers.</summary>
    public static ILocator GroupHeaderAggregateLabels(IPage page)
        => page.Locator("[class*='aggregate-labels-module__Label']:visible");

    /// <summary>The title rendered inside a Roadmap pill rather than the fixed left-hand table.</summary>
    public static ILocator RoadmapPillTitle(IPage page, string title)
        => page.Locator("[class*='roadmap-pill-module__SanitizedHtml']")
            .Filter(new() { HasText = title })
            .First;

    /// <summary>The Roadmap item/card containing an item-title locator.</summary>
    public static ILocator RoadmapItem(ILocator title)
        => title.Locator(
            "xpath=ancestor::*[@role='row' or @role='listitem' or contains(@data-testid,'roadmap-item') or contains(@class,'roadmap-item') or contains(@class,'RoadmapItem')][1]");

    /// <summary>Semantic date/time elements rendered within one Roadmap item.</summary>
    public static ILocator RoadmapItemDateElements(ILocator item)
        => item.Locator("time:visible, relative-time:visible");

    /// <summary>"Select date fields" dialog opened from the "Dates" configuration item (Roadmap).</summary>
    public static ILocator DateFieldsDialog(IPage page)
        => page.GetByRole(AriaRole.Dialog, new() { Name = "Select date fields" });

    /// <summary>"Start date" / "Target date" group inside the date-fields dialog.</summary>
    public static ILocator DateFieldGroup(ILocator dialog, string groupName)
        => dialog.GetByRole(AriaRole.Group, new() { Name = groupName });

    /// <summary>Logged-in avatar button in the page header.</summary>
    public static ILocator AvatarButton(IPage page)
        => page.GetByRole(AriaRole.Button, new() { NameRegex = AvatarButtonName });

    /// <summary>Enterprise SSO interstitial heading ("Single sign-on to &lt;enterprise&gt;").</summary>
    public static ILocator SsoHeading(IPage page)
        => page.GetByRole(AriaRole.Heading, new() { NameRegex = SsoHeadingName });

    /// <summary>"Continue" button of the SSO interstitial (re-authenticates via the stored IdP session).</summary>
    public static ILocator SsoContinueButton(IPage page)
        => page.GetByRole(AriaRole.Button, new() { Name = "Continue", Exact = true });

    // === Project field defaults (implementation contract 2026-08-24) ===

    private static readonly Regex FieldDefaultControlName = new("^Default value($|:)");
    private static readonly Regex CreateDraftOptionName = new("^Create a draft");
    /// <summary>A field entry on the Project settings page.</summary>
    public static ILocator FieldSettingsEntry(IPage page, string fieldName)
        => page.GetByRole(AriaRole.Link, new() { Name = fieldName, Exact = true })
            .Or(page.GetByRole(AriaRole.Button, new() { Name = fieldName, Exact = true }))
            .First;

    /// <summary>Heading of one custom field's settings page.</summary>
    public static ILocator FieldSettingsHeading(IPage page, string fieldName)
        => page.GetByRole(AriaRole.Heading, new()
        {
            Name = $"{fieldName} field settings",
            Exact = true,
            Level = 2,
        });

    /// <summary>Text, number, or single-select control labelled "Default value".</summary>
    public static ILocator FieldDefaultControl(IPage page)
        => page.GetByRole(AriaRole.Textbox, new() { NameRegex = FieldDefaultControlName })
            .Or(page.GetByRole(AriaRole.Spinbutton, new() { NameRegex = FieldDefaultControlName }))
            .Or(page.GetByRole(AriaRole.Combobox, new() { NameRegex = FieldDefaultControlName }))
            .Or(page.GetByRole(AriaRole.Button, new() { NameRegex = FieldDefaultControlName }))
            .First;

    /// <summary>Project item-entry combobox used to create a draft.</summary>
    public static ILocator ProjectItemEntry(IPage page)
        => page.GetByRole(AriaRole.Combobox, new()
        {
            Name = "Start typing to create an item, or type hashtag to select a repository",
            Exact = true,
        }).First;

    /// <summary>"Create a draft" option in the Project item discovery menu.</summary>
    public static ILocator CreateDraftOption(IPage page)
        => page.GetByRole(AriaRole.Option, new() { NameRegex = CreateDraftOptionName });

    /// <summary>Actions button for one Single-select option.</summary>
    public static ILocator FieldOptionActionsButton(IPage page, string optionName)
        => page.GetByRole(AriaRole.Button, new()
        {
            Name = $"Open field actions for {optionName}",
            Exact = true,
        });

    /// <summary>Open actions menu for one Single-select option.</summary>
    public static ILocator FieldOptionActionsMenu(IPage page, string optionName)
        => page.GetByRole(AriaRole.Menu, new()
        {
            Name = $"Open field actions for {optionName}",
            Exact = true,
        });

    // === Workflows (M7 discovery, 2026-07-05) ===

    // Saved Auto-add entries carry a kebab button whose label is appended to the link name.
    private const string WorkflowOptionsSuffix = "( Open workflow options)?$";

    // Edit-mode save button: "Save workflow" (saved workflow) / "Save and turn on workflow" (unsaved).
    private static readonly Regex SaveWorkflowName = new("^Save (workflow|and turn on workflow)$");

    // View/edit mode "When" value button, e.g. "When an item is closed : issue, pull request".
    // Prefix-only: the " : <value>" suffix disappears when the binding is cleared (e.g.
    // after the importer overwrote the Status options), and accessible names can contain
    // line breaks that defeat a "$"-anchored pattern.
    private static readonly Regex WhenButtonName = new("^When ");

    // Auto-add trigger heading. The repository button's accessible name differs between
    // empty and configured workflows, so locate it relative to this stable heading.
    private static readonly Regex RepositoryButtonName = new("^When the filter matches a new or updated item");

    // "Set value : <status>" button (text "Status: <status>"). With a cleared binding the
    // name becomes "Set valueundefined" (GitHub UI quirk) — match by prefix only.
    private static readonly Regex SetValueButtonName = new("^Set value");

    // Option-picker overlays: dialog "Select an item" / "Select items" / "Select a repository".
    private static readonly Regex SelectDialogName = new("^Select ");

    /// <summary>Sidebar "Default workflows" list on the workflows page.</summary>
    public static ILocator WorkflowsSidebar(IPage page)
        => page.GetByRole(AriaRole.List, new() { Name = "Default workflows" });

    /// <summary>Sidebar workflow link by name (saved Auto-add links append "Open workflow options").</summary>
    public static ILocator WorkflowLink(IPage page, string name)
        => WorkflowsSidebar(page).GetByRole(AriaRole.Link, new() { NameRegex = new Regex($"^{Regex.Escape(name)}{WorkflowOptionsSuffix}") });

    /// <summary>The h2 heading of the currently displayed workflow.</summary>
    public static ILocator WorkflowHeading(IPage page, string name)
        => page.GetByRole(AriaRole.Heading, new() { Name = name, Exact = true, Level = 2 });

    /// <summary>Header controls belonging to the detail pane identified by its workflow heading.</summary>
    public static ILocator WorkflowHeader(IPage page, string name)
        => WorkflowHeading(page, name).Locator("xpath=../..");

    /// <summary>
    /// Enable/disable control. The accessible name is not stable, so fall back to the
    /// single stateful control in the workflow detail pane.
    /// </summary>
    public static ILocator WorkflowToggle(IPage page, string name)
        => page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true })
            .Or(page.GetByRole(AriaRole.Switch, new() { Name = name, Exact = true }))
            .Or(page.GetByRole(AriaRole.Checkbox, new() { Name = name, Exact = true }))
            .Or(page.GetByRole(AriaRole.Main).Locator(
                "button[aria-pressed]:visible, [role='switch'][aria-checked]:visible, " +
                "[role='checkbox'][aria-checked]:visible, input[type='checkbox']:visible"));

    /// <summary>"Edit" button scoped to a specific workflow detail pane.</summary>
    public static ILocator EditWorkflowButton(IPage page, string name)
        => WorkflowHeader(page, name).GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true });

    /// <summary>Edit-mode save button scoped to a specific workflow detail pane.</summary>
    public static ILocator SaveWorkflowButton(IPage page, string name)
        => WorkflowHeader(page, name).GetByRole(AriaRole.Button, new() { NameRegex = SaveWorkflowName });

    /// <summary>"When ... : &lt;value&gt;" buttons (content types, Auto-close status, Auto-add repository).</summary>
    public static ILocator WorkflowWhenButtons(IPage page)
        => page.GetByRole(AriaRole.Button, new() { NameRegex = WhenButtonName });

    /// <summary>Auto-add repository picker button in the block identified by its trigger heading.</summary>
    public static ILocator WorkflowRepositoryButton(IPage page)
        => WhenFilterMatchesHeading(page)
            .Locator("xpath=../..")
            .GetByRole(AriaRole.Button)
            .First;

    /// <summary>Auto-add "When the filter matches..." section heading (h3) — marks Auto-add pages.</summary>
    public static ILocator WhenFilterMatchesHeading(IPage page)
        => page.GetByRole(AriaRole.Heading, new() { NameRegex = RepositoryButtonName });

    /// <summary>"Set value : &lt;status&gt;" button (disabled in view mode; text "Status: &lt;status&gt;").</summary>
    public static ILocator WorkflowSetValueButton(IPage page)
        => page.GetByRole(AriaRole.Button, new() { NameRegex = SetValueButtonName });

    /// <summary>Auto-add/Auto-archive filter: read-only textbox in view mode (value readable while disabled).</summary>
    public static ILocator WorkflowFiltersTextbox(IPage page)
        => page.GetByRole(AriaRole.Textbox, new() { Name = "Filters" });

    /// <summary>Auto-add/Auto-archive filter input in edit mode (combobox inside form "Filter").</summary>
    public static ILocator WorkflowFiltersCombobox(IPage page)
        => page.GetByRole(AriaRole.Combobox, new() { Name = "Filters" });

    /// <summary>Option-picker overlay ("Select an item" / "Select items" / "Select a repository").</summary>
    public static ILocator WorkflowSelectDialog(IPage page)
        => page.GetByRole(AriaRole.Dialog, new() { NameRegex = SelectDialogName });

    /// <summary>An option inside a workflow option-picker dialog.</summary>
    public static ILocator WorkflowDialogOption(ILocator dialog, string name)
        => dialog.GetByRole(AriaRole.Option, new() { Name = name, Exact = true });

    /// <summary>Search input of the "Select a repository" picker dialog.</summary>
    public static ILocator RepositorySearchCombobox(ILocator dialog)
        => dialog.GetByRole(AriaRole.Combobox, new() { Name = "Search repositories" });

    /// <summary>Kebab button inside a saved Auto-add sidebar link (appears on hover).</summary>
    public static ILocator WorkflowOptionsKebab(ILocator workflowLink)
        => workflowLink.GetByRole(AriaRole.Button, new() { Name = "Open workflow options" });

    /// <summary>"Duplicate workflow" item of the kebab menu.</summary>
    public static ILocator DuplicateWorkflowMenuItem(IPage page)
        => page.GetByRole(AriaRole.Menuitem, new() { Name = "Duplicate workflow" });

    /// <summary>"Duplicate workflow" name-prompt dialog (textbox "Workflow name" + button "Duplicate").</summary>
    public static ILocator DuplicateWorkflowDialog(IPage page)
        => page.GetByRole(AriaRole.Dialog, new() { Name = "Duplicate workflow" });

    /// <summary>"Edit workflow name" button next to the workflow heading.</summary>
    public static ILocator EditWorkflowNameButton(IPage page)
        => page.GetByRole(AriaRole.Button, new() { Name = "Edit workflow name" });

    /// <summary>"Edit workflow name" dialog (textbox "Workflow name" + Save/Cancel).</summary>
    public static ILocator EditWorkflowNameDialog(IPage page)
        => page.GetByRole(AriaRole.Dialog, new() { Name = "Edit workflow name" });

    /// <summary>The "Workflow name" textbox inside a workflow name dialog.</summary>
    public static ILocator WorkflowNameTextbox(ILocator dialog)
        => dialog.GetByRole(AriaRole.Textbox, new() { Name = "Workflow name" });
}
