using System.Globalization;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Snapshot;
using Microsoft.Playwright;

namespace Ghpmv.Core.Browser;

/// <summary>Reads browser-only defaults for Text, Number, and Single-select Project fields.</summary>
public sealed class FieldDefaultUiExporter
{
    private readonly BrowserSession _session;
    private readonly List<string> _warnings = [];

    public FieldDefaultUiExporter(BrowserSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    public Action<string>? OnProgress { get; set; }

    public IReadOnlyList<string> Warnings => _warnings;

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
            _warnings.Add($"field default settings could not be opened — {exception.Message}");
            return snapshot;
        }

        var fields = new List<FieldSnapshot>(snapshot.Fields.Count);
        foreach (var field in snapshot.Fields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Supports(field))
            {
                fields.Add(field);
                continue;
            }

            OnProgress?.Invoke($"Reading default value for field '{field.Name}' ({field.DataType})...");
            try
            {
                await OpenFieldSettingsAsync(
                    page,
                    _session,
                    ownerLogin,
                    ownerType,
                    projectNumber,
                    field.Name,
                    cancellationToken).ConfigureAwait(false);
                var defaultValue = await ReadDefaultValueAsync(page, field).ConfigureAwait(false);
                fields.Add(field with { DefaultValue = defaultValue });
            }
            catch (Exception exception) when (exception is PlaywrightException or TimeoutException or InvalidOperationException or FormatException)
            {
                _warnings.Add($"field '{field.Name}': default value could not be read — {exception.Message}");
                fields.Add(field with { DefaultValue = null });
            }
        }

        return snapshot with { Fields = fields };
    }

    internal static bool Supports(FieldSnapshot field)
        => field.IssueField is null
            && !string.Equals(field.Name, "Status", StringComparison.Ordinal)
            && field.DataType is "TEXT" or "NUMBER" or "SINGLE_SELECT";

    internal static async Task OpenFieldSettingsAsync(
        IPage page,
        BrowserSession session,
        string ownerLogin,
        ProjectOwnerType ownerType,
        int projectNumber,
        string fieldName,
        CancellationToken cancellationToken)
    {
        var url = BrowserProjectUrl.Build(
            session.BaseUrl,
            ownerLogin,
            ownerType,
            projectNumber,
            "settings");
        await session.GotoAsync(url, cancellationToken).ConfigureAwait(false);
        var entry = Sel.FieldSettingsEntry(page, fieldName);
        await entry.WaitForAsync().ConfigureAwait(false);
        await entry.ClickAsync().ConfigureAwait(false);
        await Sel.FieldDefaultControl(page).WaitForAsync().ConfigureAwait(false);
    }

    internal static async Task<FieldDefaultValueSnapshot> ReadDefaultValueAsync(
        IPage page,
        FieldSnapshot field)
    {
        ArgumentNullException.ThrowIfNull(field);
        var control = Sel.FieldDefaultControl(page);
        var tagName = await control.EvaluateAsync<string>("element => element.tagName.toLowerCase()")
            .ConfigureAwait(false);
        var raw = tagName is "input" or "textarea"
            ? await control.InputValueAsync().ConfigureAwait(false)
            : await control.InnerTextAsync().ConfigureAwait(false);

        return field.DataType switch
        {
            "TEXT" => new FieldDefaultValueSnapshot
            {
                Text = string.IsNullOrEmpty(raw) ? null : raw,
            },
            "NUMBER" => new FieldDefaultValueSnapshot { Number = ParseNumber(raw) },
            "SINGLE_SELECT" => new FieldDefaultValueSnapshot
            {
                SingleSelectOptionName = NormalizeSingleSelectValue(
                    raw,
                    field.Options?.Select(option => option.Name).ToHashSet(StringComparer.Ordinal)
                        ?? []),
            },
            _ => throw new InvalidOperationException(
                $"Field type '{field.DataType}' does not support a browser default value."),
        };
    }

    internal static double? ParseNumber(string? value)
    {
        value = EmptyToNull(value);
        return value is null
            ? null
            : double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    internal static string? NormalizeSingleSelectValue(
        string? value,
        IReadOnlySet<string> optionNames)
    {
        ArgumentNullException.ThrowIfNull(optionNames);
        value = EmptyToNull(value);
        if (value is null)
        {
            return null;
        }
        if (optionNames.Contains(value))
        {
            return value;
        }

        const string labelPrefix = "Default value:";
        if (value.StartsWith(labelPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var labelledValue = EmptyToNull(value[labelPrefix.Length..]);
            if (labelledValue is not null && optionNames.Contains(labelledValue))
            {
                return labelledValue;
            }

            value = labelledValue;
        }

        return value is null
            || value.Equals("Select a default value", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Choose a default value", StringComparison.OrdinalIgnoreCase)
            || value.Equals("No default value", StringComparison.OrdinalIgnoreCase)
            || value.Equals("None", StringComparison.OrdinalIgnoreCase)
                ? null
                : value;
    }

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
