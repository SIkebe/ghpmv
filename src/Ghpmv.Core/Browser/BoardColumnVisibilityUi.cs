using Ghpmv.Core.Snapshot;
using Microsoft.Playwright;

namespace Ghpmv.Core.Browser;

internal static class BoardColumnVisibilityUi
{
    public static async Task<IReadOnlyList<BoardColumnSnapshot>> ReadAsync(
        IPage page,
        ViewSnapshot view,
        IReadOnlyList<FieldSnapshot> fields,
        CancellationToken cancellationToken)
    {
        var field = ResolveColumnField(view, fields);
        await Sel.AddBoardColumnButton(page).WaitForAsync(new()
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000,
        }).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var visibleNames = new HashSet<string>(StringComparer.Ordinal);
        var buttons = Sel.BoardColumnActionsButtons(page);
        var count = await buttons.CountAsync().ConfigureAwait(false);
        for (var index = 0; index < count; index++)
        {
            var name = ViewUiExporter.NormalizeUiText(
                await Sel.BoardColumnHeading(buttons.Nth(index)).InnerTextAsync().ConfigureAwait(false));
            if (name is not null && ValueExists(field, name))
            {
                visibleNames.Add(name);
            }
        }

        return GetValueNames(field)
            .Where(visibleNames.Contains)
            .Select(value => CreateSnapshot(field, value))
            .ToArray();
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

        foreach (var change in BuildApplyOrder(available.ToList(), plan.VisibleNames))
        {
            await ApplyVisibilityAsync(change.Name, change.ShouldBeVisible).ConfigureAwait(false);
        }

        async Task ApplyVisibilityAsync(string name, bool shouldBeVisible)
        {
            var option = await FindOptionAsync(options, name).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"view '{view.Name}': Board column '{field.Name}' / '{name}' disappeared from the visibility picker");
            var isVisible = string.Equals(
                await option.GetAttributeAsync("aria-checked").ConfigureAwait(false),
                "true",
                StringComparison.Ordinal);
            var isDisabled = string.Equals(
                await option.GetAttributeAsync("aria-disabled").ConfigureAwait(false),
                "true",
                StringComparison.Ordinal);
            if (isDisabled && shouldBeVisible != isVisible)
            {
                plan.Warnings.Add(
                    $"view '{view.Name}': Board column '{field.Name}' / '{name}' is disabled on the target and its visibility could not be changed");
            }
            else if (shouldBeVisible != isVisible)
            {
                await option.ClickAsync().ConfigureAwait(false);
                await PauseAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var missing in plan.VisibleNames.Where(name => !available.Contains(name)))
        {
            plan.Warnings.Add(
                $"view '{view.Name}': visible Board column '{field.Name}' / '{missing}' is not available on the target; no other column was selected");
        }

        await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        return plan.Warnings;
    }

    private static async Task<ILocator?> FindOptionAsync(ILocator options, string name)
    {
        var count = await options.CountAsync().ConfigureAwait(false);
        for (var index = 0; index < count; index++)
        {
            var option = options.Nth(index);
            var currentName = ViewUiExporter.NormalizeUiText(
                await option.InnerTextAsync().ConfigureAwait(false));
            if (string.Equals(currentName, name, StringComparison.Ordinal))
            {
                return option;
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
                .Select(name => new VisibilityChange(name, ShouldBeVisible: false)))
            .ToList();

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

    internal sealed record VisibilityChange(string Name, bool ShouldBeVisible);
}
