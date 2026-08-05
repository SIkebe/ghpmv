using System.Globalization;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Snapshot;
using Microsoft.Playwright;

namespace Ghpmv.Core.Browser;

/// <summary>
/// Browser import of View settings not writable through GraphQL. <see cref="EnrichAsync"/>
/// applies those settings to API-created Views. Settings that cannot be applied are
/// collected as warnings.
/// </summary>
public sealed class ViewUiImporter
{
    private static readonly AriaRole[] OptionRoles =
    [
        AriaRole.Menuitemradio,
        AriaRole.Option,
        AriaRole.Menuitem,
    ];

    private static readonly string[] RoadmapDateSuffixes = [" start", " end"];

    private readonly BrowserSession _session;
    private readonly List<string> _warnings = [];

    public ViewUiImporter(BrowserSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>Invoked with a human-readable progress message per view.</summary>
    public Action<string>? OnProgress { get; set; }

    /// <summary>Warnings collected while importing (settings that could not be applied).</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>
    /// Pure pre-flight check: warns about view settings that reference fields missing from
    /// the snapshot and about sort keys beyond the first (only one key is applied in v1).
    /// </summary>
    public static IReadOnlyList<string> CollectPreflightWarnings(ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var warnings = new List<string>();
        var fieldNames = new HashSet<string>(snapshot.Fields.Select(f => f.Name), StringComparer.Ordinal);
        foreach (var view in snapshot.Views)
        {
            foreach (var field in view.GroupByFields)
            {
                WarnIfMissing(warnings, fieldNames, view.Name, "group-by", field);
            }

            foreach (var field in view.VerticalGroupByFields)
            {
                WarnIfMissing(warnings, fieldNames, view.Name, "column-by", field);
            }

            foreach (var sort in view.SortByFields)
            {
                WarnIfMissing(warnings, fieldNames, view.Name, "sort-by", sort.Field);
            }

            if (view.SortByFields.Count > 1)
            {
                warnings.Add(string.Create(CultureInfo.InvariantCulture,
                    $"view '{view.Name}': only the first of {view.SortByFields.Count} sort keys is applied"));
            }

            if (view.Ui?.SliceBy is { } sliceBy)
            {
                WarnIfMissing(warnings, fieldNames, view.Name, "slice-by", sliceBy);
            }

            // "Count" is a built-in Field sum entry, not a field.
            foreach (var entry in view.Ui?.FieldSum ?? [])
            {
                if (!string.Equals(entry, "Count", StringComparison.Ordinal))
                {
                    WarnIfMissing(warnings, fieldNames, view.Name, "field-sum", entry);
                }
            }

            if (view.Ui?.Roadmap is { } roadmap)
            {
                if (roadmap.StartField is { } startField && !RoadmapFieldExists(fieldNames, startField))
                {
                    warnings.Add($"view '{view.Name}': roadmap start date field '{startField}' does not exist in the snapshot");
                }

                if (roadmap.TargetField is { } targetField && !RoadmapFieldExists(fieldNames, targetField))
                {
                    warnings.Add($"view '{view.Name}': roadmap target date field '{targetField}' does not exist in the snapshot");
                }
            }
        }

        return warnings;
    }

    /// <summary>
    /// Applies only settings that the GraphQL View mutations cannot write. Views must
    /// already exist and be mapped from source to target numbers by the API import stage.
    /// </summary>
    public async Task EnrichAsync(
        ProjectSnapshot snapshot,
        string ownerLogin,
        ProjectOwnerType ownerType,
        int projectNumber,
        IReadOnlyDictionary<int, int> viewNumbers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerLogin);
        ArgumentNullException.ThrowIfNull(viewNumbers);

        if (snapshot.Views.Count == 0)
        {
            return;
        }

        _warnings.AddRange(CollectPreflightWarnings(snapshot));
        var page = await _session.GetPageAsync(cancellationToken).ConfigureAwait(false);
        foreach (var view in snapshot.Views.OrderBy(candidate => candidate.Number))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!viewNumbers.TryGetValue(view.Number, out var targetNumber))
            {
                _warnings.Add($"view '{view.Name}': API import did not return a target view number; browser-only settings were skipped");
                continue;
            }

            OnProgress?.Invoke($"Applying browser-only settings for view '{view.Name}' ({view.Layout})...");
            try
            {
                var url = BrowserProjectUrl.Build(
                    _session.BaseUrl,
                    ownerLogin,
                    ownerType,
                    projectNumber,
                    string.Create(CultureInfo.InvariantCulture, $"views/{targetNumber}"));
                await _session.GotoAsync(url, cancellationToken).ConfigureAwait(false);
                await ApplyBrowserOnlySettingsAsync(page, view, cancellationToken).ConfigureAwait(false);
                await SaveViewAsync(page, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is PlaywrightException or TimeoutException or InvalidOperationException)
            {
                _warnings.Add($"view '{view.Name}': browser-only settings could not be applied — {exception.Message}");
            }
        }
    }

    // ----- settings -----

    private async Task ApplyBrowserOnlySettingsAsync(
        IPage page,
        ViewSnapshot view,
        CancellationToken cancellationToken)
    {
        // GraphQL-derived settings. Boards expose their horizontal grouping as the
        // "Swimlanes" menu item (E2E discovery, 2026-07-06) while tables/roadmaps use
        // "Group by"; GraphQL reports both as groupByFields.
        var isBoard = string.Equals(view.Layout, "BOARD_LAYOUT", StringComparison.Ordinal);
        var groupingLabel = isBoard ? "Swimlanes" : "Group by";
        if (view.GroupByFields.Count > 0)
        {
            await TrySetSingleAsync(page, groupingLabel, view.GroupByFields[0], view.Name, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await TrySetSingleAsync(
                page,
                groupingLabel,
                ["None", "No grouping"],
                "none",
                view.Name,
                cancellationToken).ConfigureAwait(false);
        }

        if (isBoard && view.VerticalGroupByFields.Count > 0)
        {
            await TrySetSingleAsync(page, "Column by", view.VerticalGroupByFields[0], view.Name, cancellationToken).ConfigureAwait(false);
        }
        else if (isBoard)
        {
            await TrySetSingleAsync(
                page,
                "Column by",
                ["None", "No field"],
                "none",
                view.Name,
                cancellationToken).ConfigureAwait(false);
        }

        if (view.SortByFields.Count > 0)
        {
            await TryEnsureSortFieldVisibleAsync(
                page,
                view.SortByFields[0].Field,
                view.Name,
                cancellationToken).ConfigureAwait(false);
            await TrySetSortAsync(page, view.SortByFields[0], view.Name, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await TrySetSingleAsync(
                page,
                "Sort by",
                ["None", "No sorting"],
                "none",
                view.Name,
                cancellationToken).ConfigureAwait(false);
        }

        // UI-only settings.
        if (view.Ui?.SliceBy is { } sliceBy)
        {
            await TrySetSingleAsync(page, "Slice by", sliceBy, view.Name, cancellationToken).ConfigureAwait(false);
        }
        else if (view.Ui is not null)
        {
            await TrySetSingleAsync(
                page,
                "Slice by",
                ["None", "No field"],
                "none",
                view.Name,
                cancellationToken).ConfigureAwait(false);
        }

        // "Field sum" is a checkbox overlay (Count + number fields). A fresh board
        // defaults to ["Count"], so apply the complete desired set, including empty.
        if (isBoard && view.Ui is not null)
        {
            await TrySetCheckboxesAsync(page, "Field sum", view.Ui.FieldSum ?? [], view.Name, cancellationToken).ConfigureAwait(false);
        }

        if (view.Ui?.Roadmap is { } roadmap)
        {
            if (roadmap.StartField is not null || roadmap.TargetField is not null)
            {
                await TrySetDateFieldsAsync(page, roadmap, view.Name, cancellationToken).ConfigureAwait(false);
            }

            if (roadmap.Zoom is { } zoom)
            {
                await TrySetSingleAsync(page, "Zoom level", zoom, view.Name, cancellationToken).ConfigureAwait(false);
            }

            await TrySetCheckboxesAsync(page, "Markers", roadmap.Markers ?? [], view.Name, cancellationToken).ConfigureAwait(false);
        }

    }

    private async Task<bool> TrySetSingleAsync(IPage page, string label, string value, string viewName, CancellationToken cancellationToken)
        => await TrySetSingleAsync(page, label, [value], value, viewName, cancellationToken).ConfigureAwait(false);

    private async Task<bool> TrySetSingleAsync(
        IPage page,
        string label,
        IReadOnlyList<string> candidates,
        string expectedValue,
        string viewName,
        CancellationToken cancellationToken)
    {
        try
        {
            var menu = await OpenViewMenuAsync(page, cancellationToken).ConfigureAwait(false);
            var item = Sel.ConfigurationMenuItem(menu, label);
            if (await item.CountAsync().ConfigureAwait(false) == 0)
            {
                _warnings.Add($"view '{viewName}': '{label}' is not available in this layout");
                await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
                return false;
            }

            await item.First.ClickAsync().ConfigureAwait(false);
            await PauseAsync(cancellationToken).ConfigureAwait(false);

            ILocator? option = null;
            foreach (var candidate in candidates)
            {
                option = await FindOptionAsync(page, candidate).ConfigureAwait(false);
                if (option is not null)
                {
                    break;
                }
            }

            if (option is null)
            {
                _warnings.Add($"view '{viewName}': {label} value '{expectedValue}' is not available on the target");
                await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
                return false;
            }

            await option.ClickAsync().ConfigureAwait(false);
            await PauseAsync(cancellationToken).ConfigureAwait(false);
            await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
        {
            _warnings.Add($"view '{viewName}': {label} could not be applied — {exception.Message}");
            await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    private async Task TrySetSortAsync(IPage page, SortByFieldSnapshot sort, string viewName, CancellationToken cancellationToken)
    {
        if (!await TrySetSingleAsync(page, "Sort by", sort.Field, viewName, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var menu = await OpenViewMenuAsync(page, cancellationToken).ConfigureAwait(false);
            var item = Sel.ConfigurationMenuItem(menu, "Sort by");
            await item.First.ClickAsync().ConfigureAwait(false);
            await PauseAsync(cancellationToken).ConfigureAwait(false);

            var directionName = string.Equals(sort.Direction, "DESC", StringComparison.Ordinal)
                ? "Descending"
                : "Ascending";
            var direction = await FindOptionAsync(page, directionName).ConfigureAwait(false);
            if (direction is null)
            {
                _warnings.Add($"view '{viewName}': {directionName.ToLowerInvariant()} sort direction for '{sort.Field}' could not be applied");
            }
            else
            {
                await direction.ClickAsync().ConfigureAwait(false);
                await PauseAsync(cancellationToken).ConfigureAwait(false);
            }

            await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
        {
            _warnings.Add($"view '{viewName}': sort direction could not be applied — {exception.Message}");
            await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TryEnsureSortFieldVisibleAsync(
        IPage page,
        string field,
        string viewName,
        CancellationToken cancellationToken)
    {
        try
        {
            var menu = await OpenViewMenuAsync(page, cancellationToken).ConfigureAwait(false);
            var item = Sel.ConfigurationMenuItem(menu, "Fields");
            if (await item.CountAsync().ConfigureAwait(false) == 0)
            {
                await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
                return;
            }

            await item.First.ClickAsync().ConfigureAwait(false);
            await PauseAsync(cancellationToken).ConfigureAwait(false);
            var option = await FindOptionAsync(page, field).ConfigureAwait(false);
            if (option is not null
                && !string.Equals(await option.GetAttributeAsync("aria-disabled").ConfigureAwait(false), "true", StringComparison.Ordinal)
                && !string.Equals(await option.GetAttributeAsync("aria-checked").ConfigureAwait(false), "true", StringComparison.Ordinal))
            {
                await option.ClickAsync().ConfigureAwait(false);
                await PauseAsync(cancellationToken).ConfigureAwait(false);
            }

            await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
        {
            _warnings.Add($"view '{viewName}': sort field '{field}' could not be made visible — {exception.Message}");
            await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TrySetCheckboxesAsync(IPage page, string label, IReadOnlyList<string> values, string viewName, CancellationToken cancellationToken)
    {
        try
        {
            var menu = await OpenViewMenuAsync(page, cancellationToken).ConfigureAwait(false);
            var item = Sel.ConfigurationMenuItem(menu, label);
            if (await item.CountAsync().ConfigureAwait(false) == 0)
            {
                _warnings.Add($"view '{viewName}': '{label}' is not available in this layout");
                await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
                return;
            }

            await item.First.ClickAsync().ConfigureAwait(false);
            await PauseAsync(cancellationToken).ConfigureAwait(false);
            await ToggleCheckboxesAsync(page, new HashSet<string>(values, StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
            await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
        {
            _warnings.Add($"view '{viewName}': {label} could not be applied — {exception.Message}");
            await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Toggles every enabled overlay checkbox so the checked set matches <paramref name="desired"/>.
    /// Overlays differ per menu (E2E discovery, 2026-07-06): "Field sum" / "Markers" render
    /// <c>menuitemcheckbox</c> entries, while "Fields" renders <c>option</c> entries — both
    /// carry <c>aria-checked</c>.
    /// </summary>
    private static async Task ToggleCheckboxesAsync(IPage page, HashSet<string> desired, CancellationToken cancellationToken)
    {
        var checkboxes = page.GetByRole(AriaRole.Menuitemcheckbox);
        if (await checkboxes.CountAsync().ConfigureAwait(false) == 0)
        {
            checkboxes = page.GetByRole(AriaRole.Option);
        }

        var count = await checkboxes.CountAsync().ConfigureAwait(false);
        for (var i = 0; i < count; i++)
        {
            var checkbox = checkboxes.Nth(i);
            if (string.Equals(await checkbox.GetAttributeAsync("aria-disabled").ConfigureAwait(false), "true", StringComparison.Ordinal))
            {
                continue;
            }

            var name = ViewUiExporter.NormalizeUiText(await checkbox.InnerTextAsync().ConfigureAwait(false));
            if (name is null)
            {
                continue;
            }

            var isChecked = string.Equals(await checkbox.GetAttributeAsync("aria-checked").ConfigureAwait(false), "true", StringComparison.Ordinal);
            if (desired.Contains(name) != isChecked)
            {
                await checkbox.ClickAsync().ConfigureAwait(false);
                await PauseAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task TrySetDateFieldsAsync(IPage page, RoadmapSettingsSnapshot roadmap, string viewName, CancellationToken cancellationToken)
    {
        try
        {
            var menu = await OpenViewMenuAsync(page, cancellationToken).ConfigureAwait(false);
            var item = Sel.ConfigurationMenuItem(menu, "Dates");
            if (await item.CountAsync().ConfigureAwait(false) == 0)
            {
                _warnings.Add($"view '{viewName}': 'Dates' is not available in this layout");
                await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
                return;
            }

            await item.First.ClickAsync().ConfigureAwait(false);
            var dialog = Sel.DateFieldsDialog(page);
            await dialog.WaitForAsync().ConfigureAwait(false);
            await PauseAsync(cancellationToken).ConfigureAwait(false);

            await SelectDateRadioAsync(dialog, "Start date", roadmap.StartField, viewName, cancellationToken).ConfigureAwait(false);
            await SelectDateRadioAsync(dialog, "Target date", roadmap.TargetField, viewName, cancellationToken).ConfigureAwait(false);

            await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
        {
            _warnings.Add($"view '{viewName}': roadmap date fields could not be applied — {exception.Message}");
            await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SelectDateRadioAsync(ILocator dialog, string groupName, string? value, string viewName, CancellationToken cancellationToken)
    {
        if (value is null)
        {
            return;
        }

        var radio = Sel.DateFieldGroup(dialog, groupName).GetByRole(AriaRole.Menuitemradio, new() { Name = value, Exact = true });
        if (await radio.CountAsync().ConfigureAwait(false) == 0)
        {
            _warnings.Add($"view '{viewName}': {groupName} field '{value}' is not available on the target");
            return;
        }

        await radio.First.ClickAsync().ConfigureAwait(false);
        await PauseAsync(cancellationToken).ConfigureAwait(false);
    }

    // ----- save -----

    private static async Task SaveViewAsync(IPage page, CancellationToken cancellationToken)
    {
        // D0: the "Save view" button lives inside the View menu overlay, so the menu
        // must be (re-)opened first. With no unsaved changes the button is absent.
        var save = Sel.SaveViewButton(page);
        if (await save.CountAsync().ConfigureAwait(false) == 0 || !await save.First.IsVisibleAsync().ConfigureAwait(false))
        {
            await Sel.ViewMenuButton(page).ClickAsync().ConfigureAwait(false);
            await PauseAsync(cancellationToken).ConfigureAwait(false);
        }

        if (await save.CountAsync().ConfigureAwait(false) == 0 || !await save.First.IsVisibleAsync().ConfigureAwait(false))
        {
            await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
            return; // No unsaved changes.
        }

        await save.First.ClickAsync().ConfigureAwait(false);

        // D0: saving raises a confirmation alertdialog "Save display options for <view>?".
        var confirm = Sel.SaveConfirmDialog(page);
        try
        {
            await confirm.WaitForAsync(new() { Timeout = 5_000 }).ConfigureAwait(false);
            await confirm.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).First.ClickAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
        {
            // No confirmation dialog appeared; the save applied directly.
        }

        await PauseAsync(cancellationToken).ConfigureAwait(false);
    }

    // ----- helpers -----

    private static async Task<ILocator> OpenViewMenuAsync(IPage page, CancellationToken cancellationToken)
    {
        await Sel.ViewMenuButton(page).ClickAsync().ConfigureAwait(false);
        var menu = Sel.OpenMenu(page);
        await menu.WaitForAsync().ConfigureAwait(false);
        await PauseAsync(cancellationToken).ConfigureAwait(false);
        return menu;
    }

    private static async Task<ILocator?> FindOptionAsync(IPage page, string value)
    {
        var option = page.GetByRole(OptionRoles[0], new() { Name = value, Exact = true });
        for (var i = 1; i < OptionRoles.Length; i++)
        {
            option = option.Or(page.GetByRole(OptionRoles[i], new() { Name = value, Exact = true }));
        }

        try
        {
            await option.First.WaitForAsync(new() { Timeout = 10_000 }).ConfigureAwait(false);
            return option.First;
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
        {
            return null;
        }
    }

    private static async Task CloseMenusAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
        {
            // Best effort; the next navigation resets the UI state anyway.
        }
    }

    // 300ms between consecutive UI operations (BROWSER_AUTOMATION_PLAN §1.4).
    private static Task PauseAsync(CancellationToken cancellationToken) => Task.Delay(300, cancellationToken);

    private static void WarnIfMissing(List<string> warnings, HashSet<string> fieldNames, string viewName, string setting, string field)
    {
        if (!fieldNames.Contains(field))
        {
            warnings.Add($"view '{viewName}': {setting} field '{field}' does not exist in the snapshot");
        }
    }

    /// <summary>
    /// Roadmap date values may be a field name or "&lt;iteration field&gt; start" / "… end" (D0).
    /// </summary>
    private static bool RoadmapFieldExists(HashSet<string> fieldNames, string value)
    {
        if (fieldNames.Contains(value))
        {
            return true;
        }

        foreach (var suffix in RoadmapDateSuffixes)
        {
            if (value.EndsWith(suffix, StringComparison.Ordinal) && fieldNames.Contains(value[..^suffix.Length]))
            {
                return true;
            }
        }

        return false;
    }
}
