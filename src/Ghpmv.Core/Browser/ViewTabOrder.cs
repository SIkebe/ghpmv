using System.Globalization;
using Ghpmv.Core.Snapshot;
using Microsoft.Playwright;

namespace Ghpmv.Core.Browser;

internal static class ViewTabOrder
{
    public static async Task<IReadOnlyList<int>> ReadAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        cancellationToken.ThrowIfCancellationRequested();

        var tabs = Sel.SavedViewTabs(page);
        var count = await tabs.CountAsync().ConfigureAwait(false);
        var order = new List<int>(count);
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var href = await tabs.Nth(index).GetAttributeAsync("href").ConfigureAwait(false);
            order.Add(ParseViewNumber(href));
        }

        if (order.Distinct().Count() != order.Count)
        {
            throw new InvalidOperationException("The saved View tab strip contained duplicate View numbers.");
        }

        return order;
    }

    public static IReadOnlyList<ViewSnapshot> Apply(
        IReadOnlyList<ViewSnapshot> views,
        IReadOnlyList<int> orderedViewNumbers)
    {
        ArgumentNullException.ThrowIfNull(views);
        ArgumentNullException.ThrowIfNull(orderedViewNumbers);

        var expected = views.Select(view => view.Number).ToHashSet();
        if (orderedViewNumbers.Count != views.Count
            || !expected.SetEquals(orderedViewNumbers))
        {
            throw new InvalidOperationException(
                "The saved View tab strip did not contain exactly the Views returned by the API.");
        }

        var positions = orderedViewNumbers
            .Select((number, position) => (number, position))
            .ToDictionary(pair => pair.number, pair => pair.position);
        return views.Select(view => view with { TabPosition = positions[view.Number] }).ToList();
    }

    internal static int ParseViewNumber(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            throw new InvalidOperationException("A saved View tab did not expose an href.");
        }

        var path = Uri.TryCreate(href, UriKind.Absolute, out var absolute)
            && (string.Equals(absolute.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(absolute.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                ? absolute.AbsolutePath
                : href.Split('?', '#')[0];
        var marker = "/views/";
        var markerIndex = path.LastIndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            throw new InvalidOperationException($"Saved View tab href '{href}' did not contain a View number.");
        }

        var numberText = path[(markerIndex + marker.Length)..].Trim('/');
        return int.TryParse(numberText, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number > 0
                ? number
                : throw new InvalidOperationException($"Saved View tab href '{href}' contained an invalid View number.");
    }
}
