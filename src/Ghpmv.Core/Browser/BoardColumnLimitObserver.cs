using System.Globalization;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Snapshot;
using Microsoft.Playwright;

namespace Ghpmv.Core.Browser;

/// <summary>Validates configured, unlimited, and exceeded limits in the standard Board fixture.</summary>
public sealed class BoardColumnLimitObserver
{
    private readonly BrowserSession _session;

    public BoardColumnLimitObserver(BrowserSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    public Action<string>? OnProgress { get; set; }

    public async Task ValidateFixtureAsync(
        ProjectSnapshot expected,
        string ownerLogin,
        ProjectOwnerType ownerType,
        int projectNumber,
        IReadOnlyDictionary<string, int> viewNumbers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerLogin);
        ArgumentNullException.ThrowIfNull(viewNumbers);

        var boards = expected.Views
            .Where(view => view.Ui?.BoardColumnLimits is not null)
            .ToArray();
        var page = await _session.GetPageAsync(cancellationToken).ConfigureAwait(false);
        foreach (var board in boards)
        {
            if (!viewNumbers.TryGetValue(board.Name, out var viewNumber))
            {
                throw new InvalidOperationException($"Expected exactly one target View named '{board.Name}'.");
            }

            var url = BrowserProjectUrl.Build(
                _session.BaseUrl,
                ownerLogin,
                ownerType,
                projectNumber,
                string.Create(CultureInfo.InvariantCulture, $"views/{viewNumber}"));
            await _session.GotoAsync(url, cancellationToken).ConfigureAwait(false);
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);

            var currentVisibility = await BoardColumnVisibilityUi.ReadAsync(
                page,
                board,
                expected.Fields,
                cancellationToken).ConfigureAwait(false);
            var actual = await BoardColumnLimitUi.ReadCompleteAsync(
                page,
                board,
                expected.Fields,
                currentVisibility,
                cancellationToken).ConfigureAwait(false);
            ValidateLimits(board, actual);
            var columnField = expected.Fields.Single(field =>
                string.Equals(field.Name, AssertSingleColumnField(board), StringComparison.Ordinal));
            var configuredNames = board.Ui!.BoardColumnLimits!
                .Select(limit => limit.SingleSelectOptionName ?? limit.IterationTitle!)
                .ToHashSet(StringComparer.Ordinal);
            var unlimitedNames = GetValueNames(columnField)
                .Where(name => !configuredNames.Contains(name))
                .ToArray();
            if (unlimitedNames.Length == 0)
            {
                throw new InvalidOperationException(
                    $"view '{board.Name}': fixture does not define an unlimited Board column");
            }

            foreach (var limit in board.Ui.BoardColumnLimits!.Where(limit => limit.Limit == 1))
            {
                var columnName = limit.SingleSelectOptionName ?? limit.IterationTitle!;
                var count = await Sel.BoardColumnCards(page, columnName).CountAsync().ConfigureAwait(false);
                if (count <= limit.Limit)
                {
                    throw new InvalidOperationException(
                        $"view '{board.Name}': column '{columnName}' did not render more cards ({count}) than its limit ({limit.Limit})");
                }
            }

            OnProgress?.Invoke(
                $"Rendered Board limits verified for view '{board.Name}': configured={actual.Count}");
        }
    }

    internal static void ValidateLimits(
        ViewSnapshot expected,
        IReadOnlyList<BoardColumnLimitSnapshot> actual)
    {
        var desired = expected.Ui?.BoardColumnLimits
            ?? throw new InvalidOperationException($"view '{expected.Name}': expected Board limit state is unavailable");
        if (desired.Count != actual.Count
            || desired.Any(limit => !actual.Any(candidate =>
                SameColumn(limit, candidate) && limit.Limit == candidate.Limit)))
        {
            throw new InvalidOperationException($"view '{expected.Name}': rendered Board column limits do not match the fixture");
        }
    }

    private static bool SameColumn(BoardColumnLimitSnapshot first, BoardColumnLimitSnapshot second)
        => string.Equals(first.FieldName, second.FieldName, StringComparison.Ordinal)
            && string.Equals(first.SingleSelectOptionName, second.SingleSelectOptionName, StringComparison.Ordinal)
            && string.Equals(first.IterationTitle, second.IterationTitle, StringComparison.Ordinal);

    private static string AssertSingleColumnField(ViewSnapshot view)
        => view.VerticalGroupByFields.Count == 1
            ? view.VerticalGroupByFields[0]
            : throw new InvalidOperationException(
                $"view '{view.Name}': expected exactly one Board column field");

    private static IEnumerable<string> GetValueNames(FieldSnapshot field)
        => field.DataType switch
        {
            "SINGLE_SELECT" => field.Options?.Select(option => option.Name) ?? [],
            "ITERATION" when field.IterationConfiguration is { } configuration =>
                configuration.Iterations.Concat(configuration.CompletedIterations)
                    .Select(iteration => iteration.Title),
            _ => throw new InvalidOperationException(
                $"field '{field.Name}' has unsupported Board column type '{field.DataType}'"),
        };

}
