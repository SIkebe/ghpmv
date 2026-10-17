using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ghpmv.Core.Import;

public sealed record ProjectImportLog
{
    public const string FileName = "project-import-log.json";

    public PendingProjectOperation? PendingProject { get; set; }

    public string? CreatedProjectId { get; set; }

    public bool? ImportCompleted { get; set; }

    /// <summary>Null for legacy unknown state, false for a clean operation, true after any durable migration gap.</summary>
    public bool? HasUnresolvedWarnings { get; set; }

    public bool TryMarkImportCompleted(
        bool browserAutomationEnabled,
        int projectWarningCount,
        int itemWarningCount,
        int viewWarningCount,
        int workflowWarningCount)
    {
        if (CreatedProjectId is null
            || ImportCompleted is not false)
        {
            return false;
        }

        if (projectWarningCount > 0
            || itemWarningCount > 0
            || (browserAutomationEnabled && (viewWarningCount > 0 || workflowWarningCount > 0)))
        {
            HasUnresolvedWarnings = true;
            return false;
        }

        if (HasUnresolvedWarnings is not false)
        {
            return false;
        }

        ImportCompleted = true;
        return true;
    }

    public string? PendingProjectDeletionId { get; set; }

    public Dictionary<string, PendingFieldOperation> PendingFields { get; init; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> CreatedFields { get; init; } = new(StringComparer.Ordinal);

    public Dictionary<string, PendingIssueFieldOperation> PendingIssueFields { get; init; } = new(StringComparer.Ordinal);

    public Dictionary<string, PendingIssueFieldLinkOperation> PendingIssueFieldLinks { get; init; } = new(StringComparer.Ordinal);

    public Dictionary<int, PendingViewOperation> PendingViews { get; init; } = [];

    public static async Task<ProjectImportLog> LoadAsync(string directory, CancellationToken cancellationToken)
    {
        var json = await ResumeStateStore.LoadAsync(directory, FileName, cancellationToken).ConfigureAwait(false);
        return json is null ? new ProjectImportLog() : Deserialize(json);
    }

    internal static ProjectImportLog Deserialize(string json)
    {
        var log = JsonSerializer.Deserialize(json, ProjectImportLogJsonContext.Default.ProjectImportLog)
            ?? throw new JsonException($"{FileName} contained null.");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        // Older versions omitted collections added later. Explicit null still indicates malformed state.
        log = log with
        {
            PendingFields = root.TryGetProperty("pendingFields", out _) ? log.PendingFields : new(StringComparer.Ordinal),
            CreatedFields = root.TryGetProperty("createdFields", out _) ? log.CreatedFields : new(StringComparer.Ordinal),
            PendingIssueFields = root.TryGetProperty("pendingIssueFields", out _) ? log.PendingIssueFields : new(StringComparer.Ordinal),
            PendingIssueFieldLinks = root.TryGetProperty("pendingIssueFieldLinks", out _) ? log.PendingIssueFieldLinks : new(StringComparer.Ordinal),
            PendingViews = root.TryGetProperty("pendingViews", out _) ? log.PendingViews : [],
        };
        if (log.PendingFields is null || log.CreatedFields is null || log.PendingIssueFields is null
            || log.PendingIssueFieldLinks is null || log.PendingViews is null
            || log.CreatedFields.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
            || log.PendingFields.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null
                || string.IsNullOrWhiteSpace(pair.Value.OperationId) || string.IsNullOrWhiteSpace(pair.Value.ProjectId)
                || string.IsNullOrWhiteSpace(pair.Value.Name) || string.IsNullOrWhiteSpace(pair.Value.DataType)
                || !ValidIds(pair.Value.ExistingFieldIds))
            || log.PendingIssueFields.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null
                || string.IsNullOrWhiteSpace(pair.Value.OperationId) || string.IsNullOrWhiteSpace(pair.Value.ProjectId)
                || string.IsNullOrWhiteSpace(pair.Value.OwnerLogin) || string.IsNullOrWhiteSpace(pair.Value.Name)
                || string.IsNullOrWhiteSpace(pair.Value.DataType) || !ValidIds(pair.Value.ExistingIssueFieldIds))
            || log.PendingIssueFieldLinks.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null
                || string.IsNullOrWhiteSpace(pair.Value.OperationId) || string.IsNullOrWhiteSpace(pair.Value.ProjectId)
                || string.IsNullOrWhiteSpace(pair.Value.IssueFieldId) || string.IsNullOrWhiteSpace(pair.Value.Name)
                || !ValidIds(pair.Value.ExistingFieldIds))
            || log.PendingViews.Any(pair => pair.Value is null
                || string.IsNullOrWhiteSpace(pair.Value.OperationId) || string.IsNullOrWhiteSpace(pair.Value.ProjectId)
                || string.IsNullOrWhiteSpace(pair.Value.Name) || string.IsNullOrWhiteSpace(pair.Value.Layout)
                || !ValidIds(pair.Value.ExistingViewIds))
            || (log.PendingProject is { } project
                && (string.IsNullOrWhiteSpace(project.OperationId) || string.IsNullOrWhiteSpace(project.OwnerLogin)
                    || string.IsNullOrWhiteSpace(project.Title) || !ValidIds(project.ExistingProjectIds))))
        {
            throw new InvalidDataException($"{FileName} contains malformed project state and cannot be resumed safely.");
        }
        return log;
    }

    private static bool ValidIds(string[]? ids) => ids is not null && !ids.Any(string.IsNullOrWhiteSpace);

    public async Task SaveAsync(string directory, CancellationToken cancellationToken)
    {
        await ResumeStateStore.SaveAsync(directory, FileName,
            JsonSerializer.Serialize(this, ProjectImportLogJsonContext.Default.ProjectImportLog), cancellationToken).ConfigureAwait(false);
    }
}

public sealed record PendingProjectOperation
{
    public required string OperationId { get; init; }

    public required string OwnerLogin { get; init; }

    public required string Title { get; init; }

    public required string[] ExistingProjectIds { get; init; }
}

public sealed record PendingFieldOperation
{
    public required string OperationId { get; init; }

    public required string ProjectId { get; init; }

    public required string Name { get; init; }

    public required string DataType { get; init; }

    public required string[] ExistingFieldIds { get; init; }
}

public sealed record PendingIssueFieldOperation
{
    public required string OperationId { get; init; }

    public required string ProjectId { get; init; }

    public required string OwnerLogin { get; init; }

    public required string Name { get; init; }

    public required string DataType { get; init; }

    public required string[] ExistingIssueFieldIds { get; init; }
}

public sealed record PendingIssueFieldLinkOperation
{
    public required string OperationId { get; init; }

    public required string ProjectId { get; init; }

    public required string IssueFieldId { get; init; }

    public required string Name { get; init; }

    public required string[] ExistingFieldIds { get; init; }
}

public sealed record PendingViewOperation
{
    public string? ReconciledViewId { get; init; }

    public required string OperationId { get; init; }

    public required string ProjectId { get; init; }

    public required int SourceNumber { get; init; }

    public required string Name { get; init; }

    public required string Layout { get; init; }

    public required string[] ExistingViewIds { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProjectImportLog))]
public sealed partial class ProjectImportLogJsonContext : JsonSerializerContext;
