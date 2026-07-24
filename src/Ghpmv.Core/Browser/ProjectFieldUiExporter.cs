using System.Globalization;
using System.Text.Json;
using Ghpmv.Core.Export;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Browser;

/// <summary>
/// Reads the complete field catalog embedded by the Projects web UI. GitHub's public
/// GraphQL field connection cannot enumerate projects containing linked multi-select
/// Issue Fields, while this page data includes every field and the underlying linkage.
/// </summary>
public sealed class ProjectFieldUiExporter
{
    private readonly BrowserSession _session;

    public ProjectFieldUiExporter(BrowserSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    public Action<string>? OnProgress { get; set; }

    public async Task<ProjectFieldCatalog> ExportAsync(
        string ownerLogin,
        ProjectOwnerType ownerType,
        int projectNumber,
        int viewNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerLogin);
        OnProgress?.Invoke("Reading the complete field catalog from the Projects UI...");
        var url = BrowserProjectUrl.Build(
            _session.BaseUrl,
            ownerLogin,
            ownerType,
            projectNumber,
            string.Create(CultureInfo.InvariantCulture, $"views/{viewNumber}"));
        var page = await _session.GotoAsync(url, cancellationToken).ConfigureAwait(false);
        var data = Sel.ProjectFieldData(page);
        await data.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached }).ConfigureAwait(false);
        var json = await data.TextContentAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("The Projects UI did not provide its field catalog.");
        }

        var catalog = ParseCatalog(json);
        OnProgress?.Invoke(string.Create(
            CultureInfo.InvariantCulture,
            $"Read {catalog.Fields.Count} fields from the Projects UI."));
        return catalog;
    }

    public static ProjectFieldCatalog ParseCatalog(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The Projects UI field catalog was not an array.");
        }

        var entries = new List<(int Position, FieldSnapshot Field, bool IsIssueField)>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var name = RequiredString(element, "name");
            if (!names.Add(name))
            {
                throw new InvalidOperationException($"The Projects UI field catalog contained duplicate field name '{name}'.");
            }

            var dataType = MapDataType(RequiredString(element, "dataType"));
            var settings = element.TryGetProperty("settings", out var settingsElement)
                && settingsElement.ValueKind == JsonValueKind.Object
                    ? settingsElement
                    : (JsonElement?)null;
            var field = new FieldSnapshot
            {
                Name = name,
                DataType = dataType,
                Options = dataType is "SINGLE_SELECT" or "MULTI_SELECT"
                    ? ParseOptions(settings)
                    : null,
                IterationConfiguration = dataType == "ITERATION"
                    ? ParseIterationConfiguration(settings)
                    : null,
            };
            var isIssueField = element.TryGetProperty("issueFieldId", out var issueFieldId)
                && issueFieldId.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
            entries.Add((element.GetProperty("position").GetInt32(), field, isIssueField));
        }

        if (entries.Count == 0)
        {
            throw new InvalidOperationException("The Projects UI field catalog was empty.");
        }

        var ordered = entries.OrderBy(entry => entry.Position).ToArray();
        return new ProjectFieldCatalog
        {
            Fields = [.. ordered.Select(entry => entry.Field)],
            IssueFieldNames = ordered
                .Where(entry => entry.IsIssueField)
                .Select(entry => entry.Field.Name)
                .ToHashSet(StringComparer.Ordinal),
        };
    }

    private static IReadOnlyList<SingleSelectOptionSnapshot> ParseOptions(JsonElement? settings)
    {
        if (settings is not { } value
            || !value.TryGetProperty("options", out var options)
            || options.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("A select field in the Projects UI catalog did not include its options.");
        }

        return
        [
            .. options.EnumerateArray().Select(option => new SingleSelectOptionSnapshot
            {
                Id = RequiredScalarString(option, "id"),
                Name = RequiredString(option, "name"),
                Color = RequiredString(option, "color"),
                Description = OptionalString(option, "description"),
            }),
        ];
    }

    private static IterationConfigurationSnapshot ParseIterationConfiguration(JsonElement? settings)
    {
        if (settings is not { } value
            || !value.TryGetProperty("configuration", out var configuration)
            || configuration.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("An iteration field in the Projects UI catalog did not include its configuration.");
        }

        return new IterationConfigurationSnapshot
        {
            Duration = configuration.GetProperty("duration").GetInt32(),
            StartDay = configuration.GetProperty("startDay").GetInt32(),
            Iterations = ParseIterations(configuration, "iterations"),
            CompletedIterations = ParseIterations(configuration, "completedIterations"),
        };
    }

    private static IReadOnlyList<IterationSnapshot> ParseIterations(JsonElement configuration, string propertyName)
    {
        if (!configuration.TryGetProperty(propertyName, out var iterations)
            || iterations.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"An iteration field in the Projects UI catalog did not include '{propertyName}'.");
        }

        return
        [
            .. iterations.EnumerateArray().Select(iteration => new IterationSnapshot
            {
                Id = RequiredScalarString(iteration, "id"),
                Title = RequiredString(iteration, "title"),
                StartDate = RequiredString(iteration, "startDate"),
                Duration = iteration.GetProperty("duration").GetInt32(),
            }),
        ];
    }

    private static string MapDataType(string dataType) => dataType switch
    {
        "assignees" => "ASSIGNEES",
        "closed" => "CLOSED",
        "created" => "CREATED",
        "date" => "DATE",
        "issueType" => "ISSUE_TYPE",
        "iteration" => "ITERATION",
        "labels" => "LABELS",
        "linkedPullRequests" => "LINKED_PULL_REQUESTS",
        "milestone" => "MILESTONE",
        "multiSelect" => "MULTI_SELECT",
        "number" => "NUMBER",
        "parentIssue" => "PARENT_ISSUE",
        "repository" => "REPOSITORY",
        "reviewers" => "REVIEWERS",
        "singleSelect" => "SINGLE_SELECT",
        "subIssuesProgress" => "SUB_ISSUES_PROGRESS",
        "text" => "TEXT",
        "title" => "TITLE",
        "trackedBy" => "TRACKED_BY",
        "tracks" => "TRACKS",
        "updated" => "UPDATED",
        _ => throw new InvalidOperationException($"The Projects UI returned unsupported field data type '{dataType}'."),
    };

    private static string RequiredString(JsonElement element, string propertyName)
        => element.GetProperty(propertyName).GetString()
            ?? throw new InvalidOperationException($"The Projects UI field catalog property '{propertyName}' was null.");

    private static string RequiredScalarString(JsonElement element, string propertyName)
    {
        var value = element.GetProperty(propertyName);
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()
                ?? throw new InvalidOperationException($"The Projects UI field catalog property '{propertyName}' was null."),
            JsonValueKind.Number => value.GetRawText(),
            _ => throw new InvalidOperationException($"The Projects UI field catalog property '{propertyName}' was not a scalar ID."),
        };
    }

    private static string? OptionalString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
