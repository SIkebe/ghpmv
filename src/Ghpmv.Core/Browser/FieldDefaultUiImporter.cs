using System.Globalization;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Snapshot;
using Microsoft.Playwright;

namespace Ghpmv.Core.Browser;

/// <summary>Applies browser-only defaults after Project fields, options, and existing items exist.</summary>
public sealed class FieldDefaultUiImporter
{
    private readonly BrowserSession _session;
    private readonly List<string> _warnings = [];

    public FieldDefaultUiImporter(BrowserSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    public Action<string>? OnProgress { get; set; }

    public IReadOnlyList<string> Warnings => _warnings;

    public int AppliedCount { get; private set; }

    public static bool ShouldDefer(ProjectSnapshot snapshot, int skippedItemCount)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return skippedItemCount > 0
            && snapshot.Fields.Any(field => field.DefaultValue is not null);
    }

    public async Task ImportAsync(
        ProjectSnapshot snapshot,
        string ownerLogin,
        ProjectOwnerType ownerType,
        int projectNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerLogin);

        var page = await _session.GetPageAsync(cancellationToken).ConfigureAwait(false);
        foreach (var field in snapshot.Fields.Where(field => field.DefaultValue is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var validation = Validate(field);
            if (validation is not null)
            {
                _warnings.Add(validation);
                continue;
            }

            OnProgress?.Invoke($"Applying default value for field '{field.Name}' ({field.DataType})...");
            try
            {
                await ApplyAsync(
                    page,
                    field,
                    ownerLogin,
                    ownerType,
                    projectNumber,
                    cancellationToken).ConfigureAwait(false);
                AppliedCount++;
            }
            catch (Exception exception) when (exception is PlaywrightException or TimeoutException or InvalidOperationException or FormatException)
            {
                _warnings.Add($"field '{field.Name}': default value could not be applied — {exception.Message}");
            }
        }
    }

    internal static string? Validate(FieldSnapshot field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.DefaultValue is null)
        {
            return null;
        }
        if (!FieldDefaultUiExporter.Supports(field))
        {
            return $"field '{field.Name}': {field.DataType} does not support a browser default value";
        }

        var populatedMembers = (field.DefaultValue.Text is null ? 0 : 1)
            + (field.DefaultValue.Number is null ? 0 : 1)
            + (field.DefaultValue.SingleSelectOptionName is null ? 0 : 1);
        if (populatedMembers > 1)
        {
            return $"field '{field.Name}': default value contains members for multiple field types";
        }

        if (field.DataType == "SINGLE_SELECT"
            && field.DefaultValue.SingleSelectOptionName is { } optionName
            && !(field.Options ?? []).Any(option =>
                string.Equals(option.Name, optionName, StringComparison.Ordinal)))
        {
            return $"field '{field.Name}': default option '{optionName}' does not exist in the snapshot";
        }

        var wrongMember = field.DataType switch
        {
            "TEXT" => field.DefaultValue.Number is not null
                || field.DefaultValue.SingleSelectOptionName is not null,
            "NUMBER" => field.DefaultValue.Text is not null
                || field.DefaultValue.SingleSelectOptionName is not null,
            "SINGLE_SELECT" => field.DefaultValue.Text is not null
                || field.DefaultValue.Number is not null,
            _ => true,
        };
        return wrongMember
            ? $"field '{field.Name}': default value does not match field type {field.DataType}"
            : null;
    }

    internal static bool ValuesEqual(
        string dataType,
        FieldDefaultValueSnapshot expected,
        FieldDefaultValueSnapshot actual)
        => dataType switch
        {
            "TEXT" => string.Equals(expected.Text, actual.Text, StringComparison.Ordinal),
            "NUMBER" => expected.Number == actual.Number,
            "SINGLE_SELECT" => string.Equals(
                expected.SingleSelectOptionName,
                actual.SingleSelectOptionName,
                StringComparison.Ordinal),
            _ => false,
        };

    private async Task ApplyAsync(
        IPage page,
        FieldSnapshot field,
        string ownerLogin,
        ProjectOwnerType ownerType,
        int projectNumber,
        CancellationToken cancellationToken)
    {
        await FieldDefaultUiExporter.OpenFieldSettingsAsync(
            page,
            _session,
            ownerLogin,
            ownerType,
            projectNumber,
            field.Name,
            cancellationToken).ConfigureAwait(false);

        var current = await FieldDefaultUiExporter.ReadDefaultValueAsync(page, field)
            .ConfigureAwait(false);
        if (ValuesEqual(field.DataType, field.DefaultValue!, current))
        {
            return;
        }

        var control = Sel.FieldDefaultControl(page);
        switch (field.DataType)
        {
            case "TEXT":
                await control.FillAsync(field.DefaultValue!.Text ?? string.Empty).ConfigureAwait(false);
                break;
            case "NUMBER":
                await control.FillAsync(field.DefaultValue!.Number?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty)
                    .ConfigureAwait(false);
                break;
            case "SINGLE_SELECT":
                await ApplySingleSelectAsync(page, control, field.DefaultValue!.SingleSelectOptionName)
                    .ConfigureAwait(false);
                break;
        }

        var save = Sel.SaveFieldSettingsButton(page);
        await save.WaitForAsync().ConfigureAwait(false);
        await save.ClickAsync().ConfigureAwait(false);
        await Sel.FieldDefaultControl(page).WaitForAsync(new()
        {
            State = WaitForSelectorState.Hidden,
            Timeout = 10_000,
        }).ConfigureAwait(false);

        await FieldDefaultUiExporter.OpenFieldSettingsAsync(
            page,
            _session,
            ownerLogin,
            ownerType,
            projectNumber,
            field.Name,
            cancellationToken).ConfigureAwait(false);
        var actual = await FieldDefaultUiExporter.ReadDefaultValueAsync(page, field)
            .ConfigureAwait(false);
        if (!ValuesEqual(field.DataType, field.DefaultValue!, actual))
        {
            throw new InvalidOperationException(
                $"saved value did not persist (expected {Display(field)}, actual {Display(field with { DefaultValue = actual })})");
        }
    }

    private static async Task ApplySingleSelectAsync(
        IPage page,
        ILocator control,
        string? optionName)
    {
        await control.ClickAsync().ConfigureAwait(false);
        if (optionName is not null)
        {
            var option = Sel.FieldDefaultOption(page, optionName);
            await option.WaitForAsync().ConfigureAwait(false);
            await option.ClickAsync().ConfigureAwait(false);
            return;
        }

        var clear = Sel.ClearFieldDefaultButton(page);
        if (await clear.CountAsync().ConfigureAwait(false) > 0)
        {
            await clear.ClickAsync().ConfigureAwait(false);
            return;
        }

        var tagName = await control.EvaluateAsync<string>("element => element.tagName.toLowerCase()")
            .ConfigureAwait(false);
        if (tagName == "input")
        {
            await control.PressAsync("Control+A").ConfigureAwait(false);
            await control.PressAsync("Backspace").ConfigureAwait(false);
            await control.PressAsync("Escape").ConfigureAwait(false);
            return;
        }

        throw new InvalidOperationException("the single-select default picker exposed no clear action");
    }

    private static string Display(FieldSnapshot field)
        => field.DataType switch
        {
            "TEXT" => field.DefaultValue?.Text is { } text ? $"'{text}'" : "<cleared>",
            "NUMBER" => field.DefaultValue?.Number is { } number
                ? number.ToString("R", CultureInfo.InvariantCulture)
                : "<cleared>",
            "SINGLE_SELECT" => field.DefaultValue?.SingleSelectOptionName is { } option
                ? $"'{option}'"
                : "<cleared>",
            _ => "<unsupported>",
        };
}
