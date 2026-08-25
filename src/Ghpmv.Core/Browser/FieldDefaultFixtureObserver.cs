using System.Globalization;
using Ghpmv.Core.Export;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Snapshot;
using Microsoft.Playwright;

namespace Ghpmv.Core.Browser;

/// <summary>Functionally verifies fixture defaults by creating a target draft.</summary>
public sealed class FieldDefaultFixtureObserver
{
    private readonly GitHubGraphQLClient _client;
    private readonly BrowserSession? _session;

    public FieldDefaultFixtureObserver(
        GitHubGraphQLClient client,
        BrowserSession? session = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _session = session;
    }

    public Action<string>? OnProgress { get; set; }

    public Task<FieldDefaultFixtureCheckResult> ValidateStandardFixtureAsync(
        string organization,
        int projectNumber,
        CancellationToken cancellationToken = default)
        => ValidateStandardFixtureAsync(
            organization,
            projectNumber,
            cleanupDraft: true,
            cancellationToken);

    public async Task<FieldDefaultFixtureCheckResult> ValidateStandardFixtureAsync(
        string organization,
        int projectNumber,
        bool cleanupDraft,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organization);
        var expected = FixtureUiSnapshotFactory.Create();
        var title = $"ghpmv-default-check-{Guid.NewGuid():N}";
        OnProgress?.Invoke($"Creating field-default check draft '{title}'...");
        var projectId = await ResolveProjectIdAsync(organization, projectNumber, cancellationToken)
            .ConfigureAwait(false);
        string? itemId = null;
        IReadOnlyList<string>? reconciledIds = null;
        try
        {
            try
            {
                await CreateDraftThroughUiAsync(
                    organization,
                    projectNumber,
                    title,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
            {
                reconciledIds = await ReconcileDraftInventoryAfterSideEffectAsync(
                    projectId,
                    title).ConfigureAwait(false);
                var ids = reconciledIds.Count == 0
                    ? "(none found)"
                    : string.Join(",", reconciledIds);
                OnProgress?.Invoke(
                    $"Field-default check draft creation was ambiguous: ids={ids} title='{title}' cleanup=pending");
                throw new InvalidOperationException(
                    $"Field-default check draft UI creation was ambiguous; {FormatDraftInventory(title, reconciledIds)}.",
                    exception);
            }

            reconciledIds = await ReconcileDraftInventoryAfterSideEffectAsync(
                projectId,
                title).ConfigureAwait(false);
            if (reconciledIds.Count == 1)
            {
                itemId = reconciledIds[0];
            }
            else if (reconciledIds.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Field-default check draft '{title}' matched multiple target items; {FormatDraftInventory(title, reconciledIds)}.");
            }
            else
            {
                throw new TimeoutException(
                    $"The field-default check draft '{title}' was not visible through GraphQL within 30 seconds.");
            }

            OnProgress?.Invoke($"Field-default check draft created: id={itemId} title='{title}'");

            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            InvalidOperationException? lastDefaultMismatch = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = await new ProjectExporter(_client)
                    .ExportAsync(organization, projectNumber, cancellationToken)
                    .ConfigureAwait(false);
                var draft = snapshot.Items.SingleOrDefault(item =>
                    string.Equals(item.Draft?.Title, title, StringComparison.Ordinal));
                if (draft is not null)
                {
                    try
                    {
                        ValidateDraftDefaults(expected.Fields, draft);
                        OnProgress?.Invoke(
                            $"Fixture field defaults verified on new draft '{title}': fields={expected.Fields.Count(field => field.DefaultValue is not null)}");
                        return new FieldDefaultFixtureCheckResult
                        {
                            ItemId = itemId,
                            Title = title,
                        };
                    }
                    catch (InvalidOperationException exception)
                    {
                        lastDefaultMismatch = exception;
                    }
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    if (lastDefaultMismatch is not null)
                    {
                        throw new InvalidOperationException(
                            $"The field-default check draft '{title}' did not receive all expected defaults within 30 seconds.",
                            lastDefaultMismatch);
                    }

                    throw new TimeoutException(
                        $"The field-default check draft '{title}' was not visible within 30 seconds.");
                }

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (cleanupDraft)
            {
                var cleanupIds = itemId is not null
                    ? [itemId]
                    : reconciledIds ?? await WaitForMatchingDraftItemIdsAsync(
                        projectId,
                        title,
                        CancellationToken.None).ConfigureAwait(false);
                foreach (var cleanupId in cleanupIds)
                {
                    await DeleteAndConfirmDraftAsync(projectId, cleanupId, title).ConfigureAwait(false);
                }

                var remaining = await FindMatchingDraftItemIdsAsync(
                    projectId,
                    title,
                    CancellationToken.None).ConfigureAwait(false);
                if (remaining.Count > 0)
                {
                    ThrowCleanupFailure(title);
                }
            }
        }
    }

    public async Task DeleteDraftAsync(
        string organization,
        int projectNumber,
        string itemId,
        string title,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organization);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var projectId = await ResolveProjectIdAsync(organization, projectNumber, cancellationToken)
            .ConfigureAwait(false);
        var matchingIds = await FindMatchingDraftItemIdsAsync(projectId, title, cancellationToken)
            .ConfigureAwait(false);
        if (!matchingIds.Contains(itemId, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Field-default check draft identity mismatch: item '{itemId}' does not have title '{title}'.");
        }

        await DeleteAndConfirmDraftAsync(projectId, itemId, title).ConfigureAwait(false);
        OnProgress?.Invoke($"Field-default check draft deleted: id={itemId} title='{title}'");
    }

    internal static void ValidateDraftDefaults(
        IReadOnlyList<FieldSnapshot> fields,
        ItemSnapshot draft)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Draft is null)
        {
            throw new InvalidOperationException("The field-default check item is not a draft issue.");
        }

        var values = draft.FieldValues.ToDictionary(value => value.FieldName, StringComparer.Ordinal);
        foreach (var field in fields.Where(field => field.DefaultValue is not null))
        {
            values.TryGetValue(field.Name, out var actual);
            var matches = field.DataType switch
            {
                "TEXT" => string.Equals(
                    field.DefaultValue!.Text,
                    actual?.Text,
                    StringComparison.Ordinal),
                "NUMBER" => field.DefaultValue!.Number == actual?.Number,
                "SINGLE_SELECT" => string.Equals(
                    field.DefaultValue!.SingleSelectOptionName,
                    actual?.SingleSelectOptionName,
                    StringComparison.Ordinal),
                _ => true,
            };
            if (!matches)
            {
                throw new InvalidOperationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"new draft field '{field.Name}' did not receive its expected default"));
            }
        }
    }

    private async Task<string> ResolveProjectIdAsync(
        string organization,
        int projectNumber,
        CancellationToken cancellationToken)
    {
        var data = await _client.QueryAsync(
            """
            query($login: String!, $number: Int!) {
              organization(login: $login) {
                projectV2(number: $number) { id }
              }
            }
            """,
            new { login = organization, number = projectNumber },
            cancellationToken).ConfigureAwait(false);
        return data.GetProperty("organization").GetProperty("projectV2").GetProperty("id").GetString()
            ?? throw new GitHubGraphQLException(
                $"Fixture Project '{organization}/projects/{projectNumber}' returned no id.");
    }

    private async Task CreateDraftThroughUiAsync(
        string organization,
        int projectNumber,
        string title,
        CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            throw new InvalidOperationException(
                "A browser session is required for the field-default functional check.");
        }

        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"{_session.BaseUrl.TrimEnd('/')}/orgs/{organization}/projects/{projectNumber}");
        var page = await _session.GotoAsync(url, cancellationToken).ConfigureAwait(false);
        var input = Sel.ProjectItemEntry(page);
        await input.WaitForAsync().ConfigureAwait(false);
        await input.FillAsync(title).ConfigureAwait(false);
        var createDraft = Sel.CreateDraftOption(page);
        await createDraft.WaitForAsync().ConfigureAwait(false);
        await createDraft.ClickAsync().ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> FindMatchingDraftItemIdsAsync(
        string projectId,
        string title,
        CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        await foreach (var node in _client.QueryPaginatedAsync(
            """
            query($projectId: ID!, $after: String) {
              node(id: $projectId) {
                ... on ProjectV2 {
                  items(first: 100, after: $after, archivedStates: [ARCHIVED, NOT_ARCHIVED]) {
                    nodes {
                      id
                      type
                      content { ... on DraftIssue { title } }
                    }
                    pageInfo { hasNextPage endCursor }
                  }
                }
              }
            }
            """,
            new { projectId },
            "node.items",
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            var content = node.GetProperty("content");
            if (string.Equals(node.GetProperty("type").GetString(), "DRAFT_ISSUE", StringComparison.Ordinal)
                && content.ValueKind == System.Text.Json.JsonValueKind.Object
                && string.Equals(content.GetProperty("title").GetString(), title, StringComparison.Ordinal)
                && node.GetProperty("id").GetString() is { } id)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private Task<IReadOnlyList<string>> WaitForMatchingDraftItemIdsAsync(
        string projectId,
        string title,
        CancellationToken cancellationToken)
        => PollForMatchesAsync(
            token => FindMatchingDraftItemIdsAsync(projectId, title, token),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(500),
            cancellationToken,
            IsReconciliationFailure);

    private async Task<IReadOnlyList<string>> ReconcileDraftInventoryAfterSideEffectAsync(
        string projectId,
        string title)
    {
        try
        {
            return await WaitForMatchingDraftItemIdsAsync(
                projectId,
                title,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsReconciliationFailure(exception))
        {
            IReadOnlyList<string> reconciledIds = [];
            OnProgress?.Invoke(
                $"Field-default check draft reconciliation failed: ids=(none found) title='{title}' cleanup=pending");
            throw new InvalidOperationException(
                $"Field-default check draft reconciliation failed; {FormatDraftInventory(title, reconciledIds)}.",
                exception);
        }
    }

    internal static async Task<IReadOnlyList<string>> PollForMatchesAsync(
        Func<CancellationToken, Task<IReadOnlyList<string>>> queryAsync,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken,
        Func<Exception, bool>? shouldRetry = null)
    {
        ArgumentNullException.ThrowIfNull(queryAsync);
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<string> matches;
            try
            {
                matches = await queryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (shouldRetry?.Invoke(exception) is true
                && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (matches.Count > 0 || DateTimeOffset.UtcNow >= deadline)
            {
                return matches;
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static string FormatDraftInventory(
        string title,
        IReadOnlyList<string> matchingIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(matchingIds);
        var ids = matchingIds.Count == 0
            ? "(none found)"
            : string.Join(",", matchingIds);
        return $"inventory title '{title}' and matching item IDs [{ids}] before cleanup";
    }

    private static bool IsReconciliationFailure(Exception exception)
        => exception is GitHubGraphQLException or HttpRequestException or TimeoutException;

    private async Task DeleteAndConfirmDraftAsync(
        string projectId,
        string itemId,
        string title)
    {
        Exception? deletionException = null;
        try
        {
            await _client.MutationAsync(
                "deleteProjectV2Item",
                """
                mutation($projectId: ID!, $itemId: ID!, $clientMutationId: String!) {
                  deleteProjectV2Item(input: {
                    projectId: $projectId,
                    itemId: $itemId,
                    clientMutationId: $clientMutationId
                  }) {
                    deletedItemId
                  }
                }
                """,
                new { projectId, itemId },
                MutationRetryPolicy.Create,
                target: itemId,
                requiredResultPath: "deletedItemId",
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is GitHubGraphQLException or HttpRequestException or TimeoutException)
        {
            deletionException = exception;
        }

        var remaining = await FindMatchingDraftItemIdsAsync(
            projectId,
            title,
            CancellationToken.None).ConfigureAwait(false);
        if (remaining.Contains(itemId, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"The field-default check draft '{title}' could not be deleted.",
                deletionException);
        }
    }

    private static void ThrowCleanupFailure(string title)
        => throw new InvalidOperationException(
            $"The field-default check draft '{title}' still exists after cleanup.");
}

public sealed record FieldDefaultFixtureCheckResult
{
    public required string ItemId { get; init; }

    public required string Title { get; init; }
}
