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
    private const int ViewPersistenceAttempts = 3;

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
        ValidateSharedRoadmapDisplaySettings(snapshot.Views);

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
                await ApplyAndVerifyBrowserOnlySettingsAsync(page, view, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is PlaywrightException or TimeoutException or InvalidOperationException)
            {
                _warnings.Add($"view '{view.Name}': browser-only settings could not be applied — {exception.Message}");
            }
        }

        await ApplyTabOrderRecoverablyAsync(
            () => ReorderTabsAsync(
                page,
                snapshot,
                viewNumbers,
                cancellationToken),
            _warnings).ConfigureAwait(false);
        // GitHub keeps some View preferences in browser storage rather than the Project API.
        await _session.SaveStateAsync(cancellationToken).ConfigureAwait(false);
    }

    public static void ValidateSharedRoadmapDisplaySettings(IReadOnlyList<ViewSnapshot> views)
    {
        ArgumentNullException.ThrowIfNull(views);
        var roadmaps = views
            .Where(view => string.Equals(view.Layout, "ROADMAP_LAYOUT", StringComparison.Ordinal))
            .Select(view => view.Ui?.Roadmap)
            .Where(settings => settings is not null)
            .ToArray();
        if (roadmaps.Where(settings => settings!.TruncateTitles is not null)
                .Select(settings => settings!.TruncateTitles)
                .Distinct()
                .Count() > 1
            || roadmaps.Where(settings => settings!.ShowDateFields is not null)
                .Select(settings => settings!.ShowDateFields)
                .Distinct()
                .Count() > 1)
        {
            throw new InvalidOperationException(
                "Roadmap Truncate titles and Show date fields are project-shared and must have one consistent value across all Roadmap Views.");
        }
    }

    /// <summary>Applies and saves only the complete Field sum selection for one target View.</summary>
    public async Task ApplyFieldSumAsync(
        string ownerLogin,
        ProjectOwnerType ownerType,
        int projectNumber,
        int viewNumber,
        string viewName,
        IReadOnlyList<string> fieldSum,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerLogin);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewName);
        ArgumentNullException.ThrowIfNull(fieldSum);

        OnProgress?.Invoke($"Applying field-sum drift for view '{viewName}'...");
        try
        {
            var page = await _session.GetPageAsync(cancellationToken).ConfigureAwait(false);
            var url = BrowserProjectUrl.Build(
                _session.BaseUrl,
                ownerLogin,
                ownerType,
                projectNumber,
                string.Create(CultureInfo.InvariantCulture, $"views/{viewNumber}"));
            await _session.GotoAsync(url, cancellationToken).ConfigureAwait(false);
            await ApplyAndVerifyFieldSumAsync(
                page,
                viewName,
                fieldSum,
                cancellationToken).ConfigureAwait(false);
            await _session.SaveStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException or InvalidOperationException)
        {
            _warnings.Add($"view '{viewName}': field-sum drift could not be applied — {exception.Message}");
        }
    }

    /// <summary>Applies and verifies both persisted Roadmap display checkboxes for one target View.</summary>
    public async Task ApplyRoadmapDisplayOptionsAsync(
        string ownerLogin,
        ProjectOwnerType ownerType,
        int projectNumber,
        int viewNumber,
        string viewName,
        bool truncateTitles,
        bool showDateFields,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerLogin);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewName);

        OnProgress?.Invoke($"Applying Roadmap display-option drift for view '{viewName}'...");
        var sessionStarted = false;
        try
        {
            var page = await _session.GetPageAsync(cancellationToken).ConfigureAwait(false);
            sessionStarted = true;
            var url = BrowserProjectUrl.Build(
                _session.BaseUrl,
                ownerLogin,
                ownerType,
                projectNumber,
                string.Create(CultureInfo.InvariantCulture, $"views/{viewNumber}"));
            await _session.GotoAsync(url, cancellationToken).ConfigureAwait(false);

            (bool? TruncateTitles, bool? ShowDateFields) persisted = default;
            for (var attempt = 1; attempt <= ViewPersistenceAttempts; attempt++)
            {
                var warningStart = _warnings.Count;
                await TrySetMenuCheckboxAsync(
                    page,
                    "Truncate titles",
                    truncateTitles,
                    viewName,
                    cancellationToken).ConfigureAwait(false);
                await TrySetMenuCheckboxAsync(
                    page,
                    "Show date fields",
                    showDateFields,
                    viewName,
                    cancellationToken).ConfigureAwait(false);
                await SaveViewAsync(page, cancellationToken).ConfigureAwait(false);
                persisted = await ReadPersistedRoadmapDisplayOptionsAsync(page, cancellationToken).ConfigureAwait(false);
                if (persisted.TruncateTitles == truncateTitles
                    && persisted.ShowDateFields == showDateFields)
                {
                    return;
                }

                if (attempt < ViewPersistenceAttempts)
                {
                    _warnings.RemoveRange(warningStart, _warnings.Count - warningStart);
                    OnProgress?.Invoke(
                        $"View '{viewName}' did not persist the Roadmap display options; retrying ({attempt + 1}/{ViewPersistenceAttempts})...");
                    await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);
                    await PauseAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            if (persisted.TruncateTitles != truncateTitles)
            {
                _warnings.Add(
                    $"view '{viewName}': Truncate titles expected '{FormatBoolean(truncateTitles)}', "
                    + $"actual '{FormatBoolean(persisted.TruncateTitles)}' did not persist after {ViewPersistenceAttempts} attempts");
            }

            if (persisted.ShowDateFields != showDateFields)
            {
                _warnings.Add(
                    $"view '{viewName}': Show date fields expected '{FormatBoolean(showDateFields)}', "
                    + $"actual '{FormatBoolean(persisted.ShowDateFields)}' did not persist after {ViewPersistenceAttempts} attempts");
            }
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException or InvalidOperationException)
        {
            _warnings.Add($"view '{viewName}': Roadmap display-option drift could not be applied — {exception.Message}");
        }
        finally
        {
            if (sessionStarted)
            {
                // Preserve partial browser-storage writes even when read-back reports a recoverable mismatch.
                await _session.SaveStateAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    internal static async Task ApplyTabOrderRecoverablyAsync(
        Func<Task> reorderAsync,
        List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(reorderAsync);
        ArgumentNullException.ThrowIfNull(warnings);

        try
        {
            await reorderAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException or InvalidOperationException)
        {
            warnings.Add($"view tab order could not be applied — {exception.Message}");
        }
    }

    private async Task ReorderTabsAsync(
        IPage page,
        ProjectSnapshot snapshot,
        IReadOnlyDictionary<int, int> viewNumbers,
        CancellationToken cancellationToken)
    {
        if (snapshot.Views.Count < 2 || snapshot.Views.Any(view => view.TabPosition is null))
        {
            return;
        }

        var desired = snapshot.Views
            .OrderBy(view => view.TabPosition)
            .Select(view => viewNumbers.TryGetValue(view.Number, out var targetNumber)
                ? targetNumber
                : (int?)null)
            .ToList();
        if (desired.Any(number => number is null))
        {
            _warnings.Add("view tab order could not be applied because one or more source views have no target mapping");
            return;
        }

        var desiredNumbers = desired.Select(number => number!.Value).ToList();
        var importedNumbers = desiredNumbers.ToHashSet();
        var currentNumbers = await ReadImportedTabOrderUntilCompleteAsync(
            page,
            importedNumbers,
            desiredNumbers.Count,
            cancellationToken).ConfigureAwait(false);
        if (currentNumbers.Count != desiredNumbers.Count)
        {
            _warnings.Add("view tab order could not be applied because the target View list is incomplete");
            return;
        }

        var moves = BuildTabMovePlan(currentNumbers, desiredNumbers);
        if (moves.Count == 0)
        {
            return;
        }

        var names = snapshot.Views.ToDictionary(
            view => viewNumbers[view.Number],
            view => view.Name);
        OnProgress?.Invoke(string.Create(
            CultureInfo.InvariantCulture,
            $"Reordering View tabs with {moves.Count} drag operation(s)..."));
        _warnings.AddRange(await ApplyTabMovesAsync(
            moves,
            desiredNumbers,
            names,
            async (move, token) =>
            {
                var source = Sel.DraggableViewTab(page, move.ViewNumber);
                var anchor = Sel.DraggableViewTab(page, move.AnchorViewNumber);
                await source.ScrollIntoViewIfNeededAsync().ConfigureAwait(false);
                await anchor.ScrollIntoViewIfNeededAsync().ConfigureAwait(false);
                var anchorBox = await anchor.BoundingBoxAsync().ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"view tab '{names[move.AnchorViewNumber]}' has no visible bounding box");
                await source.DragToAsync(anchor, new()
                {
                    TargetPosition = new()
                    {
                        X = move.PlaceBefore ? 2 : Math.Max(2, anchorBox.Width - 2),
                        Y = anchorBox.Height / 2,
                    },
                }).ConfigureAwait(false);
                await PauseAsync(token).ConfigureAwait(false);
            },
            token => ReadImportedTabOrderAsync(
                page,
                importedNumbers,
                desiredNumbers,
                token),
            cancellationToken).ConfigureAwait(false));
    }

    internal static async Task<List<string>> ApplyTabMovesAsync(
        List<TabMove> moves,
        List<int> desiredNumbers,
        Dictionary<int, string> names,
        Func<TabMove, CancellationToken, Task> dragAsync,
        Func<CancellationToken, Task<IReadOnlyList<int>>> readOrderAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(moves);
        ArgumentNullException.ThrowIfNull(desiredNumbers);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(dragAsync);
        ArgumentNullException.ThrowIfNull(readOrderAsync);

        var warnings = new List<string>();
        foreach (var move in moves)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await dragAsync(move, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is PlaywrightException or TimeoutException or InvalidOperationException)
            {
                warnings.Add($"view tab '{names[move.ViewNumber]}' could not be reordered — {exception.Message}");
            }
        }

        var actualNumbers = await readOrderAsync(cancellationToken).ConfigureAwait(false);
        if (!desiredNumbers.SequenceEqual(actualNumbers))
        {
            warnings.Add(
                $"view tab order could not be fully applied (expected [{FormatOrder(desiredNumbers, names)}], actual [{FormatOrder(actualNumbers, names)}])");
        }

        return warnings;
    }

    private static Task<IReadOnlyList<int>> ReadImportedTabOrderUntilCompleteAsync(
        IPage page,
        HashSet<int> importedNumbers,
        int expectedCount,
        CancellationToken cancellationToken)
        => PollImportedTabOrderAsync(
            page,
            importedNumbers,
            order => order.Count == expectedCount,
            cancellationToken);

    private static async Task<IReadOnlyList<int>> ReadImportedTabOrderAsync(
        IPage page,
        HashSet<int> importedNumbers,
        IReadOnlyList<int> desiredNumbers,
        CancellationToken cancellationToken)
        => await PollImportedTabOrderAsync(
            page,
            importedNumbers,
            order => order.SequenceEqual(desiredNumbers),
            cancellationToken).ConfigureAwait(false);

    private static Task<IReadOnlyList<int>> PollImportedTabOrderAsync(
        IPage page,
        HashSet<int> importedNumbers,
        Func<IReadOnlyList<int>, bool> completed,
        CancellationToken cancellationToken)
        => PollTabOrderAsync(
            async token => (await ViewTabOrder.ReadAsync(page, token).ConfigureAwait(false))
                .Where(importedNumbers.Contains)
                .ToList(),
            completed,
            token => Task.Delay(TimeSpan.FromMilliseconds(500), token),
            cancellationToken);

    internal static async Task<IReadOnlyList<int>> PollTabOrderAsync(
        Func<CancellationToken, Task<IReadOnlyList<int>>> readAsync,
        Func<IReadOnlyList<int>, bool> completed,
        Func<CancellationToken, Task> delayAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readAsync);
        ArgumentNullException.ThrowIfNull(completed);
        ArgumentNullException.ThrowIfNull(delayAsync);

        IReadOnlyList<int> result = [];
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0)
            {
                await delayAsync(cancellationToken).ConfigureAwait(false);
            }

            result = await readAsync(cancellationToken).ConfigureAwait(false);
            if (completed(result))
            {
                break;
            }
        }

        return result;
    }

    internal static List<TabMove> BuildTabMovePlan(
        IReadOnlyList<int> current,
        IReadOnlyList<int> desired)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(desired);
        if (current.Count != desired.Count
            || current.Distinct().Count() != current.Count
            || desired.Distinct().Count() != desired.Count
            || !current.ToHashSet().SetEquals(desired))
        {
            throw new ArgumentException("Current and desired tab orders must contain the same unique View numbers.");
        }

        if (current.SequenceEqual(desired))
        {
            return [];
        }

        var desiredIndexes = desired
            .Select((number, index) => (number, index))
            .ToDictionary(pair => pair.number, pair => pair.index);
        var sequence = current.Select(number => desiredIndexes[number]).ToArray();
        var lengths = Enumerable.Repeat(1, sequence.Length).ToArray();
        var previous = Enumerable.Repeat(-1, sequence.Length).ToArray();
        var longestEnd = 0;
        for (var index = 0; index < sequence.Length; index++)
        {
            for (var candidate = 0; candidate < index; candidate++)
            {
                if (sequence[candidate] < sequence[index]
                    && lengths[candidate] + 1 > lengths[index])
                {
                    lengths[index] = lengths[candidate] + 1;
                    previous[index] = candidate;
                }
            }

            if (lengths[index] > lengths[longestEnd])
            {
                longestEnd = index;
            }
        }

        var fixedTabs = new HashSet<int>();
        for (var index = longestEnd; index >= 0; index = previous[index])
        {
            fixedTabs.Add(current[index]);
            if (previous[index] < 0)
            {
                break;
            }
        }

        var simulated = current.ToList();
        var moves = new List<TabMove>(desired.Count - fixedTabs.Count);
        for (var index = desired.Count - 1; index >= 0; index--)
        {
            var number = desired[index];
            if (fixedTabs.Contains(number))
            {
                continue;
            }

            simulated.Remove(number);
            if (index + 1 < desired.Count)
            {
                var anchor = desired[index + 1];
                moves.Add(new TabMove(number, anchor, PlaceBefore: true));
                simulated.Insert(simulated.IndexOf(anchor), number);
            }
            else
            {
                var anchor = simulated[^1];
                moves.Add(new TabMove(number, anchor, PlaceBefore: false));
                simulated.Add(number);
            }
        }

        return moves;
    }

    private static string FormatOrder(IEnumerable<int> order, Dictionary<int, string> names)
        => string.Join(", ", order.Select(number => names.TryGetValue(number, out var name)
            ? name
            : number.ToString(CultureInfo.InvariantCulture)));

    internal sealed record TabMove(int ViewNumber, int AnchorViewNumber, bool PlaceBefore);

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

        // Grouped Table/Roadmap views do not reliably expose Field sum until the
        // grouping change has been persisted and the View has reloaded.
        await SaveViewAsync(page, cancellationToken).ConfigureAwait(false);

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
            var sort = view.SortByFields[0];
            if (!await IsSortAlreadyAppliedAsync(page, sort, cancellationToken).ConfigureAwait(false))
            {
                await TryEnsureSortFieldVisibleAsync(
                    page,
                    sort.Field,
                    view.Name,
                    cancellationToken).ConfigureAwait(false);
                await TrySetSortAsync(page, sort, view.Name, cancellationToken).ConfigureAwait(false);
            }
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

        // Grouped Table/Roadmap views and Board views expose the same checkbox overlay
        // (Count + number fields). Apply the complete desired set, including empty.
        if (FieldSumValuesToApply(view) is { } fieldSum)
        {
            await TrySetCheckboxesAsync(page, "Field sum", fieldSum, view.Name, cancellationToken).ConfigureAwait(false);
        }

        if (view.Ui?.Roadmap is { } roadmap)
        {
            if (roadmap.TruncateTitles is { } truncateTitles)
            {
                await TrySetMenuCheckboxAsync(
                    page,
                    "Truncate titles",
                    truncateTitles,
                    view.Name,
                    cancellationToken).ConfigureAwait(false);
            }

            if (roadmap.ShowDateFields is { } showDateFields)
            {
                await TrySetMenuCheckboxAsync(
                    page,
                    "Show date fields",
                    showDateFields,
                    view.Name,
                    cancellationToken).ConfigureAwait(false);
            }

            if (roadmap.StartField is not null || roadmap.TargetField is not null)
            {
                await TrySetDateFieldsAsync(page, roadmap, view.Name, cancellationToken).ConfigureAwait(false);
            }

            if (roadmap.Zoom is { } zoom)
            {
                await TrySetSingleAsync(page, "Zoom level", zoom, view.Name, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static IReadOnlyList<string>? FieldSumValuesToApply(ViewSnapshot view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (view.Ui is null)
        {
            return null;
        }

        return FieldSumControlExpected(view) ? view.Ui.FieldSum ?? [] : null;
    }

    internal static bool FieldSumControlExpected(ViewSnapshot view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return string.Equals(view.Layout, "BOARD_LAYOUT", StringComparison.Ordinal)
            || view.GroupByFields.Count > 0
            && (view.Layout is "TABLE_LAYOUT" or "ROADMAP_LAYOUT");
    }

    private async Task ApplyAndVerifyBrowserOnlySettingsAsync(
        IPage page,
        ViewSnapshot view,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> differences = [];
        for (var attempt = 1; attempt <= ViewPersistenceAttempts; attempt++)
        {
            var warningStart = _warnings.Count;
            await ApplyBrowserOnlySettingsAsync(page, view, cancellationToken).ConfigureAwait(false);
            await SaveViewAsync(page, cancellationToken).ConfigureAwait(false);

            var persisted = await ReadPersistedSettingsAsync(page, view, cancellationToken).ConfigureAwait(false);
            differences = CollectPersistenceDifferences(view, persisted);
            if (differences.Count == 0)
            {
               if (view.Ui?.Roadmap is { } roadmap)
               {
                   await ApplyAndVerifyCheckboxesAsync(
                       page,
                       view.Name,
                       "Markers",
                       roadmap.Markers ?? [],
                       cancellationToken).ConfigureAwait(false);
               }

               return;
            }

            if (attempt < ViewPersistenceAttempts)
            {
               _warnings.RemoveRange(warningStart, _warnings.Count - warningStart);
               OnProgress?.Invoke(
                   $"View '{view.Name}' did not persist all browser settings; retrying ({attempt + 1}/{ViewPersistenceAttempts})...");
               await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);
               await PauseAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var difference in differences)
        {
            _warnings.Add(
               $"view '{view.Name}': {difference} did not persist after {ViewPersistenceAttempts} attempts");
        }
    }

    private async Task ApplyAndVerifyFieldSumAsync(
        IPage page,
        string viewName,
        IReadOnlyList<string> fieldSum,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? persisted = null;
        for (var attempt = 1; attempt <= ViewPersistenceAttempts; attempt++)
        {
            var warningStart = _warnings.Count;
            await TrySetCheckboxesAsync(page, "Field sum", fieldSum, viewName, cancellationToken).ConfigureAwait(false);
            await SaveViewAsync(page, cancellationToken).ConfigureAwait(false);
            persisted = await ReadPersistedFieldSumAsync(page, cancellationToken).ConfigureAwait(false);
            if (FieldSumMatches(fieldSum, persisted))
            {
                return;
            }

            if (attempt < ViewPersistenceAttempts)
            {
                _warnings.RemoveRange(warningStart, _warnings.Count - warningStart);
                OnProgress?.Invoke(
                    $"View '{viewName}' did not persist the Field sum selection; retrying ({attempt + 1}/{ViewPersistenceAttempts})...");
                await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);
                await PauseAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        _warnings.Add(
            $"view '{viewName}': field-sum expected [{string.Join(", ", fieldSum)}], "
            + $"actual [{(persisted is null ? "unavailable" : string.Join(", ", persisted))}] "
            + $"did not persist after {ViewPersistenceAttempts} attempts");
    }

    private async Task ApplyAndVerifyCheckboxesAsync(
        IPage page,
        string viewName,
        string label,
        IReadOnlyList<string> expected,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? persisted = null;
        for (var attempt = 1; attempt <= ViewPersistenceAttempts; attempt++)
        {
            var warningStart = _warnings.Count;
            await TrySetCheckboxesAsync(page, label, expected, viewName, cancellationToken).ConfigureAwait(false);
            await SaveViewAsync(page, cancellationToken).ConfigureAwait(false);
            persisted = await ReadPersistedCheckboxesAsync(page, label, cancellationToken).ConfigureAwait(false);
            if (CheckboxSelectionMatches(expected, persisted))
            {
                return;
            }

            if (attempt < ViewPersistenceAttempts)
            {
                _warnings.RemoveRange(warningStart, _warnings.Count - warningStart);
                OnProgress?.Invoke(
                    $"View '{viewName}' did not persist the {label} selection; retrying ({attempt + 1}/{ViewPersistenceAttempts})...");
                await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);
                await PauseAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        _warnings.Add(
            $"view '{viewName}': {label} expected [{string.Join(", ", expected)}], "
            + $"actual [{(persisted is null ? "unavailable" : string.Join(", ", persisted))}] "
            + $"did not persist after {ViewPersistenceAttempts} attempts");
    }

    private static async Task<PersistedViewSettings> ReadPersistedSettingsAsync(
        IPage page,
        ViewSnapshot view,
        CancellationToken cancellationToken)
    {
        var menu = await OpenViewMenuAsync(page, cancellationToken).ConfigureAwait(false);
        try
        {
            var groupingLabel = string.Equals(view.Layout, "BOARD_LAYOUT", StringComparison.Ordinal)
               ? "Swimlanes"
               : "Group by";
            var groupBy = await ReadMenuValueAsync(menu, groupingLabel).ConfigureAwait(false);
            var columnBy = string.Equals(view.Layout, "BOARD_LAYOUT", StringComparison.Ordinal)
               ? await ReadMenuValueAsync(menu, "Column by").ConfigureAwait(false)
               : null;
            var sliceBy = view.Ui is null
               ? null
               : await ReadMenuValueAsync(menu, "Slice by").ConfigureAwait(false);
            var truncateTitles = view.Ui?.Roadmap?.TruncateTitles is null
                ? null
                : await ReadMenuCheckboxAsync(menu, "Truncate titles").ConfigureAwait(false);
            var showDateFields = view.Ui?.Roadmap?.ShowDateFields is null
                ? null
                : await ReadMenuCheckboxAsync(menu, "Show date fields").ConfigureAwait(false);

            var expectedFieldSum = FieldSumValuesToApply(view);
            IReadOnlyList<string> fieldSum = [];
            var fieldSumAvailable = false;
            if (expectedFieldSum is not null)
            {
                var fieldSumItem = Sel.ConfigurationMenuItem(menu, "Field sum");
                if (await fieldSumItem.CountAsync().ConfigureAwait(false) > 0)
                {
                    await fieldSumItem.First.ClickAsync().ConfigureAwait(false);
                    await PauseAsync(cancellationToken).ConfigureAwait(false);
                    var overlay = Sel.OpenMenu(page);
                    await overlay.WaitForAsync().ConfigureAwait(false);
                    if (await ReadCheckedValuesAsync(overlay).ConfigureAwait(false) is { } checkedValues)
                    {
                        fieldSum = checkedValues;
                        fieldSumAvailable = true;
                    }
                }
            }

            return new PersistedViewSettings(
                groupBy,
                columnBy,
                sliceBy,
                fieldSumAvailable,
                fieldSum,
                truncateTitles,
                showDateFields);
        }
        finally
        {
            await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<string>?> ReadPersistedFieldSumAsync(
        IPage page,
        CancellationToken cancellationToken)
        => await ReadPersistedCheckboxesAsync(page, "Field sum", cancellationToken).ConfigureAwait(false);

    private static async Task<IReadOnlyList<string>?> ReadPersistedCheckboxesAsync(
        IPage page,
        string label,
        CancellationToken cancellationToken)
    {
        var menu = await OpenViewMenuAsync(page, cancellationToken).ConfigureAwait(false);
        try
        {
            var item = Sel.ConfigurationMenuItem(menu, label);
            if (await item.CountAsync().ConfigureAwait(false) == 0)
            {
                return null;
            }

            await item.First.ClickAsync().ConfigureAwait(false);
            await PauseAsync(cancellationToken).ConfigureAwait(false);
            var overlay = Sel.OpenMenu(page);
            await overlay.WaitForAsync().ConfigureAwait(false);
            return await ReadCheckedValuesAsync(overlay).ConfigureAwait(false);
        }
        finally
        {
            await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<(bool? TruncateTitles, bool? ShowDateFields)> ReadPersistedRoadmapDisplayOptionsAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        var menu = await OpenViewMenuAsync(page, cancellationToken).ConfigureAwait(false);
        try
        {
            return (
                await ReadMenuCheckboxAsync(menu, "Truncate titles").ConfigureAwait(false),
                await ReadMenuCheckboxAsync(menu, "Show date fields").ConfigureAwait(false));
        }
        finally
        {
            await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string?> ReadMenuValueAsync(ILocator menu, string label)
    {
        var item = Sel.ConfigurationMenuItem(menu, label);
        return await item.CountAsync().ConfigureAwait(false) == 0
            ? null
            : ViewUiExporter.ParseMenuValue(await item.First.InnerTextAsync().ConfigureAwait(false));
    }

    private static async Task<bool?> ReadMenuCheckboxAsync(ILocator menu, string label)
    {
        var option = Sel.ViewOptionCheckbox(menu, label);
        if (await option.CountAsync().ConfigureAwait(false) == 0)
        {
            return null;
        }

        return await option.First.GetAttributeAsync("aria-checked").ConfigureAwait(false) switch
        {
            "true" => true,
            "false" => false,
            _ => null,
        };
    }

    private static async Task<IReadOnlyList<string>?> ReadCheckedValuesAsync(ILocator overlay)
    {
        var values = new List<string>();
        var options = Sel.CheckboxOptions(overlay);
        var count = await options.CountAsync().ConfigureAwait(false);
        if (count == 0)
        {
            return null;
        }

        for (var index = 0; index < count; index++)
        {
            var option = options.Nth(index);
            if (!string.Equals(
                   await option.GetAttributeAsync("aria-checked").ConfigureAwait(false),
                   "true",
                   StringComparison.Ordinal))
            {
               continue;
            }

            if (ViewUiExporter.NormalizeUiText(await option.InnerTextAsync().ConfigureAwait(false)) is { } value)
            {
               values.Add(value);
            }
        }

        return values;
    }

    internal static IReadOnlyList<string> CollectPersistenceDifferences(
        ViewSnapshot expected,
        PersistedViewSettings actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        var differences = new List<string>();
        var expectedGroupBy = expected.GroupByFields.Count == 0
            ? null
            : expected.GroupByFields[0];
        if (!string.Equals(expectedGroupBy, actual.GroupBy, StringComparison.Ordinal))
        {
            differences.Add($"grouping expected '{FormatValue(expectedGroupBy)}', actual '{FormatValue(actual.GroupBy)}'");
        }

        if (string.Equals(expected.Layout, "BOARD_LAYOUT", StringComparison.Ordinal))
        {
            var expectedColumnBy = expected.VerticalGroupByFields.Count == 0
                ? null
                : expected.VerticalGroupByFields[0];
            if (!string.Equals(expectedColumnBy, actual.ColumnBy, StringComparison.Ordinal))
            {
               differences.Add($"column-by expected '{FormatValue(expectedColumnBy)}', actual '{FormatValue(actual.ColumnBy)}'");
            }
        }

        if (expected.Ui is not null
            && !string.Equals(expected.Ui.SliceBy, actual.SliceBy, StringComparison.Ordinal))
        {
            differences.Add($"slice-by expected '{FormatValue(expected.Ui.SliceBy)}', actual '{FormatValue(actual.SliceBy)}'");
        }

        if (FieldSumValuesToApply(expected) is { } expectedFieldSum)
        {
            if (!actual.FieldSumAvailable)
            {
               differences.Add("field-sum control is unavailable");
            }
            else if (!SetEquals(expectedFieldSum, actual.FieldSum))
            {
               differences.Add(
                   $"field-sum expected [{string.Join(", ", expectedFieldSum)}], actual [{string.Join(", ", actual.FieldSum)}]");
            }
        }

        if (expected.Ui?.Roadmap is { } roadmap)
        {
            if (roadmap.TruncateTitles is { } expectedTruncateTitles
                && actual.TruncateTitles != expectedTruncateTitles)
            {
                differences.Add(
                    $"truncate-titles expected '{FormatBoolean(expectedTruncateTitles)}', actual '{FormatBoolean(actual.TruncateTitles)}'");
            }

            if (roadmap.ShowDateFields is { } expectedShowDateFields
                && actual.ShowDateFields != expectedShowDateFields)
            {
                differences.Add(
                    $"show-date-fields expected '{FormatBoolean(expectedShowDateFields)}', actual '{FormatBoolean(actual.ShowDateFields)}'");
            }
        }

        return differences;
    }

    private static bool SetEquals(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
        => expected.Count == actual.Count
            && expected.ToHashSet(StringComparer.Ordinal).SetEquals(actual);

    internal static bool FieldSumMatches(
        IReadOnlyList<string> expected,
        IReadOnlyList<string>? actual)
        => CheckboxSelectionMatches(expected, actual);

    internal static bool CheckboxSelectionMatches(
        IReadOnlyList<string> expected,
        IReadOnlyList<string>? actual)
        => actual is not null && SetEquals(expected, actual);

    private static string FormatValue(string? value) => value ?? "none";

    private static string FormatBoolean(bool? value) => value?.ToString().ToLowerInvariant() ?? "unavailable";

    internal sealed record PersistedViewSettings(
        string? GroupBy,
        string? ColumnBy,
        string? SliceBy,
        bool FieldSumAvailable,
        IReadOnlyList<string> FieldSum,
        bool? TruncateTitles = null,
        bool? ShowDateFields = null);

    private async Task TrySetMenuCheckboxAsync(
        IPage page,
        string label,
        bool desired,
        string viewName,
        CancellationToken cancellationToken)
    {
        try
        {
            var menu = await OpenViewMenuAsync(page, cancellationToken).ConfigureAwait(false);
            var option = Sel.ViewOptionCheckbox(menu, label);
            if (await option.CountAsync().ConfigureAwait(false) == 0)
            {
                _warnings.Add($"view '{viewName}': roadmap display option '{label}' is not available on the target");
                await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
                return;
            }

            var current = await ReadMenuCheckboxAsync(menu, label).ConfigureAwait(false);
            var disabled = string.Equals(
                await option.First.GetAttributeAsync("aria-disabled").ConfigureAwait(false),
                "true",
                StringComparison.Ordinal);
            if (current is null)
            {
                _warnings.Add($"view '{viewName}': roadmap display option '{label}' state could not be read on the target");
            }
            else if (current != desired && disabled)
            {
                _warnings.Add($"view '{viewName}': roadmap display option '{label}' is disabled and could not be set to {FormatBoolean(desired)}");
            }
            else if (current != desired)
            {
                await option.First.ClickAsync().ConfigureAwait(false);
                await PauseAsync(cancellationToken).ConfigureAwait(false);
            }

            await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
        {
            _warnings.Add($"view '{viewName}': roadmap display option '{label}' could not be applied — {exception.Message}");
            await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
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

            if (string.Equals(expectedValue, "none", StringComparison.Ordinal)
                && ViewUiExporter.ParseMenuValue(await item.First.InnerTextAsync().ConfigureAwait(false)) is null)
            {
                await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
                return true;
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
            var directionName = string.Equals(sort.Direction, "DESC", StringComparison.Ordinal)
                ? "Descending"
                : "Ascending";
            if (HasSortDirection(await item.First.InnerTextAsync().ConfigureAwait(false), directionName))
            {
                await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
                return;
            }

            await item.First.ClickAsync().ConfigureAwait(false);
            await PauseAsync(cancellationToken).ConfigureAwait(false);

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

    internal static bool HasSortDirection(string? menuText, string directionName)
        => menuText?.Contains(directionName, StringComparison.OrdinalIgnoreCase) == true;

    internal static bool SortMenuMatches(string? menuText, SortByFieldSnapshot sort)
    {
        ArgumentNullException.ThrowIfNull(sort);
        var value = ViewUiExporter.ParseMenuValue(menuText);
        var directionName = string.Equals(sort.Direction, "DESC", StringComparison.Ordinal)
            ? "Descending"
            : "Ascending";
        var fieldValue = value?
            .Replace("Ascending", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Descending", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim(' ', ',', '(', ')', '\r', '\n');
        return value is not null
            && string.Equals(fieldValue, sort.Field, StringComparison.Ordinal)
            && HasSortDirection(menuText, directionName);
    }

    private static async Task<bool> IsSortAlreadyAppliedAsync(
        IPage page,
        SortByFieldSnapshot sort,
        CancellationToken cancellationToken)
    {
        var menu = await OpenViewMenuAsync(page, cancellationToken).ConfigureAwait(false);
        try
        {
            var item = Sel.ConfigurationMenuItem(menu, "Sort by");
            return await item.CountAsync().ConfigureAwait(false) > 0
                && SortMenuMatches(await item.First.InnerTextAsync().ConfigureAwait(false), sort);
        }
        finally
        {
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
            var overlay = Sel.OpenMenu(page);
            await overlay.WaitForAsync().ConfigureAwait(false);
            var result = await ToggleCheckboxesAsync(
                overlay,
                new HashSet<string>(values, StringComparer.Ordinal),
                cancellationToken).ConfigureAwait(false);
            if (result.Available.Count == 0)
            {
                _warnings.Add($"view '{viewName}': {label} menu contains no checkable entries");
            }

            foreach (var value in values.Where(value => !result.Available.Contains(value)))
            {
                _warnings.Add($"view '{viewName}': {label} value '{value}' is not available on the target");
            }

            foreach (var mismatch in result.DisabledMismatches)
            {
                var action = mismatch.ShouldBeChecked ? "selected" : "cleared";
                _warnings.Add($"view '{viewName}': {label} value '{mismatch.Name}' is disabled on the target and could not be {action}");
            }

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
    private static async Task<CheckboxToggleResult> ToggleCheckboxesAsync(
        ILocator overlay,
        HashSet<string> desired,
        CancellationToken cancellationToken)
    {
        var checkboxes = Sel.CheckboxOptions(overlay);

        var available = new HashSet<string>(StringComparer.Ordinal);
        var disabledMismatches = new List<DisabledCheckboxMismatch>();
        var count = await checkboxes.CountAsync().ConfigureAwait(false);
        for (var i = 0; i < count; i++)
        {
            var checkbox = checkboxes.Nth(i);
            var name = ViewUiExporter.NormalizeUiText(await checkbox.InnerTextAsync().ConfigureAwait(false));
            if (name is null)
            {
                continue;
            }

            available.Add(name);
            var isChecked = string.Equals(await checkbox.GetAttributeAsync("aria-checked").ConfigureAwait(false), "true", StringComparison.Ordinal);
            var isDisabled = string.Equals(await checkbox.GetAttributeAsync("aria-disabled").ConfigureAwait(false), "true", StringComparison.Ordinal);
            var shouldBeChecked = desired.Contains(name);
            if (DisabledCheckboxChangeRequired(shouldBeChecked, isChecked, isDisabled))
            {
                disabledMismatches.Add(new DisabledCheckboxMismatch(name, shouldBeChecked));
            }

            if (isDisabled)
            {
                continue;
            }

            if (shouldBeChecked != isChecked)
            {
                await checkbox.ClickAsync().ConfigureAwait(false);
                await PauseAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return new CheckboxToggleResult(available, disabledMismatches);
    }

    internal static bool DisabledCheckboxChangeRequired(bool shouldBeChecked, bool isChecked, bool isDisabled)
        => isDisabled && shouldBeChecked != isChecked;

    private sealed record CheckboxToggleResult(
        HashSet<string> Available,
        IReadOnlyList<DisabledCheckboxMismatch> DisabledMismatches);

    private sealed record DisabledCheckboxMismatch(string Name, bool ShouldBeChecked);

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
        var unsavedChanges = Sel.UnsavedChangesStatus(page);
        try
        {
            await unsavedChanges.WaitForAsync(new()
            {
                State = WaitForSelectorState.Visible,
                Timeout = 750,
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
        {
            return;
        }

        // D0: the "Save view" button lives inside the View menu overlay. Close any
        // child menu first so clicking View opens the parent configuration menu.
        await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
        await Sel.ViewMenuButton(page).ClickAsync().ConfigureAwait(false);
        await PauseAsync(cancellationToken).ConfigureAwait(false);

        var save = Sel.SaveViewButton(page);
        try
        {
            await save.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 1_000 }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
        {
            await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                "the View has unsaved changes but Save view is unavailable",
                exception);
        }

        await save.ClickAsync().ConfigureAwait(false);

        // D0: saving raises a confirmation alertdialog "Save display options for <view>?".
        var confirm = Sel.SaveConfirmDialog(page);
        try
        {
            await confirm.WaitForAsync(new() { Timeout = 5_000 }).ConfigureAwait(false);
            await confirm.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).First.ClickAsync().ConfigureAwait(false);
            await confirm.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 10_000 }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
        {
            // No confirmation dialog appeared; the save applied directly.
        }

        // GitHub clears the local dirty state before the save request is fully durable.
        // Give the request time to complete before navigation can cancel it, then reload
        // so the following checks and next View operate on server-persisted state.
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);
        await PauseAsync(cancellationToken).ConfigureAwait(false);
        await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
        await Sel.ViewMenuButton(page).ClickAsync().ConfigureAwait(false);
        await PauseAsync(cancellationToken).ConfigureAwait(false);
        if (await save.CountAsync().ConfigureAwait(false) > 0
            && await save.IsVisibleAsync().ConfigureAwait(false))
        {
            throw new InvalidOperationException("the View still exposes Save view after the save completed");
        }

        await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
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
