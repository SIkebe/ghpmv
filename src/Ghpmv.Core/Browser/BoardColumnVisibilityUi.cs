using Ghpmv.Core.Snapshot;
using Microsoft.Playwright;

namespace Ghpmv.Core.Browser;

internal static class BoardColumnVisibilityUi
{
    private const int PickerViewportHeight = 3000;

    public static async Task<IReadOnlyList<BoardColumnSnapshot>> ReadAsync(
        IPage page,
        ViewSnapshot view,
        IReadOnlyList<FieldSnapshot> fields,
        CancellationToken cancellationToken)
    {
        var field = ResolveColumnField(view, fields);
        var originalViewport = page.ViewportSize;
        var resizeViewport = originalViewport is { Height: < PickerViewportHeight };
        if (resizeViewport)
        {
            await page.SetViewportSizeAsync(
                originalViewport!.Width,
                PickerViewportHeight).ConfigureAwait(false);
        }

        try
        {
            var pickerState = await ReadPickerStateAsync(page, field, cancellationToken)
                .ConfigureAwait(false);
            var visibleNames = pickerState
                .Where(column => column.IsVisible)
                .Select(column => column.Name)
                .ToHashSet(StringComparer.Ordinal);

            return GetValueNames(field)
                .Where(visibleNames.Contains)
                .Select(value => CreateSnapshot(field, value))
                .ToArray();
        }
        finally
        {
            if (resizeViewport)
            {
                await page.SetViewportSizeAsync(
                    originalViewport!.Width,
                    originalViewport.Height).ConfigureAwait(false);
            }
        }
    }

    public static async Task<IReadOnlyList<string>> ApplyAsync(
        IPage page,
        ViewSnapshot view,
        IReadOnlyList<FieldSnapshot> fields,
        IReadOnlyList<BoardColumnSnapshot> desiredColumns,
        CancellationToken cancellationToken)
    {
        var field = ResolveColumnField(view, fields);
        var plan = BuildReconciliationPlan(view, field, desiredColumns);
        if (plan.Warnings.Count > 0)
        {
            return plan.Warnings;
        }

        var originalViewport = page.ViewportSize;
        var resizeViewport = originalViewport is { Height: < PickerViewportHeight };
        if (resizeViewport)
        {
            await page.SetViewportSizeAsync(
                originalViewport!.Width,
                PickerViewportHeight).ConfigureAwait(false);
        }
        try
        {
            await Sel.AddBoardColumnButton(page).ClickAsync().ConfigureAwait(false);
            await PauseAsync(cancellationToken).ConfigureAwait(false);
            var overlay = Sel.OpenMenu(page);
            await overlay.WaitForAsync().ConfigureAwait(false);

            var available = new HashSet<string>(StringComparer.Ordinal);
            var options = Sel.CheckboxOptions(overlay);
            var count = await options.CountAsync().ConfigureAwait(false);
            for (var index = 0; index < count; index++)
            {
                var option = options.Nth(index);
                var name = ViewUiExporter.NormalizeUiText(await option.InnerTextAsync().ConfigureAwait(false));
                if (name is null || !ValueExists(field, name))
                {
                    continue;
                }

                available.Add(name);
            }

            foreach (var missing in FindMissingValueNames(field, available))
            {
                plan.Warnings.Add(
                    $"view '{view.Name}': Board column '{field.Name}' / '{missing}' is not available on the target; no visibility was changed");
            }
            if (plan.Warnings.Count > 0)
            {
                return plan.Warnings;
            }

            await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
            await overlay.WaitForAsync(new()
            {
                State = WaitForSelectorState.Hidden,
            }).ConfigureAwait(false);
            await PauseAsync(cancellationToken).ConfigureAwait(false);

            foreach (var change in BuildApplyOrder(available.ToList(), plan.VisibleNames))
            {
                if (change.ShouldBeVisible)
                {
                    await ShowColumnAsync(change.Name).ConfigureAwait(false);
                }
                else
                {
                    await HideColumnAsync(change.Name).ConfigureAwait(false);
                }
            }

            return plan.Warnings;

            async Task ShowColumnAsync(string name)
            {
                await Sel.AddBoardColumnButton(page).ClickAsync().ConfigureAwait(false);
                await PauseAsync(cancellationToken).ConfigureAwait(false);
                var picker = Sel.OpenMenu(page);
                await picker.WaitForAsync().ConfigureAwait(false);
                var pickerOptions = Sel.CheckboxOptions(picker);
                try
                {
                    var option = await FindOptionAsync(page, pickerOptions, name).ConfigureAwait(false)
                        ?? throw new InvalidOperationException(
                            $"view '{view.Name}': Board column '{field.Name}' / '{name}' disappeared from the visibility picker");
                    var isChecked = string.Equals(
                        await option.GetAttributeAsync("aria-checked").ConfigureAwait(false),
                        "true",
                        StringComparison.Ordinal);
                    if (isChecked)
                    {
                        return;
                    }

                    var isDisabled = string.Equals(
                        await option.GetAttributeAsync("aria-disabled").ConfigureAwait(false),
                        "true",
                        StringComparison.Ordinal);
                    if (DisabledColumnNeedsWarning(isChecked, isDisabled))
                    {
                        plan.Warnings.Add(
                            $"view '{view.Name}': Board column '{field.Name}' / '{name}' is disabled on the target and its visibility could not be changed");
                        return;
                    }

                    for (var attempt = 0; attempt < 6; attempt++)
                    {
                        option = await FindOptionAsync(page, pickerOptions, name).ConfigureAwait(false);
                        if (option is null)
                        {
                            await PauseAsync(cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                        if (string.Equals(
                            await option.GetAttributeAsync("aria-checked").ConfigureAwait(false),
                            "true",
                            StringComparison.Ordinal))
                        {
                            return;
                        }

                        await ActivatePickerOptionAsync(page, pickerOptions, option, name, attempt)
                            .ConfigureAwait(false);
                        await PauseAsync(cancellationToken).ConfigureAwait(false);
                    }

                    throw new InvalidOperationException(
                        $"view '{view.Name}': Board column '{field.Name}' / '{name}' visibility did not update in the picker");
                }
                finally
                {
                    await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
                    await picker.WaitForAsync(new()
                    {
                        State = WaitForSelectorState.Hidden,
                    }).ConfigureAwait(false);
                    await PauseAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            async Task HideColumnAsync(string name)
            {
                var pickerState = await ReadPickerStateAsync(page, field, cancellationToken)
                    .ConfigureAwait(false);
                if (!pickerState.Any(column =>
                    string.Equals(column.Name, name, StringComparison.Ordinal) && column.IsVisible))
                {
                    return;
                }

                var actionsButton = await BoardColumnLimitUi.EnsureColumnActionsButtonAsync(
                    page,
                    name,
                    cancellationToken).ConfigureAwait(false);
                await actionsButton.ClickAsync().ConfigureAwait(false);
                await PauseAsync(cancellationToken).ConfigureAwait(false);
                var hideItem = Sel.BoardColumnHideMenuItem(page);
                await hideItem.WaitForAsync().ConfigureAwait(false);
                await hideItem.ClickAsync().ConfigureAwait(false);
                await PauseAsync(cancellationToken).ConfigureAwait(false);

                pickerState = await ReadPickerStateAsync(page, field, cancellationToken)
                    .ConfigureAwait(false);
                if (pickerState.Any(column =>
                    string.Equals(column.Name, name, StringComparison.Ordinal) && column.IsVisible))
                {
                    throw new InvalidOperationException(
                        $"view '{view.Name}': Board column '{field.Name}' / '{name}' remained visible after using Hide from view");
                }
            }
        }
        finally
        {
            if (resizeViewport)
            {
                await page.SetViewportSizeAsync(
                    originalViewport!.Width,
                    originalViewport.Height).ConfigureAwait(false);
            }
        }

        static async Task ActivatePickerOptionAsync(
            IPage page,
            ILocator options,
            ILocator option,
            string name,
            int attempt)
        {
            await option.EvaluateAsync(
                "element => element.scrollIntoView({ block: 'center', inline: 'nearest' })").ConfigureAwait(false);
            var box = await option.BoundingBoxAsync().ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Board column '{name}' has no clickable bounds");
            var localClickX = box.Width / 2;
            var localClickY = box.Height / 2;
            if (!double.IsFinite(localClickX) || !double.IsFinite(localClickY))
            {
                throw new InvalidOperationException($"Board column '{name}' has non-finite clickable bounds");
            }

            if (attempt == 0)
            {
                await page.WaitForTimeoutAsync(100).ConfigureAwait(false);
                option = await FindOptionAsync(page, options, name).ConfigureAwait(false) ?? option;
                var labelBox = await option.GetByText(name, new() { Exact = true }).BoundingBoxAsync()
                    .ConfigureAwait(false);
                var viewport = page.ViewportSize;
                if (labelBox is not null &&
                    (viewport is null ||
                     (labelBox.X >= 0 &&
                      labelBox.Y >= 0 &&
                      labelBox.X + labelBox.Width <= viewport.Width &&
                      labelBox.Y + labelBox.Height <= viewport.Height)))
                {
                    await page.Mouse.ClickAsync(
                        labelBox.X + (labelBox.Width / 2),
                        labelBox.Y + (labelBox.Height / 2),
                        new() { Delay = 100 }).ConfigureAwait(false);
                }
            }
            else if (attempt == 1)
            {
                var leadingVisual = Sel.CheckboxOptionLeadingVisual(option);
                var leadingVisualBox = await leadingVisual.CountAsync().ConfigureAwait(false) > 0
                    ? await leadingVisual.BoundingBoxAsync().ConfigureAwait(false)
                    : null;
                if (leadingVisualBox is not null)
                {
                    await page.Mouse.ClickAsync(
                        leadingVisualBox.X + (leadingVisualBox.Width / 2),
                        leadingVisualBox.Y + (leadingVisualBox.Height / 2),
                        new() { Delay = 100 }).ConfigureAwait(false);
                }
                else
                {
                    await option.ClickAsync(new()
                    {
                        Delay = 100,
                        Force = true,
                        Position = new() { X = Math.Min(16, box.Width / 2), Y = localClickY },
                        Timeout = 5_000,
                    }).ConfigureAwait(false);
                }
            }
            else if (attempt == 2)
            {
                await option.ClickAsync(new()
                {
                    Delay = 100,
                    Force = true,
                    Position = new() { X = localClickX, Y = localClickY },
                    Timeout = 5_000,
                }).ConfigureAwait(false);
            }
            else if (attempt == 3)
            {
                await page.Mouse.ClickAsync(
                    box.X + Math.Max(1, box.Width - 16),
                    box.Y + localClickY,
                    new() { Delay = 100 }).ConfigureAwait(false);
            }
            else if (attempt == 4)
            {
                await option.PressAsync("Space", new() { Timeout = 5_000 }).ConfigureAwait(false);
            }
            else
            {
                await option.PressAsync("Enter", new() { Timeout = 5_000 }).ConfigureAwait(false);
            }
        }
    }

    private static async Task<IReadOnlyList<PickerColumnState>> ReadPickerStateAsync(
        IPage page,
        FieldSnapshot field,
        CancellationToken cancellationToken)
    {
        await Sel.AddBoardColumnButton(page).WaitForAsync(new()
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000,
        }).ConfigureAwait(false);
        await Sel.AddBoardColumnButton(page).ClickAsync().ConfigureAwait(false);
        await PauseAsync(cancellationToken).ConfigureAwait(false);
        var picker = Sel.OpenMenu(page);
        await picker.WaitForAsync().ConfigureAwait(false);
        try
        {
            var result = new List<PickerColumnState>();
            var options = Sel.CheckboxOptions(picker);
            var count = await options.CountAsync().ConfigureAwait(false);
            for (var index = 0; index < count; index++)
            {
                var option = options.Nth(index);
                var name = ViewUiExporter.NormalizeUiText(
                    await option.InnerTextAsync().ConfigureAwait(false));
                if (name is null || !ValueExists(field, name))
                {
                    continue;
                }

                result.Add(new(
                    name,
                    string.Equals(
                        await option.GetAttributeAsync("aria-checked").ConfigureAwait(false),
                        "true",
                        StringComparison.Ordinal)));
            }

            return result;
        }
        finally
        {
            await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
            await picker.WaitForAsync(new()
            {
                State = WaitForSelectorState.Hidden,
            }).ConfigureAwait(false);
            await PauseAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<ILocator?> FindOptionAsync(IPage page, ILocator options, string name)
    {
        var count = await options.CountAsync().ConfigureAwait(false);
        for (var index = 0; index < count; index++)
        {
            var option = options.Nth(index);
            var currentName = ViewUiExporter.NormalizeUiText(
                await option.InnerTextAsync().ConfigureAwait(false));
            if (string.Equals(currentName, name, StringComparison.Ordinal))
            {
                return options.Filter(new()
                {
                    Has = page.GetByText(name, new() { Exact = true }),
                }).First;
            }
        }

        return null;
    }

    internal static ReconciliationPlan BuildReconciliationPlan(
        ViewSnapshot view,
        FieldSnapshot field,
        IReadOnlyList<BoardColumnSnapshot> desiredColumns)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(desiredColumns);
        var visibleNames = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        foreach (var column in desiredColumns)
        {
            if (!TryGetValueName(field, column, out var valueName))
            {
                warnings.Add(
                    $"view '{view.Name}': visible Board column {Describe(column)} is not a valid value of {field.DataType} field '{field.Name}'");
                continue;
            }

            if (!visibleNames.Add(valueName))
            {
                warnings.Add($"view '{view.Name}': duplicate visible Board column {Describe(column)}");
            }
        }

        return new ReconciliationPlan(visibleNames, warnings);
    }

    internal static bool SetEquals(
        IReadOnlyList<BoardColumnSnapshot>? expected,
        IReadOnlyList<BoardColumnSnapshot>? actual)
    {
        if (expected is null || actual is null)
        {
            return true;
        }

        var expectedKeys = expected.Select(ColumnKey).ToHashSet(StringComparer.Ordinal);
        var actualKeys = actual.Select(ColumnKey).ToHashSet(StringComparer.Ordinal);
        return expectedKeys.Count == expected.Count
            && actualKeys.Count == actual.Count
            && expectedKeys.SetEquals(actualKeys);
    }

    internal static IReadOnlyList<VisibilityChange> BuildApplyOrder(
        IReadOnlyList<string> availableNames,
        IReadOnlySet<string> visibleNames)
        => availableNames
            .Where(visibleNames.Contains)
            .Select(name => new VisibilityChange(name, ShouldBeVisible: true))
            .Concat(availableNames
                .Where(name => !visibleNames.Contains(name))
                .Reverse()
                .Select(name => new VisibilityChange(name, ShouldBeVisible: false)))
            .ToList();

    internal static bool DisabledColumnNeedsWarning(bool isChecked, bool isDisabled)
        => !isChecked && isDisabled;

    internal static IReadOnlyList<string> FindMissingValueNames(
        FieldSnapshot field,
        IReadOnlySet<string> availableNames)
        => GetValueNames(field)
            .Where(name => !availableNames.Contains(name))
            .ToArray();

    internal static IReadOnlyList<BoardColumnSnapshot> GetAllColumns(
        ViewSnapshot view,
        IReadOnlyList<FieldSnapshot> fields)
    {
        var field = ResolveColumnField(view, fields);
        return GetValueNames(field)
            .Select(value => CreateSnapshot(field, value))
            .ToArray();
    }

    internal static bool SameColumn(BoardColumnSnapshot first, BoardColumnSnapshot second)
        => string.Equals(first.FieldName, second.FieldName, StringComparison.Ordinal)
            && string.Equals(first.SingleSelectOptionName, second.SingleSelectOptionName, StringComparison.Ordinal)
            && string.Equals(first.IterationTitle, second.IterationTitle, StringComparison.Ordinal);

    internal static string Describe(BoardColumnSnapshot column)
        => column.SingleSelectOptionName is { } optionName
            ? $"Single-select column '{column.FieldName}' / '{optionName}'"
            : column.IterationTitle is { } iterationTitle
                ? $"Iteration column '{column.FieldName}' / '{iterationTitle}'"
                : $"unidentified column for field '{column.FieldName}'";

    private static string ColumnKey(BoardColumnSnapshot column)
        => string.Join(
            "\u001f",
            column.FieldName,
            column.SingleSelectOptionName ?? string.Empty,
            column.IterationTitle ?? string.Empty);

    private static bool TryGetValueName(
        FieldSnapshot field,
        BoardColumnSnapshot column,
        out string valueName)
    {
        valueName = column.SingleSelectOptionName ?? column.IterationTitle ?? string.Empty;
        var identityCount = (column.SingleSelectOptionName is null ? 0 : 1)
            + (column.IterationTitle is null ? 0 : 1);
        return identityCount == 1
            && string.Equals(column.FieldName, field.Name, StringComparison.Ordinal)
            && (field.DataType == "SINGLE_SELECT" && column.SingleSelectOptionName is not null
                || field.DataType == "ITERATION" && column.IterationTitle is not null)
            && ValueExists(field, valueName);
    }

    private static BoardColumnSnapshot CreateSnapshot(FieldSnapshot field, string value)
        => new()
        {
            FieldName = field.Name,
            SingleSelectOptionName = field.DataType == "SINGLE_SELECT" ? value : null,
            IterationTitle = field.DataType == "ITERATION" ? value : null,
        };

    private static FieldSnapshot ResolveColumnField(
        ViewSnapshot view,
        IReadOnlyList<FieldSnapshot> fields)
    {
        if (!BoardColumnLimitUi.CanCapture(view, fields, out var reason))
        {
            throw new InvalidOperationException(
                $"view '{view.Name}': Board column visibility cannot be captured — {reason}");
        }

        return fields.Single(field =>
            string.Equals(field.Name, view.VerticalGroupByFields[0], StringComparison.Ordinal));
    }

    private static bool ValueExists(FieldSnapshot field, string value)
        => GetValueNames(field).Contains(value, StringComparer.Ordinal);

    private static IEnumerable<string> GetValueNames(FieldSnapshot field)
        => field.DataType switch
        {
            "SINGLE_SELECT" => field.Options?.Select(option => option.Name) ?? [],
            "ITERATION" when field.IterationConfiguration is { } configuration =>
                configuration.Iterations.Concat(configuration.CompletedIterations)
                    .Select(iteration => iteration.Title),
            _ => [],
        };

    private static Task PauseAsync(CancellationToken cancellationToken)
        => Task.Delay(300, cancellationToken);

    internal sealed record ReconciliationPlan(
        HashSet<string> VisibleNames,
        List<string> Warnings);

    private sealed record PickerColumnState(string Name, bool IsVisible);

    internal sealed record VisibilityChange(string Name, bool ShouldBeVisible);
}
