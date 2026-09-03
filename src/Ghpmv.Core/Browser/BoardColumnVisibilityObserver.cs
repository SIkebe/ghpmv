using System.Globalization;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Snapshot;
using Microsoft.Playwright;

namespace Ghpmv.Core.Browser;

/// <summary>Validates rendered Single-select and Iteration Board column visibility.</summary>
public sealed class BoardColumnVisibilityObserver
{
    private readonly BrowserSession _session;

    public BoardColumnVisibilityObserver(BrowserSession session)
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
        var page = await _session.GetPageAsync(cancellationToken).ConfigureAwait(false);
        foreach (var board in expected.Views.Where(view => view.Ui?.VisibleColumns is not null))
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
            var actual = await BoardColumnVisibilityUi.ReadAsync(
                page,
                board,
                expected.Fields,
                cancellationToken).ConfigureAwait(false);
            if (!BoardColumnVisibilityUi.SetEquals(board.Ui!.VisibleColumns, actual))
            {
                throw new InvalidOperationException(
                    $"view '{board.Name}': rendered Board column visibility does not match the fixture");
            }

            OnProgress?.Invoke(
                $"Rendered Board column visibility verified for view '{board.Name}': visible={actual.Count}");
        }
    }
}
