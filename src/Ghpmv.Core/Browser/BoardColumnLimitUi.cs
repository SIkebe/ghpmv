using System.Globalization;
using Ghpmv.Core.Snapshot;
using Microsoft.Playwright;

namespace Ghpmv.Core.Browser;

internal static class BoardColumnLimitUi
{
    public static async Task<IReadOnlyList<BoardColumnLimitSnapshot>> ReadAsync(
        IPage page,
        ViewSnapshot view,
        IReadOnlyList<FieldSnapshot> fields,
        CancellationToken cancellationToken)
    {
        var field = ResolveColumnField(view, fields);
        var limits = new List<BoardColumnLimitSnapshot>();
        var columns = await ReadDisplayedColumnsAsync(page, cancellationToken).ConfigureAwait(false);
        if (columns.Count == 0)
        {
            throw new InvalidOperationException($"view '{view.Name}': no displayed Board columns were found");
        }

        foreach (var column in columns)
        {
            var currentLimit = await ReadLimitAsync(page, column.Name, cancellationToken).ConfigureAwait(false);
            if (currentLimit is null)
            {
                continue;
            }

            limits.Add(CreateSnapshot(field, column.Name, currentLimit.Value, view.Name));
        }

        return limits;
    }

    public static async Task<IReadOnlyList<string>> ApplyAsync(
        IPage page,
        ViewSnapshot view,
        IReadOnlyList<FieldSnapshot> fields,
        IReadOnlyList<BoardColumnLimitSnapshot> desiredLimits,
        CancellationToken cancellationToken)
    {
        var field = ResolveColumnField(view, fields);
        var warnings = new List<string>();
        var columns = await ReadDisplayedColumnsAsync(page, cancellationToken).ConfigureAwait(false);
        var displayedNames = columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        var desiredByName = desiredLimits
            .Where(limit => string.Equals(limit.FieldName, field.Name, StringComparison.Ordinal))
            .ToDictionary(GetValueName, StringComparer.Ordinal);

        foreach (var desired in desiredByName)
        {
            if (!displayedNames.Contains(desired.Key))
            {
                warnings.Add(
                    $"view '{view.Name}': target {ViewUiImporter.DescribeColumn(desired.Value)} was not found; its limit was not applied");
            }
        }

        foreach (var column in columns)
        {
            if (!ValueExists(field, column.Name))
            {
                continue;
            }

            var desired = desiredByName.TryGetValue(column.Name, out var configured)
                ? configured.Limit
                : (int?)null;
            var current = await ReadLimitAsync(page, column.Name, cancellationToken).ConfigureAwait(false);
            if (current == desired)
            {
                continue;
            }

            await WriteLimitAsync(page, column.Name, desired, cancellationToken).ConfigureAwait(false);
        }

        return warnings;
    }

    internal static int? ParseLimit(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit)
            || limit <= 0)
        {
            throw new InvalidOperationException($"Invalid Board column limit '{value}'");
        }

        return limit;
    }

    internal static bool CanCapture(
        ViewSnapshot view,
        IReadOnlyList<FieldSnapshot> fields,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(fields);
        if (!string.Equals(view.Layout, "BOARD_LAYOUT", StringComparison.Ordinal))
        {
            reason = $"layout '{view.Layout}' is not a Board";
            return false;
        }

        if (view.VerticalGroupByFields.Count != 1)
        {
            reason = $"expected exactly one column-by field, found {view.VerticalGroupByFields.Count}";
            return false;
        }

        var fieldName = view.VerticalGroupByFields[0];
        var matches = fields.Where(field =>
            string.Equals(field.Name, fieldName, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
        {
            reason = $"column-by field '{fieldName}' does not uniquely exist in the snapshot";
            return false;
        }

        if (matches[0].DataType is not ("SINGLE_SELECT" or "ITERATION"))
        {
            reason = $"column-by field '{fieldName}' has unsupported type '{matches[0].DataType}'";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static async Task<IReadOnlyList<DisplayedColumn>> ReadDisplayedColumnsAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        var buttons = Sel.BoardColumnActionsButtons(page);
        await buttons.First.WaitForAsync(new()
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000,
        }).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var columns = new List<DisplayedColumn>();
        var count = await buttons.CountAsync().ConfigureAwait(false);
        for (var index = 0; index < count; index++)
        {
            var button = buttons.Nth(index);
            var columnName = ViewUiExporter.NormalizeUiText(
                await Sel.BoardColumnHeading(button).InnerTextAsync().ConfigureAwait(false));
            if (columnName is null)
            {
                throw new InvalidOperationException("Board column heading has no readable logical value");
            }

            if (columns.Any(column => string.Equals(column.Name, columnName, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"Board contains more than one displayed column named '{columnName}'");
            }

            columns.Add(new DisplayedColumn(columnName));
        }

        return columns;
    }

    private static async Task<int?> ReadLimitAsync(
        IPage page,
        string columnName,
        CancellationToken cancellationToken)
    {
        await Sel.BoardColumnActionsButton(page, columnName).ClickAsync().ConfigureAwait(false);
        var item = Sel.BoardColumnLimitMenuItem(page);
        await item.WaitForAsync().ConfigureAwait(false);
        await item.ClickAsync().ConfigureAwait(false);
        var input = Sel.BoardColumnLimitInput(page);
        await input.WaitForAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var value = await input.InputValueAsync().ConfigureAwait(false);
        await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        try
        {
            return ParseLimit(value);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"Board column '{columnName}' has invalid configured limit '{value}'",
                exception);
        }
    }

    private static async Task WriteLimitAsync(
        IPage page,
        string columnName,
        int? limit,
        CancellationToken cancellationToken)
    {
        await Sel.BoardColumnActionsButton(page, columnName).ClickAsync().ConfigureAwait(false);
        var item = Sel.BoardColumnLimitMenuItem(page);
        await item.WaitForAsync().ConfigureAwait(false);
        await item.ClickAsync().ConfigureAwait(false);
        var input = Sel.BoardColumnLimitInput(page);
        await input.WaitForAsync().ConfigureAwait(false);
        var overlay = Sel.BoardColumnLimitOverlay(input);
        await input.FillAsync(limit?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await Sel.BoardColumnLimitSaveButton(overlay).ClickAsync().ConfigureAwait(false);
        await input.WaitForAsync(new() { State = WaitForSelectorState.Hidden }).ConfigureAwait(false);
    }

    private static FieldSnapshot ResolveColumnField(ViewSnapshot view, IReadOnlyList<FieldSnapshot> fields)
    {
        if (!CanCapture(view, fields, out var reason))
        {
            throw new InvalidOperationException($"view '{view.Name}': Board column limits cannot be captured — {reason}");
        }

        var fieldName = view.VerticalGroupByFields[0];
        return fields.Single(field => string.Equals(field.Name, fieldName, StringComparison.Ordinal));
    }

    private static BoardColumnLimitSnapshot CreateSnapshot(
        FieldSnapshot field,
        string columnName,
        int limit,
        string viewName)
    {
        if (!ValueExists(field, columnName))
        {
            throw new InvalidOperationException(
                $"view '{viewName}': limited column '{columnName}' does not exist in {field.DataType} field '{field.Name}'");
        }

        return new BoardColumnLimitSnapshot
        {
            FieldName = field.Name,
            SingleSelectOptionName = string.Equals(field.DataType, "SINGLE_SELECT", StringComparison.Ordinal)
                ? columnName
                : null,
            IterationTitle = string.Equals(field.DataType, "ITERATION", StringComparison.Ordinal)
                ? columnName
                : null,
            Limit = limit,
        };
    }

    private static bool ValueExists(FieldSnapshot field, string value)
        => field.DataType switch
        {
            "SINGLE_SELECT" => field.Options?.Any(option =>
                string.Equals(option.Name, value, StringComparison.Ordinal)) is true,
            "ITERATION" => field.IterationConfiguration is { } configuration
                && configuration.Iterations.Concat(configuration.CompletedIterations)
                    .Any(iteration => string.Equals(iteration.Title, value, StringComparison.Ordinal)),
            _ => false,
        };

    private static string GetValueName(BoardColumnLimitSnapshot limit)
        => limit.SingleSelectOptionName
            ?? limit.IterationTitle
            ?? throw new InvalidOperationException(
                $"Board column limit for field '{limit.FieldName}' has no logical value identity");

    private sealed record DisplayedColumn(string Name);
}
