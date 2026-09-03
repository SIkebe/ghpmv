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
        var expectedNames = GetValueNames(field).ToHashSet(StringComparer.Ordinal);
        var displayedNames = columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        if (!expectedNames.IsSubsetOf(displayedNames))
        {
            throw new InvalidOperationException(
                $"view '{view.Name}': not all logical Board columns were displayed for complete limit capture");
        }

        foreach (var column in columns.Where(column => expectedNames.Contains(column.Name)))
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

    public static async Task<IReadOnlyList<BoardColumnLimitSnapshot>> ReadCompleteAsync(
        IPage page,
        ViewSnapshot view,
        IReadOnlyList<FieldSnapshot> fields,
        IReadOnlyList<BoardColumnSnapshot> originalVisibility,
        CancellationToken cancellationToken)
    {
        var allColumns = BoardColumnVisibilityUi.GetAllColumns(view, fields);
        var restoreVisibility = !BoardColumnVisibilityUi.SetEquals(originalVisibility, allColumns);
        try
        {
            if (restoreVisibility)
            {
                ThrowIfVisibilityWarnings(await BoardColumnVisibilityUi.ApplyAsync(
                    page,
                    view,
                    fields,
                    allColumns,
                    cancellationToken).ConfigureAwait(false));
            }

            return await ReadAsync(page, view, fields, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (restoreVisibility)
            {
                ThrowIfVisibilityWarnings(await BoardColumnVisibilityUi.ApplyAsync(
                    page,
                    view,
                    fields,
                    originalVisibility,
                    cancellationToken).ConfigureAwait(false));
            }
        }
    }

    public static async Task<IReadOnlyList<string>> ApplyAsync(
        IPage page,
        ViewSnapshot view,
        IReadOnlyList<FieldSnapshot> fields,
        IReadOnlyList<BoardColumnLimitSnapshot> desiredLimits,
        CancellationToken cancellationToken)
    {
        var field = ResolveColumnField(view, fields);
        var columns = await ReadDisplayedColumnsAsync(page, cancellationToken).ConfigureAwait(false);
        var plan = BuildReconciliationPlan(
            view,
            field,
            desiredLimits,
            columns.Select(column => column.Name).ToArray());
        if (plan.Warnings.Count > 0)
        {
            return plan.Warnings;
        }

        foreach (var target in plan.Targets)
        {
            var current = await ReadLimitAsync(page, target.ColumnName, cancellationToken).ConfigureAwait(false);
            if (current == target.Limit)
            {
                continue;
            }

            await WriteLimitAsync(page, target.ColumnName, target.Limit, cancellationToken).ConfigureAwait(false);
        }

        return plan.Warnings;
    }

    internal static ReconciliationPlan BuildReconciliationPlan(
        ViewSnapshot view,
        FieldSnapshot field,
        IReadOnlyList<BoardColumnLimitSnapshot> desiredLimits,
        IReadOnlyList<string> displayedColumnNames)
    {
        var warnings = new List<string>();
        var desiredByName = new Dictionary<string, BoardColumnLimitSnapshot>(StringComparer.Ordinal);
        foreach (var limit in desiredLimits)
        {
            if (limit.Limit <= 0)
            {
                warnings.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"view '{view.Name}': Board column limit for {ViewUiImporter.DescribeColumn(limit)} must be positive, found {limit.Limit}"));
                continue;
            }

            if (!string.Equals(limit.FieldName, field.Name, StringComparison.Ordinal))
            {
                warnings.Add(
                    $"view '{view.Name}': Board column limit for {ViewUiImporter.DescribeColumn(limit)} does not use column-by field '{field.Name}'");
                continue;
            }

            var identityCount = (limit.SingleSelectOptionName is null ? 0 : 1)
                + (limit.IterationTitle is null ? 0 : 1);
            if (identityCount != 1)
            {
                warnings.Add(
                    $"view '{view.Name}': Board column limit for field '{field.Name}' must identify exactly one Single-select option or Iteration");
                continue;
            }

            var valueName = GetValueName(limit);
            var identityMatchesField = field.DataType switch
            {
                "SINGLE_SELECT" => limit.SingleSelectOptionName is not null,
                "ITERATION" => limit.IterationTitle is not null,
                _ => false,
            };
            if (!identityMatchesField || !ValueExists(field, valueName))
            {
                warnings.Add(
                    $"view '{view.Name}': Board column limit for {ViewUiImporter.DescribeColumn(limit)} is not a valid value of {field.DataType} field '{field.Name}'");
                continue;
            }

            if (!desiredByName.TryAdd(valueName, limit))
            {
                warnings.Add(
                    $"view '{view.Name}': duplicate Board column limit for {ViewUiImporter.DescribeColumn(limit)}");
            }
        }

        if (warnings.Count > 0)
        {
            return new ReconciliationPlan([], warnings);
        }

        var displayed = displayedColumnNames.ToHashSet(StringComparer.Ordinal);
        foreach (var missingColumn in desiredByName.Keys.Where(name => !displayed.Contains(name)))
        {
            warnings.Add(
                $"view '{view.Name}': target {ViewUiImporter.DescribeColumn(desiredByName[missingColumn])} was not found; no Board limits were changed");
        }

        if (warnings.Count > 0)
        {
            return new ReconciliationPlan([], warnings);
        }

        var targets = displayedColumnNames
            .Where(columnName => ValueExists(field, columnName))
            .Select(columnName => new ReconciliationTarget(
                columnName,
                desiredByName.TryGetValue(columnName, out var configured)
                    ? configured.Limit
                    : null))
            .ToArray();
        return new ReconciliationPlan(targets, warnings);
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
        await PauseAsync(cancellationToken).ConfigureAwait(false);
        var item = Sel.BoardColumnLimitMenuItem(page);
        await item.WaitForAsync().ConfigureAwait(false);
        await item.ClickAsync().ConfigureAwait(false);
        await PauseAsync(cancellationToken).ConfigureAwait(false);
        var input = Sel.BoardColumnLimitInput(page);
        await input.WaitForAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var value = await input.InputValueAsync().ConfigureAwait(false);
        await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        await PauseAsync(cancellationToken).ConfigureAwait(false);
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
        await PauseAsync(cancellationToken).ConfigureAwait(false);
        var item = Sel.BoardColumnLimitMenuItem(page);
        await item.WaitForAsync().ConfigureAwait(false);
        await item.ClickAsync().ConfigureAwait(false);
        await PauseAsync(cancellationToken).ConfigureAwait(false);
        var input = Sel.BoardColumnLimitInput(page);
        await input.WaitForAsync().ConfigureAwait(false);
        var overlay = Sel.BoardColumnLimitOverlay(input);
        await input.FillAsync(limit?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).ConfigureAwait(false);
        await PauseAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await Sel.BoardColumnLimitSaveButton(overlay).ClickAsync().ConfigureAwait(false);
        await input.WaitForAsync(new() { State = WaitForSelectorState.Hidden }).ConfigureAwait(false);
        await PauseAsync(cancellationToken).ConfigureAwait(false);
    }

    // 300ms between consecutive UI operations (BROWSER_AUTOMATION_PLAN §1.4).
    private static Task PauseAsync(CancellationToken cancellationToken) => Task.Delay(300, cancellationToken);

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
        => GetValueNames(field).Contains(value, StringComparer.Ordinal);

    private static IEnumerable<string> GetValueNames(FieldSnapshot field)
        => field.DataType switch
        {
            "SINGLE_SELECT" => field.Options?.Select(option => option.Name) ?? [],
            "ITERATION" => field.IterationConfiguration is { } configuration
                ? configuration.Iterations.Concat(configuration.CompletedIterations)
                    .Select(iteration => iteration.Title)
                : [],
            _ => [],
        };

    private static string GetValueName(BoardColumnLimitSnapshot limit)
        => limit.SingleSelectOptionName
            ?? limit.IterationTitle
            ?? throw new InvalidOperationException(
                $"Board column limit for field '{limit.FieldName}' has no logical value identity");

    private static void ThrowIfVisibilityWarnings(IReadOnlyList<string> warnings)
    {
        if (warnings.Count > 0)
        {
            throw new InvalidOperationException(string.Join("; ", warnings));
        }
    }

    private sealed record DisplayedColumn(string Name);

    internal sealed record ReconciliationPlan(
        IReadOnlyList<ReconciliationTarget> Targets,
        IReadOnlyList<string> Warnings);

    internal sealed record ReconciliationTarget(string ColumnName, int? Limit);
}
