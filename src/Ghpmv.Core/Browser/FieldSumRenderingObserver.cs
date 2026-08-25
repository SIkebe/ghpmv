using System.Globalization;
using System.Text.RegularExpressions;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Snapshot;
using Microsoft.Playwright;

namespace Ghpmv.Core.Browser;

/// <summary>Validates the visible grouped-header rendering for the standard Field sum fixture.</summary>
public sealed partial class FieldSumRenderingObserver
{
    private readonly BrowserSession _session;

    public FieldSumRenderingObserver(BrowserSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    public Action<string>? OnProgress { get; set; }

    public async Task ValidateStandardFixtureAsync(
        string ownerLogin,
        ProjectOwnerType ownerType,
        int projectNumber,
        IReadOnlyDictionary<string, int> viewNumbers,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerLogin);
        ArgumentNullException.ThrowIfNull(viewNumbers);

        var expectedViews = FixtureUiSnapshotFactory.Create().Views
            .Where(view => view.Name is "View 1" or "Fixture Roadmap")
            .ToArray();
        var page = await _session.GetPageAsync(cancellationToken).ConfigureAwait(false);
        foreach (var view in expectedViews)
        {
            if (!viewNumbers.TryGetValue(view.Name, out var viewNumber))
            {
                throw new InvalidOperationException($"Expected exactly one target View named '{view.Name}'.");
            }

            var url = BrowserProjectUrl.Build(
                _session.BaseUrl,
                ownerLogin,
                ownerType,
                projectNumber,
                string.Create(CultureInfo.InvariantCulture, $"views/{viewNumber}"));
            await _session.GotoAsync(url, cancellationToken).ConfigureAwait(false);
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);

            var headers = Sel.GroupHeaderContents(page);
            await headers.First.WaitForAsync(new()
            {
                State = WaitForSelectorState.Visible,
                Timeout = 15_000,
            }).ConfigureAwait(false);
            var headerTexts = await ReadNormalizedTextsAsync(headers).ConfigureAwait(false);
            var labelTexts = await ReadNormalizedTextsAsync(Sel.GroupHeaderAggregateLabels(page)).ConfigureAwait(false);
            ValidateObservation(view, headerTexts, labelTexts);
            OnProgress?.Invoke(
                $"Rendered Field sums verified for view '{view.Name}': headers=[{string.Join(" | ", headerTexts)}]");
        }
    }

    internal static void ValidateObservation(
        ViewSnapshot view,
        IReadOnlyList<string> headerTexts,
        IReadOnlyList<string> labelTexts)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(headerTexts);
        ArgumentNullException.ThrowIfNull(labelTexts);

        if (headerTexts.Count == 0)
        {
            throw new InvalidOperationException($"view '{view.Name}': no visible group headers were rendered");
        }

        var fieldSums = view.Ui?.FieldSum
            ?? throw new InvalidOperationException($"view '{view.Name}': expected Field sum state is unavailable");
        if (fieldSums.Contains("Count", StringComparer.Ordinal)
            && !headerTexts.Any(text => CountRendering().IsMatch(text)))
        {
            throw new InvalidOperationException($"view '{view.Name}': visible Count aggregate was not rendered");
        }

        foreach (var field in fieldSums.Where(field => !string.Equals(field, "Count", StringComparison.Ordinal)))
        {
            var prefix = field + ":";
            if (!labelTexts.Any(label =>
                    label.StartsWith(prefix, StringComparison.Ordinal)
                    && NumericRendering().IsMatch(label[prefix.Length..])))
            {
                throw new InvalidOperationException(
                    $"view '{view.Name}': visible aggregate for '{field}' was not rendered with a numeric value");
            }
        }
    }

    private static async Task<IReadOnlyList<string>> ReadNormalizedTextsAsync(ILocator locator)
    {
        var result = new List<string>();
        var count = await locator.CountAsync().ConfigureAwait(false);
        for (var index = 0; index < count; index++)
        {
            if (ViewUiExporter.NormalizeUiText(await locator.Nth(index).InnerTextAsync().ConfigureAwait(false)) is { } text)
            {
                result.Add(text);
            }
        }

        return result;
    }

    [GeneratedRegex(@"\b\d+\s+\(\d+\)", RegexOptions.CultureInvariant)]
    private static partial Regex CountRendering();

    [GeneratedRegex(@"^\s*[-+]?(?:\d+(?:[.,]\d+)?|[.,]\d+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex NumericRendering();
}
