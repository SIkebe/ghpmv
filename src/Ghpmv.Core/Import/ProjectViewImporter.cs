using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Import;

/// <summary>Imports the subset of Project views exposed by the GraphQL API.</summary>
internal sealed class ProjectViewImporter
{
    private readonly GitHubGraphQLClient _client;
    private readonly ProjectImportLog _operationLog;
    private readonly Func<CancellationToken, Task> _saveOperationLogAsync;
    private readonly List<string> _warnings = [];

    public ProjectViewImporter(
        GitHubGraphQLClient client,
        ProjectImportLog operationLog,
        Func<CancellationToken, Task> saveOperationLogAsync)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(operationLog);
        ArgumentNullException.ThrowIfNull(saveOperationLogAsync);
        _client = client;
        _operationLog = operationLog;
        _saveOperationLogAsync = saveOperationLogAsync;
    }

    public IReadOnlyDictionary<string, string> RepositoryMapping { get; init; } =
        ReadOnlyDictionary<string, string>.Empty;

    public IReadOnlyDictionary<string, string> UserMapping { get; init; } =
        ReadOnlyDictionary<string, string>.Empty;

    public IReadOnlyDictionary<string, string> OrganizationMapping { get; init; } =
        ReadOnlyDictionary<string, string>.Empty;

    public bool BrowserEnrichmentPlanned { get; init; }

    public Action<string>? OnProgress { get; set; }

    public IReadOnlyList<string> Warnings => _warnings;

    public async Task<IReadOnlyDictionary<int, int>> ImportAsync(
        IReadOnlyList<ViewSnapshot> sourceViews,
        string projectId,
        IReadOnlyDictionary<string, string> fieldIds,
        ProjectImportOutcome projectOutcome,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceViews);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(fieldIds);

        if (sourceViews.Count == 0)
        {
            return ReadOnlyDictionary<int, int>.Empty;
        }

        ValidatePendingOperations(sourceViews, projectId);
        var targetViews = await FetchViewsAsync(projectId, cancellationToken).ConfigureAwait(false);
        var usedTargetIds = new HashSet<string>(StringComparer.Ordinal);
        var initiallyExistingTargetIds = targetViews.Select(view => view.Id).ToHashSet(StringComparer.Ordinal);
        var viewNumbers = new Dictionary<int, int>();
        var orderedSourceViews = sourceViews.OrderBy(view => view.Number).ToArray();

        for (var index = 0; index < orderedSourceViews.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = orderedSourceViews[index];
            var visibleFieldIds = await ResolveVisibleFieldIdsAsync(
                source,
                projectId,
                fieldIds,
                cancellationToken).ConfigureAwait(false);
            var target = await ResolveTargetViewAsync(
                source,
                projectId,
                targetViews,
                usedTargetIds,
                projectOutcome == ProjectImportOutcome.Created && index == 0,
                visibleFieldIds,
                cancellationToken).ConfigureAwait(false);

            OnProgress?.Invoke($"Applying API settings for view '{source.Name}' ({source.Layout})...");
            target = await UpdateViewAsync(source, target.Id, visibleFieldIds, cancellationToken).ConfigureAwait(false);
            usedTargetIds.Add(target.Id);
            viewNumbers[source.Number] = target.Number;

            if (!BrowserEnrichmentPlanned)
            {
                var targetWasReused = projectOutcome == ProjectImportOutcome.Updated
                    && initiallyExistingTargetIds.Contains(target.Id);
                WarnAboutBrowserOnlySettings(source, targetWasReused);
            }
        }

        return viewNumbers;
    }

    private void ValidatePendingOperations(IReadOnlyList<ViewSnapshot> sourceViews, string projectId)
    {
        var sourceByNumber = sourceViews.ToDictionary(view => view.Number);
        foreach (var (sourceNumber, pending) in _operationLog.PendingViews)
        {
            if (!sourceByNumber.TryGetValue(sourceNumber, out var source)
                || !string.Equals(pending.ProjectId, projectId, StringComparison.Ordinal)
                || !string.Equals(pending.Name, source.Name, StringComparison.Ordinal)
                || !string.Equals(pending.Layout, source.Layout, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Pending view operation '{pending.OperationId}' does not match the selected project and snapshot. Resume the original import or reconcile it manually.");
            }
        }
    }

    private async Task<TargetView> ResolveTargetViewAsync(
        ViewSnapshot source,
        string projectId,
        List<TargetView> targetViews,
        HashSet<string> usedTargetIds,
        bool mayReuseDefault,
        IReadOnlyList<string> visibleFieldIds,
        CancellationToken cancellationToken)
    {
        if (_operationLog.PendingViews.TryGetValue(source.Number, out var pending))
        {
            var reconciled = await ReconcilePendingViewAsync(source, pending, cancellationToken).ConfigureAwait(false);
            _operationLog.PendingViews.Remove(source.Number);
            await _saveOperationLogAsync(cancellationToken).ConfigureAwait(false);
            ReplaceOrAdd(targetViews, reconciled);
            return reconciled;
        }

        var namedMatch = targetViews
            .Where(view => !usedTargetIds.Contains(view.Id)
                && string.Equals(view.Name, source.Name, StringComparison.Ordinal))
            .OrderBy(view => view.Number)
            .FirstOrDefault();
        if (namedMatch is not null)
        {
            return namedMatch;
        }

        if (mayReuseDefault)
        {
            var reusable = targetViews
                .Where(view => !usedTargetIds.Contains(view.Id))
                .OrderBy(view => view.Number)
                .FirstOrDefault();
            if (reusable is not null)
            {
                return reusable;
            }
        }

        return await CreateViewAsync(
            source,
            projectId,
            targetViews,
            visibleFieldIds,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TargetView> CreateViewAsync(
        ViewSnapshot source,
        string projectId,
        List<TargetView> targetViews,
        IReadOnlyList<string> visibleFieldIds,
        CancellationToken cancellationToken)
    {
        OnProgress?.Invoke($"Creating view '{source.Name}' ({source.Layout}) through GraphQL...");
        var operationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        _operationLog.PendingViews[source.Number] = new PendingViewOperation
        {
            OperationId = operationId,
            ProjectId = projectId,
            SourceNumber = source.Number,
            Name = source.Name,
            Layout = source.Layout,
            ExistingViewIds = [.. targetViews.Select(view => view.Id)],
        };
        await _saveOperationLogAsync(cancellationToken).ConfigureAwait(false);

        JsonElement data;
        try
        {
            data = await _client.MutationAsync(
                "createProjectV2View",
                CreateViewMutation,
                new
                {
                    projectId,
                    name = source.Name,
                    layout = source.Layout,
                    configuration = new { visibleFieldIds },
                },
                MutationRetryPolicy.Create,
                target: projectId,
                clientMutationId: operationId,
                requiredResultPath: "projectV2View.id",
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (AmbiguousMutationResultException)
        {
            throw;
        }
        catch
        {
            _operationLog.PendingViews.Remove(source.Number);
            await _saveOperationLogAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var created = ParseView(data.GetProperty("createProjectV2View").GetProperty("projectV2View"));
        _operationLog.PendingViews.Remove(source.Number);
        await _saveOperationLogAsync(cancellationToken).ConfigureAwait(false);
        targetViews.Add(created);
        return created;
    }

    private async Task<TargetView> ReconcilePendingViewAsync(
        ViewSnapshot source,
        PendingViewOperation pending,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), cancellationToken).ConfigureAwait(false);
            }

            var views = await FetchViewsAsync(pending.ProjectId, cancellationToken).ConfigureAwait(false);
            var candidates = views.Where(view =>
                !pending.ExistingViewIds.Contains(view.Id, StringComparer.Ordinal)
                && string.Equals(view.Name, source.Name, StringComparison.Ordinal)
                && string.Equals(view.Layout, source.Layout, StringComparison.Ordinal)).ToArray();
            if (candidates.Length == 1)
            {
                return candidates[0];
            }

            if (candidates.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Pending view operation '{pending.OperationId}' matches multiple new views. Reconcile the target manually.");
            }
        }

        throw new InvalidOperationException(
            $"Pending view operation '{pending.OperationId}' could not be reconciled. The view may not have been created; reconcile the target manually.");
    }

    private async Task<TargetView> UpdateViewAsync(
        ViewSnapshot source,
        string viewId,
        IReadOnlyList<string> visibleFieldIds,
        CancellationToken cancellationToken)
    {
        var filter = source.Filter is null
            ? null
            : TransformFilter(source.Name, source.Filter);
        var data = await _client.MutationAsync(
            "updateProjectV2View",
            UpdateViewMutation,
            new
            {
                viewId,
                name = source.Name,
                layout = source.Layout,
                filter,
                configuration = new { visibleFieldIds },
            },
            MutationRetryPolicy.Idempotent,
            target: viewId,
            requiredResultPath: "projectV2View.id",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseView(data.GetProperty("updateProjectV2View").GetProperty("projectV2View"));
    }

    private string TransformFilter(string viewName, string filter)
    {
        var result = ProjectFilterTransformer.Transform(
            filter,
            UserMapping,
            RepositoryMapping,
            OrganizationMapping);
        foreach (var identifier in result.Unresolved)
        {
            Warn($"view '{viewName}': unmapped {identifier.Qualifier} filter value '{identifier.Value}' was left unchanged");
        }

        foreach (var identifier in result.Unsupported)
        {
            Warn($"view '{viewName}': unsupported filter qualifier '{identifier.Qualifier}' was left unchanged");
        }

        return result.Transformed;
    }

    private async Task<IReadOnlyList<string>> ResolveVisibleFieldIdsAsync(
        ViewSnapshot view,
        string projectId,
        IReadOnlyDictionary<string, string> fieldIds,
        CancellationToken cancellationToken)
    {
        var result = new List<string>(view.VisibleFields.Count);
        foreach (var name in view.VisibleFields)
        {
            if (fieldIds.TryGetValue(name, out var fieldId))
            {
                result.Add(fieldId);
                continue;
            }

            JsonElement data;
            try
            {
                data = await _client.QueryAsync(
                    FieldByNameQuery,
                    new { projectId, name },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (GitHubGraphQLException exception) when (exception.ErrorType == "NOT_FOUND")
            {
                Warn($"view '{view.Name}': visible field '{name}' was not found on the target and was omitted");
                continue;
            }

            var node = data.GetProperty("node");
            var field = node.ValueKind == JsonValueKind.Object
                && node.TryGetProperty("field", out var candidate)
                    ? candidate
                    : default;
            if (field.ValueKind == JsonValueKind.Object
                && field.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String)
            {
                result.Add(id.GetString()!);
            }
            else
            {
                Warn($"view '{view.Name}': visible field '{name}' was not found on the target and was omitted");
            }
        }

        return result;
    }

    private void WarnAboutBrowserOnlySettings(ViewSnapshot view, bool targetWasReused)
    {
        var settings = new List<string>();
        if (view.GroupByFields.Count > 0)
        {
            settings.Add("group-by/swimlanes");
        }

        var boardWithoutColumn = string.Equals(view.Layout, "BOARD_LAYOUT", StringComparison.Ordinal)
            && view.VerticalGroupByFields.Count == 0;
        if (boardWithoutColumn
            || (view.VerticalGroupByFields.Count > 0
                && (targetWasReused
                    || !(view.VerticalGroupByFields.Count == 1
                    && string.Equals(view.VerticalGroupByFields[0], "Status", StringComparison.Ordinal)))))
        {
            settings.Add("column-by");
        }

        if (view.SortByFields.Count > 0)
        {
            settings.Add("sort-by");
        }

        if (view.Ui is not null)
        {
            settings.Add("UI-only settings");
        }

        if (targetWasReused)
        {
            settings.Add("existing browser-only settings");
        }

        if (settings.Count > 0)
        {
            Warn($"view '{view.Name}': {string.Join(", ", settings)} require browser automation and were not applied");
        }
    }

    private async Task<List<TargetView>> FetchViewsAsync(string projectId, CancellationToken cancellationToken)
    {
        var result = new List<TargetView>();
        await foreach (var node in _client.QueryPaginatedAsync(
            ViewsQuery,
            new { projectId, first = 50 },
            "node.views",
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            result.Add(ParseView(node));
        }

        return result;
    }

    private static TargetView ParseView(JsonElement node) => new(
        node.GetProperty("id").GetString() ?? throw new GitHubGraphQLException("View id was null."),
        node.GetProperty("number").GetInt32(),
        node.GetProperty("name").GetString() ?? string.Empty,
        node.GetProperty("layout").GetString() ?? string.Empty);

    private static void ReplaceOrAdd(List<TargetView> views, TargetView replacement)
    {
        var index = views.FindIndex(view => string.Equals(view.Id, replacement.Id, StringComparison.Ordinal));
        if (index >= 0)
        {
            views[index] = replacement;
        }
        else
        {
            views.Add(replacement);
        }
    }

    private void Warn(string message)
    {
        _warnings.Add(message);
        OnProgress?.Invoke("warning: " + message);
    }

    private sealed record TargetView(string Id, int Number, string Name, string Layout);

    private const string ViewsQuery =
        """
        query($projectId: ID!, $first: Int!, $after: String) {
          node(id: $projectId) {
            ... on ProjectV2 {
              views(first: $first, after: $after) {
                nodes { id number name layout }
                pageInfo { hasNextPage endCursor }
              }
            }
          }
        }
        """;

    private const string FieldByNameQuery =
        """
        query($projectId: ID!, $name: String!) {
          node(id: $projectId) {
            ... on ProjectV2 {
              field(name: $name) {
                ... on ProjectV2FieldCommon { id name }
              }
            }
          }
        }
        """;

    private const string CreateViewMutation =
        """
        mutation($projectId: ID!, $name: String!, $layout: ProjectV2ViewLayout!, $configuration: ProjectV2ViewConfigurationInput!, $clientMutationId: String!) {
          createProjectV2View(input: { projectId: $projectId, name: $name, layout: $layout, configuration: $configuration, clientMutationId: $clientMutationId }) {
            projectV2View { id number name layout }
          }
        }
        """;

    private const string UpdateViewMutation =
        """
        mutation($viewId: ID!, $name: String!, $layout: ProjectV2ViewLayout!, $filter: String, $configuration: ProjectV2ViewConfigurationInput!, $clientMutationId: String!) {
          updateProjectV2View(input: { viewId: $viewId, name: $name, layout: $layout, filter: $filter, configuration: $configuration, clientMutationId: $clientMutationId }) {
            projectV2View { id number name layout }
          }
        }
        """;
}
