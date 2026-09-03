using System.Globalization;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Snapshot;
using Microsoft.Playwright;

namespace Ghpmv.Core.Browser;

/// <summary>
/// UI export of view settings that GraphQL does not expose (B2). For each view the
/// "View" configuration menu is opened and the current values of Markers / Dates /
/// Zoom level / Slice by / Field sum / Board columns / Roadmap display options are read. Results are stored in <see cref="ViewSnapshot.Ui"/>;
/// views whose UI settings cannot be read keep <c>Ui = null</c> and add a warning.
/// </summary>
public sealed class ViewUiExporter
{
    private readonly BrowserSession _session;
    private readonly List<string> _warnings = [];

    public ViewUiExporter(BrowserSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>Invoked with a human-readable progress message per view.</summary>
    public Action<string>? OnProgress { get; set; }

    /// <summary>Warnings collected while scraping (views whose UI settings could not be read).</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>
    /// Whether the API's POSITION-ordered View connection matched the saved-tab DOM order.
    /// Null when browser tab order could not be read. A true value is a capability signal:
    /// re-evaluate whether browser reads are still required before changing behavior.
    /// </summary>
    public bool? GraphQlPositionMatchesDomOrder { get; private set; }

    /// <summary>Returns a copy of <paramref name="snapshot"/> with <see cref="ViewSnapshot.Ui"/> populated.</summary>
    public async Task<ProjectSnapshot> EnrichAsync(ProjectSnapshot snapshot, string orgLogin, int projectNumber, CancellationToken cancellationToken = default)
        => await EnrichAsync(snapshot, orgLogin, ProjectOwnerType.Organization, projectNumber, cancellationToken).ConfigureAwait(false);

    /// <summary>Returns a copy of <paramref name="snapshot"/> with <see cref="ViewSnapshot.Ui"/> populated.</summary>
    public async Task<ProjectSnapshot> EnrichAsync(
        ProjectSnapshot snapshot,
        string ownerLogin,
        ProjectOwnerType ownerType,
        int projectNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerLogin);

        IPage page;
        try
        {
            page = await _session.GetPageAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
        {
            _warnings.Add($"view settings page could not be opened — {exception.Message}");
            return snapshot with
            {
                Views = snapshot.Views.Select(view => view with { Ui = null }).ToList(),
            };
        }

        var views = new List<ViewSnapshot>(snapshot.Views.Count);
        foreach (var view in snapshot.Views)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OnProgress?.Invoke(string.Create(CultureInfo.InvariantCulture,
                $"Reading UI settings for view '{view.Name}' (#{view.Number})..."));
            ViewUiSnapshot? ui = null;
            try
            {
                ui = await ReadViewUiAsync(
                    page,
                    ownerLogin,
                    ownerType,
                    projectNumber,
                    view,
                    snapshot.Fields,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is PlaywrightException or TimeoutException or InvalidOperationException)
            {
                _warnings.Add($"view '{view.Name}': UI settings could not be read — {exception.Message}");
            }

            views.Add(view with { Ui = ui });
        }

        try
        {
            var graphQlPositionOrder = views.Select(view => view.Number).ToList();
            var tabOrder = await ViewTabOrder.ReadAsync(page, cancellationToken).ConfigureAwait(false);
            GraphQlPositionMatchesDomOrder = graphQlPositionOrder.SequenceEqual(tabOrder);
            views = [.. ViewTabOrder.Apply(views, tabOrder)];
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException or InvalidOperationException)
        {
            GraphQlPositionMatchesDomOrder = null;
            _warnings.Add($"view tab order could not be read — {exception.Message}");
            views = [.. views.Select(view => view with { TabPosition = null })];
        }

        return snapshot with { Views = views };
    }

    private async Task<ViewUiSnapshot> ReadViewUiAsync(
        IPage page,
        string ownerLogin,
        ProjectOwnerType ownerType,
        int projectNumber,
        ViewSnapshot view,
        IReadOnlyList<FieldSnapshot> fields,
        CancellationToken cancellationToken)
    {
        var url = BrowserProjectUrl.Build(
            _session.BaseUrl,
            ownerLogin,
            ownerType,
            projectNumber,
            string.Create(CultureInfo.InvariantCulture, $"views/{view.Number}"));
        await _session.GotoAsync(url, cancellationToken).ConfigureAwait(false);

        await Sel.ViewMenuButton(page).ClickAsync().ConfigureAwait(false);
        var menu = Sel.OpenMenu(page);
        await menu.WaitForAsync().ConfigureAwait(false);
        await Task.Delay(300, cancellationToken).ConfigureAwait(false);

        bool? truncateTitles = null;
        bool? showDateFields = null;
        if (string.Equals(view.Layout, "ROADMAP_LAYOUT", StringComparison.Ordinal))
        {
            truncateTitles = await ReadRoadmapDisplayOptionAsync(menu, view.Name, "Truncate titles").ConfigureAwait(false);
            showDateFields = await ReadRoadmapDisplayOptionAsync(menu, view.Name, "Show date fields").ConfigureAwait(false);
        }

        var sliceBy = ParseMenuValue(await ReadMenuItemTextAsync(menu, "Slice by").ConfigureAwait(false));
        var fieldSum = await ReadCheckedMenuValuesAsync(
            page,
            menu,
            "Field sum",
            ViewUiImporter.FieldSumControlExpected(view),
            cancellationToken).ConfigureAwait(false);

        RoadmapSettingsSnapshot? roadmap = null;
        if (string.Equals(view.Layout, "ROADMAP_LAYOUT", StringComparison.Ordinal))
        {
            var zoom = ParseMenuValue(await ReadMenuItemTextAsync(menu, "Zoom level").ConfigureAwait(false));
            var markers = ParseListValue(await ReadMenuItemTextAsync(menu, "Markers").ConfigureAwait(false));
            var (startField, targetField) = await ReadDateFieldsAsync(page, menu, cancellationToken).ConfigureAwait(false);
            roadmap = new RoadmapSettingsSnapshot
            {
                StartField = startField,
                TargetField = targetField,
                Zoom = zoom,
                Markers = markers,
                TruncateTitles = truncateTitles,
                ShowDateFields = showDateFields,
            };
        }

        await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        IReadOnlyList<BoardColumnLimitSnapshot>? boardColumnLimits = null;
        IReadOnlyList<BoardColumnSnapshot>? visibleColumns = null;
        if (string.Equals(view.Layout, "BOARD_LAYOUT", StringComparison.Ordinal))
        {
            if (BoardColumnLimitUi.CanCapture(view, fields, out var reason))
            {
                try
                {
                    visibleColumns = await BoardColumnVisibilityUi.ReadAsync(
                        page,
                        view,
                        fields,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is PlaywrightException or TimeoutException or InvalidOperationException)
                {
                    _warnings.Add(
                        $"view '{view.Name}': Board column visibility was not captured — {exception.Message}");
                }

                if (visibleColumns is not null)
                {
                    boardColumnLimits = await ReadCompleteBoardColumnLimitsAsync(
                        page,
                        view,
                        fields,
                        visibleColumns,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _warnings.Add(
                        $"view '{view.Name}': Board column limits were not captured because complete column visibility could not be read");
                }
            }
            else
            {
                _warnings.Add(
                    $"view '{view.Name}': Board column visibility and limits were not captured — {reason}");
            }
        }

        return new ViewUiSnapshot
        {
            SliceBy = sliceBy,
            FieldSum = fieldSum,
            BoardColumnLimits = boardColumnLimits,
            VisibleColumns = visibleColumns,
            Roadmap = roadmap,
            ScrapedAt = DateTimeOffset.UtcNow,
        };
    }

    private async Task<IReadOnlyList<BoardColumnLimitSnapshot>?> ReadCompleteBoardColumnLimitsAsync(
        IPage page,
        ViewSnapshot view,
        IReadOnlyList<FieldSnapshot> fields,
        IReadOnlyList<BoardColumnSnapshot> originalVisibility,
        CancellationToken cancellationToken)
    {
        try
        {
            // Limits are only reachable from rendered columns, so reveal every logical
            // value without saving and restore the captured visibility afterward.
            return await BoardColumnLimitUi.ReadCompleteAsync(
                page,
                view,
                fields,
                originalVisibility,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException or InvalidOperationException)
        {
            _warnings.Add($"view '{view.Name}': Board column limits were not captured — {exception.Message}");
            return null;
        }
    }

    private async Task<bool?> ReadRoadmapDisplayOptionAsync(ILocator menu, string viewName, string label)
    {
        var option = Sel.ViewOptionCheckbox(menu, label);
        if (await option.CountAsync().ConfigureAwait(false) == 0)
        {
            _warnings.Add($"view '{viewName}': roadmap display option '{label}' could not be read — control is unavailable");
            return null;
        }

        return await option.First.GetAttributeAsync("aria-checked").ConfigureAwait(false) switch
        {
            "true" => true,
            "false" => false,
            _ => WarnUnreadableRoadmapDisplayOption(viewName, label),
        };
    }

    private bool? WarnUnreadableRoadmapDisplayOption(string viewName, string label)
    {
        _warnings.Add($"view '{viewName}': roadmap display option '{label}' could not be read — aria-checked is unavailable");
        return null;
    }

    private static async Task<string?> ReadMenuItemTextAsync(ILocator menu, string label)
    {
        var item = Sel.ConfigurationMenuItem(menu, label);
        if (await item.CountAsync().ConfigureAwait(false) == 0)
        {
            return null;
        }

        return await item.First.InnerTextAsync().ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<string>?> ReadCheckedMenuValuesAsync(
        IPage page,
        ILocator menu,
        string label,
        bool required,
        CancellationToken cancellationToken)
    {
        var item = Sel.ConfigurationMenuItem(menu, label);
        if (await item.CountAsync().ConfigureAwait(false) == 0)
        {
            if (required)
            {
                throw new InvalidOperationException($"'{label}' control is not available for this grouped view");
            }

            return null;
        }

        await item.First.ClickAsync().ConfigureAwait(false);
        var overlay = Sel.OpenMenu(page);
        await overlay.WaitForAsync().ConfigureAwait(false);
        await Task.Delay(300, cancellationToken).ConfigureAwait(false);

        var values = new List<string>();
        var checkboxes = Sel.CheckboxOptions(overlay);
        var count = await checkboxes.CountAsync().ConfigureAwait(false);
        if (count == 0)
        {
            throw new InvalidOperationException($"'{label}' menu contains no checkable entries");
        }

        for (var index = 0; index < count; index++)
        {
            var checkbox = checkboxes.Nth(index);
            if (!string.Equals(
                    await checkbox.GetAttributeAsync("aria-checked").ConfigureAwait(false),
                    "true",
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (NormalizeUiText(await checkbox.InnerTextAsync().ConfigureAwait(false)) is { } value)
            {
                values.Add(value);
            }
        }

        await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        return values;
    }

    /// <summary>
    /// Opens the "Dates" configuration item and reads the checked radios of the
    /// "Select date fields" dialog (D0: iteration fields expand to
    /// "&lt;name&gt; start" / "&lt;name&gt; end" radios).
    /// </summary>
    private static async Task<(string? StartField, string? TargetField)> ReadDateFieldsAsync(IPage page, ILocator menu, CancellationToken cancellationToken)
    {
        var item = Sel.ConfigurationMenuItem(menu, "Dates");
        if (await item.CountAsync().ConfigureAwait(false) == 0)
        {
            return (null, null);
        }

        await item.First.ClickAsync().ConfigureAwait(false);
        var dialog = Sel.DateFieldsDialog(page);
        await dialog.WaitForAsync().ConfigureAwait(false);
        await Task.Delay(300, cancellationToken).ConfigureAwait(false);

        var startField = await ReadCheckedRadioAsync(dialog, "Start date").ConfigureAwait(false);
        var targetField = await ReadCheckedRadioAsync(dialog, "Target date").ConfigureAwait(false);

        await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        return (startField, targetField);
    }

    private static async Task<string?> ReadCheckedRadioAsync(ILocator dialog, string groupName)
    {
        var radios = Sel.DateFieldGroup(dialog, groupName).GetByRole(AriaRole.Menuitemradio);
        var count = await radios.CountAsync().ConfigureAwait(false);
        for (var i = 0; i < count; i++)
        {
            var radio = radios.Nth(i);
            var isChecked = await radio.GetAttributeAsync("aria-checked").ConfigureAwait(false);
            if (string.Equals(isChecked, "true", StringComparison.Ordinal))
            {
                return NormalizeUiText(await radio.InnerTextAsync().ConfigureAwait(false));
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts the value from a configuration menu item text of the form
    /// "Group by: &lt;value&gt;". Whitespace (including newlines) is collapsed and
    /// "none" (case-insensitive) or an empty value is normalized to null.
    /// </summary>
    public static string? ParseMenuValue(string? menuItemText)
    {
        if (string.IsNullOrWhiteSpace(menuItemText))
        {
            return null;
        }

        var separatorIndex = menuItemText.IndexOf(':');
        var value = separatorIndex < 0 ? menuItemText : menuItemText[(separatorIndex + 1)..];
        var normalized = NormalizeUiText(value);
        if (normalized is null || normalized.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return normalized;
    }

    /// <summary>
    /// Parses a list menu value into its entries, or null when none. The UI renders
    /// lists in prose form — "A and B" / "A, B, and C" (E2E discovery, 2026-07-06) —
    /// so both the comma and the " and " conjunction are treated as separators.
    /// </summary>
    public static IReadOnlyList<string>? ParseListValue(string? menuItemText)
    {
        var value = ParseMenuValue(menuItemText);
        if (value is null)
        {
            return null;
        }

        var parts = value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(part => part.Split(" and ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Select(part => part.StartsWith("and ", StringComparison.Ordinal) ? part["and ".Length..] : part)
            .Where(part => part.Length > 0)
            .ToList();
        return parts.Count == 0 ? null : parts;
    }

    /// <summary>Collapses all whitespace runs (including newlines) to single spaces; null when empty.</summary>
    public static string? NormalizeUiText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
