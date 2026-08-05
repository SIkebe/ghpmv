using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Fixtures;

/// <summary>Creates the standard API-backed integration-test fixture without PowerShell or gh CLI.</summary>
public sealed class FixtureProjectBuilder
{
    private const string RepositoryClaimFileName = "fixture-repository.txt";
    private const string PendingRepositoryStatus = "pending";
    private const string FallbackPendingRepositoryStatus = "fallback-pending";
    private const string ClaimedRepositoryStatus = "claimed";

    private readonly GitHubGraphQLClient _graphQl;
    private readonly GitHubRestClient _rest;

    public FixtureProjectBuilder(GitHubGraphQLClient graphQl, GitHubRestClient rest)
    {
        ArgumentNullException.ThrowIfNull(graphQl);
        ArgumentNullException.ThrowIfNull(rest);
        _graphQl = graphQl;
        _rest = rest;
    }

    public Action<string>? OnProgress { get; set; }

    public required string OperationLogDirectory { get; init; }

    public bool RequireNewResources { get; init; }

    public bool AllowExistingEmptyRepository { get; init; }

    public async Task<FixtureProjectSetupResult> CreateAsync(
        string organization,
        string title = "gpm-fixture",
        string repositoryName = "fixture-repo",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organization);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryName);

        var apiHost = GetApiHost();
        var operationKey = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{apiHost}\n{organization.ToLowerInvariant()}\n{title}\n{repositoryName.ToLowerInvariant()}")))[..16]
            .ToLowerInvariant();
        var operationDirectory = Path.Combine(OperationLogDirectory, operationKey);
        // Keep this order consistent so overlapping operation and repository scopes cannot deadlock.
        using var operationLock = AcquireFixtureOperationLock(operationDirectory);
        var repositoryFullName = $"{organization}/{repositoryName}";
        using var repositoryLock = AcquireFixtureRepositoryLock(
            OperationLogDirectory,
            apiHost,
            repositoryFullName);
        var projectLog = await ProjectImportLog.LoadAsync(operationDirectory, cancellationToken).ConfigureAwait(false);
        var itemLog = await ImportLog.LoadAsync(operationDirectory, cancellationToken).ConfigureAwait(false);
        var projectMatches = await FindProjectsByTitleAsync(organization, title, cancellationToken).ConfigureAwait(false);
        var (existing, projectOwnedByOperation) = SelectProjectForOperation(
            projectMatches,
            organization,
            title,
            projectLog,
            itemLog,
            RequireNewResources);
        if (RequireNewResources)
        {
            ValidateNewProjectRequirement(
                organization,
                title,
                projectExists: existing is not null,
                projectOwnedByOperation);
        }

        var projectImportWasPending = projectLog.PendingProject is not null
            || projectLog.PendingFields.Count > 0
            || projectLog.PendingIssueFields.Count > 0
            || projectLog.PendingIssueFieldLinks.Count > 0
            || (existing is not null
                && string.Equals(projectLog.CreatedProjectId, existing.Id, StringComparison.Ordinal));
        var shouldImportItems = ShouldImportItems(
            existing is not null,
            HasItemWork(itemLog),
            projectImportWasPending);

        if (itemLog is not null
            && (existing is null || !string.Equals(existing.Id, itemLog.ProjectId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"{ImportLog.FileName} targets project '{itemLog.ProjectId}', but that fixture project was not found.");
        }

        ImportLog? templateLog = itemLog;
        async Task PersistTemplateRestorationAsync(bool required, CancellationToken ct)
        {
            var latestLog = await ImportLog.LoadAsync(operationDirectory, ct).ConfigureAwait(false)
                ?? templateLog
                ?? throw new InvalidOperationException(
                    $"{ImportLog.FileName} was unavailable while persisting template restoration state.");
            latestLog.TemplateRestorationRequired = required;
            await latestLog.SaveAsync(operationDirectory, ct).ConfigureAwait(false);
            templateLog = latestLog;
        }

        ProjectTemplateWriteSession? templateWriteSession = null;
        if (itemLog is { TemplateRestorationRequired: true })
        {
            templateWriteSession = await ProjectTemplateWriteSession.PrepareAsync(
                _graphQl,
                itemLog.ProjectId,
                restorationWasPending: true,
                PersistTemplateRestorationAsync,
                OnProgress,
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var viewerLogin = await _graphQl.GetViewerLoginAsync(cancellationToken).ConfigureAwait(false);
            var repositoryFullName = $"{organization}/{repositoryName}";
            var pullRequestNumber = itemLog is null
                ? await EnsureRepositoryAsync(organization, repositoryName, cancellationToken).ConfigureAwait(false)
                : await FindOpenFixturePullRequestNumberAsync(repositoryFullName, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"The fixture pull request in '{repositoryFullName}' was not found; refusing to mutate fixtures for an existing import log.");

            var snapshot = CreateSnapshot(title, repositoryFullName, viewerLogin, pullRequestNumber);
            var importStatusUpdates = true;
            IReadOnlyDictionary<int, string> matchedFixtureStatusUpdates =
                new Dictionary<int, string>();
            if (itemLog is not null)
            {
                var snapshotFingerprint = ImportLog.ComputeSnapshotFingerprint(snapshot);
                if (!string.Equals(itemLog.SourceSnapshotFingerprint, snapshotFingerprint, StringComparison.Ordinal))
                {
                    itemLog = UpgradeLegacyFixtureLog(itemLog, snapshot);
                    if (itemLog is null)
                    {
                        throw new InvalidOperationException(
                            $"{ImportLog.FileName} in '{operationDirectory}' belongs to a different fixture snapshot. Recreate the preview fixture instead of reusing incompatible artifacts.");
                    }

                    await itemLog.SaveAsync(operationDirectory, cancellationToken).ConfigureAwait(false);
                    templateLog = itemLog;
                }
            }

            if (existing is not null && snapshot.StatusUpdates is { Count: > 0 } expectedStatusUpdates)
            {
                var existingStatusUpdates = await FetchStatusUpdatesAsync(
                    existing.Id,
                    cancellationToken).ConfigureAwait(false);
                var reconciliation = ReconcileFixtureStatusUpdates(
                    expectedStatusUpdates,
                    existingStatusUpdates,
                    itemLog);
                matchedFixtureStatusUpdates = reconciliation.CanonicalMatches;
                importStatusUpdates = reconciliation.ImportRequired;
                if (reconciliation.LogChanged)
                {
                    await itemLog!.SaveAsync(operationDirectory, cancellationToken).ConfigureAwait(false);
                    templateLog = itemLog;
                }

                if (!importStatusUpdates)
                {
                    OnProgress?.Invoke(
                        "Fixture project already contains the expected status update history; leaving it and any unrelated history unchanged.");
                }
                else
                {
                    OnProgress?.Invoke(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Fixture project contains {matchedFixtureStatusUpdates.Count}/{expectedStatusUpdates.Count} expected status updates; seeding only the missing fixture history and leaving unrelated history unchanged."));
                }
            }

            var projectImporter = new ProjectImporter(_graphQl)
            {
                OnProgress = OnProgress,
                OnConflict = existing is null ? ConflictAction.Fail : ConflictAction.Update,
                OperationLogDirectory = operationDirectory,
                PendingItemProjectId = itemLog?.ProjectId,
                RepositoryMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [repositoryFullName] = repositoryFullName,
                },
            };
            var project = await projectImporter.ImportAsync(snapshot, organization, cancellationToken).ConfigureAwait(false);

            await EnsureMultiSelectIssueFieldValueAsync(
                repositoryFullName,
                project,
                cancellationToken).ConfigureAwait(false);

            if (shouldImportItems)
            {
                var itemImporter = new ItemImporter(_graphQl)
                {
                    OnProgress = OnProgress,
                    RepositoryMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [repositoryFullName] = repositoryFullName,
                    },
                    UserMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [viewerLogin] = viewerLogin,
                    },
                };
                var itemResult = await itemImporter.ImportAsync(snapshot, project, operationDirectory, cancellationToken).ConfigureAwait(false);
                foreach (var warning in itemResult.Warnings)
                {
                    OnProgress?.Invoke("warning: " + warning);
                }
            }
            else
            {
                await EnsureExistingSelectValuesAsync(snapshot, project, cancellationToken).ConfigureAwait(false);
                OnProgress?.Invoke(string.Create(CultureInfo.InvariantCulture,
                    $"Fixture project already existed; synchronized fields without duplicating items: {project.Url}"));
            }

            if (importStatusUpdates && snapshot.StatusUpdates is { Count: > 0 })
            {
                templateLog = await ImportLog.LoadAsync(operationDirectory, cancellationToken).ConfigureAwait(false)
                    ?? new ImportLog
                    {
                        ProjectId = project.ProjectId,
                        SourceSnapshotFingerprint = ImportLog.ComputeSnapshotFingerprint(snapshot),
                    };
                if (ReconcileFixtureStatusLog(templateLog, matchedFixtureStatusUpdates))
                {
                    await templateLog.SaveAsync(operationDirectory, cancellationToken).ConfigureAwait(false);
                }

                if (ProjectTemplateWriteSession.RequiresPreparation(templateWriteSession))
                {
                    templateWriteSession = await ProjectTemplateWriteSession.PrepareAsync(
                        _graphQl,
                        project.ProjectId,
                        templateLog.TemplateRestorationRequired,
                        PersistTemplateRestorationAsync,
                        OnProgress,
                        cancellationToken).ConfigureAwait(false);
                }

                var statusUpdateImporter = new StatusUpdateImporter(_graphQl)
                {
                    OnProgress = OnProgress,
                    AddAttributionNote = false,
                };
                await statusUpdateImporter.ImportAsync(
                    snapshot,
                    project,
                    operationDirectory,
                    cancellationToken).ConfigureAwait(false);
            }

            return new FixtureProjectSetupResult(project.ProjectNumber, project.Url, Created: existing is null);
        }
        finally
        {
            if (templateWriteSession is not null)
            {
                await templateWriteSession.RestoreAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    internal static bool ShouldImportItems(
        bool projectAlreadyExists,
        bool hasItemWork,
        bool projectImportWasPending)
        => !projectAlreadyExists || hasItemWork || projectImportWasPending;

    internal static bool HasItemWork(ImportLog? log)
        => log is { Items.Count: > 0 }
            or { ItemStates.Count: > 0 }
            or { PendingDrafts.Count: > 0 }
            or { PendingContents.Count: > 0 };

    internal static ImportLog? UpgradeLegacyFixtureLog(ImportLog log, ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (log.StatusUpdates.Count > 0
            || log.PendingStatusUpdates.Count > 0
            || !string.Equals(
                log.SourceSnapshotFingerprint,
                ImportLog.ComputeSnapshotFingerprint(snapshot with { StatusUpdates = null }),
                StringComparison.Ordinal))
        {
            return null;
        }

        return log with
        {
            SourceSnapshotFingerprint = ImportLog.ComputeSnapshotFingerprint(snapshot),
        };
    }

    internal static IReadOnlyDictionary<int, string> MatchFixtureStatusUpdates(
        IReadOnlyList<StatusUpdateSnapshot> expected,
        IReadOnlyList<FixtureStatusUpdate> actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        for (var left = 0; left < expected.Count; left++)
        {
            for (var right = left + 1; right < expected.Count; right++)
            {
                if (FixtureStatusUpdateMatches(expected[left], expected[right]))
                {
                    throw new InvalidOperationException(
                        "The standard fixture defines duplicate status updates and cannot be seeded safely.");
                }
            }
        }

        var fixtureEntries = new List<(int ExpectedIndex, string TargetId)>();
        int? nextExpectedIndex = null;
        foreach (var candidate in actual)
        {
            var expectedIndex = expected
                .Select((update, index) => (update, index))
                .Where(entry => FixtureStatusUpdateMatches(entry.update, candidate.Update))
                .Select(entry => entry.index)
                .Cast<int?>()
                .SingleOrDefault();
            if (expectedIndex is null)
            {
                continue;
            }

            if (fixtureEntries.Any(entry => entry.ExpectedIndex == expectedIndex.Value))
            {
                // A shared fixture may contain a legacy duplicate. Keep one canonical
                // occurrence without claiming or deleting the duplicate node.
                continue;
            }

            nextExpectedIndex ??= expectedIndex.Value;
            if (expectedIndex.Value != nextExpectedIndex.Value)
            {
                throw UnsafeFixtureHistory(
                    $"found snapshot sequence {expectedIndex} where sequence {nextExpectedIndex} was required");
            }

            fixtureEntries.Add((expectedIndex.Value, candidate.Id));
            nextExpectedIndex++;
        }

        if (fixtureEntries.Count > 0 && nextExpectedIndex != expected.Count)
        {
            throw UnsafeFixtureHistory(
                $"snapshot sequence {nextExpectedIndex} is missing from the created prefix");
        }

        return fixtureEntries.ToDictionary(
            entry => entry.ExpectedIndex,
            entry => entry.TargetId);
    }

    internal static FixtureStatusReconciliation ReconcileFixtureStatusUpdates(
        IReadOnlyList<StatusUpdateSnapshot> expected,
        IReadOnlyList<FixtureStatusUpdate> actual,
        ImportLog? log)
    {
        var canonicalMatches = MatchFixtureStatusUpdates(expected, actual);
        var logChanged = log is not null && ReconcileFixtureStatusLog(log, canonicalMatches);
        return new FixtureStatusReconciliation(
            canonicalMatches,
            ImportRequired: canonicalMatches.Count != expected.Count
                || log is { PendingStatusUpdates.Count: > 0 },
            logChanged);
    }

    internal static bool ReconcileFixtureStatusLog(
        ImportLog log,
        IReadOnlyDictionary<int, string> canonicalMatches)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(canonicalMatches);

        var reconciled = canonicalMatches
            .Where(match => !log.PendingStatusUpdates.ContainsKey(
                match.Key.ToString(CultureInfo.InvariantCulture)))
            .ToDictionary(
                match => match.Key.ToString(CultureInfo.InvariantCulture),
                match => match.Value,
                StringComparer.Ordinal);
        if (log.StatusUpdates.Count == reconciled.Count
            && log.StatusUpdates.All(match =>
                reconciled.TryGetValue(match.Key, out var targetId)
                && string.Equals(match.Value, targetId, StringComparison.Ordinal)))
        {
            return false;
        }

        log.StatusUpdates.Clear();
        foreach (var match in reconciled)
        {
            log.StatusUpdates[match.Key] = match.Value;
        }

        return true;
    }

    private static InvalidOperationException UnsafeFixtureHistory(string detail)
        => new(
            "The existing fixture project's standard status updates are not an append-safe "
            + $"contiguous history ({detail}). No status updates were changed. Use a new fixture "
            + "title or reconcile the fixture history manually.");

    private static bool FixtureStatusUpdateMatches(
        StatusUpdateSnapshot expected,
        StatusUpdateSnapshot actual)
        => string.Equals(NormalizeBody(expected.Body), NormalizeBody(actual.Body), StringComparison.Ordinal)
            && string.Equals(expected.Status, actual.Status, StringComparison.Ordinal)
            && string.Equals(expected.StartDate, actual.StartDate, StringComparison.Ordinal)
            && string.Equals(expected.TargetDate, actual.TargetDate, StringComparison.Ordinal);

    private static string NormalizeBody(string body)
        => body.Replace("\r\n", "\n", StringComparison.Ordinal);

    private async Task<List<FixtureStatusUpdate>> FetchStatusUpdatesAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var updates = new List<FixtureStatusUpdate>();
        await foreach (var node in _graphQl.QueryPaginatedAsync(
            """
            query($projectId: ID!, $first: Int!, $after: String) {
              node(id: $projectId) {
                ... on ProjectV2 {
                  statusUpdates(first: $first, after: $after, orderBy: { field: CREATED_AT, direction: DESC }) {
                    nodes { id body status startDate targetDate createdAt updatedAt }
                    pageInfo { hasNextPage endCursor }
                  }
                }
              }
            }
            """,
            new { projectId, first = 100 },
            "node.statusUpdates",
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            updates.Add(new FixtureStatusUpdate(
                node.GetProperty("id").GetString()
                    ?? throw new JsonException("Project status update contained an empty id."),
                new StatusUpdateSnapshot
                {
                    Body = node.GetProperty("body").GetString() ?? string.Empty,
                    Status = GetOptionalString(node, "status"),
                    StartDate = GetOptionalString(node, "startDate"),
                    TargetDate = GetOptionalString(node, "targetDate"),
                    CreatedAt = node.GetProperty("createdAt").GetString() ?? string.Empty,
                    UpdatedAt = node.GetProperty("updatedAt").GetString() ?? string.Empty,
                }));
        }

        return updates;
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    internal static void ValidateNewProjectRequirement(
        string organization,
        string title,
        bool projectExists,
        bool projectOwnedByOperation = false)
    {
        if (projectExists && !projectOwnedByOperation)
        {
            throw new InvalidOperationException($"Fixture project '{title}' already exists in organization '{organization}'.");
        }
    }

    internal static void ValidateRepositoryRequirement(
        string repositoryFullName,
        bool requireNewResources,
        bool allowExistingEmptyRepository,
        bool repositoryExists,
        bool repositoryIsEmpty)
    {
        if (!requireNewResources || !repositoryExists)
        {
            return;
        }

        if (!allowExistingEmptyRepository)
        {
            throw new InvalidOperationException($"Fixture repository '{repositoryFullName}' already exists.");
        }

        if (!repositoryIsEmpty)
        {
            throw new InvalidOperationException($"Fixture repository '{repositoryFullName}' is not empty.");
        }
    }

    private async Task<int> EnsureRepositoryAsync(
        string organization,
        string repositoryName,
        bool requireNewResources,
        bool allowExistingEmptyRepository,
        string operationDirectory,
        Func<CancellationToken, Task>? beforeWriteAsync,
        Func<CancellationToken, Task>? compensateBeforeWriteAsync,
        CancellationToken cancellationToken)
    {
        var repositoryFullName = $"{organization}/{repositoryName}";
        var repositoryState = requireNewResources
            ? await LoadRepositoryClaimAsync(operationDirectory, cancellationToken).ConfigureAwait(false)
            : null;
        if (repositoryState is not null)
        {
            ValidateRepositoryStateIdentity(repositoryState, repositoryFullName);
        }

        var repository = await _rest.GetAsync($"repos/{repositoryFullName}", cancellationToken).ConfigureAwait(false);
        var beforeWriteInvoked = false;
        if (repositoryState?.Status == PendingRepositoryStatus)
        {
            repository = await ReconcilePendingRepositoryAsync(
                repositoryState,
                repository,
                operationDirectory,
                cancellationToken).ConfigureAwait(false);
            ValidatePrivateRepository(repositoryFullName, repository.Value);
        }
        else if (repositoryState?.Status is ClaimedRepositoryStatus or FallbackPendingRepositoryStatus)
        {
            ValidateClaimedRepository(repositoryState, repositoryFullName, repository);
            ValidatePrivateRepository(repositoryFullName, repository!.Value);
            if (repositoryState.Status == FallbackPendingRepositoryStatus)
            {
                var repositoryIsEmpty = await IsRepositoryEmptyAsync(
                    repositoryFullName,
                    cancellationToken).ConfigureAwait(false);
                ValidateRepositoryRequirement(
                    repositoryFullName,
                    requireNewResources: true,
                    allowExistingEmptyRepository: true,
                    repositoryExists: true,
                    repositoryIsEmpty);
            }
        }
        else if (repository is null)
        {
            if (beforeWriteAsync is not null)
            {
                await beforeWriteAsync(cancellationToken).ConfigureAwait(false);
                beforeWriteInvoked = true;
            }

            if (requireNewResources)
            {
                repositoryState = new RepositoryOperationState(
                    GetApiHost(),
                    repositoryFullName,
                    PendingRepositoryStatus,
                    Guid.NewGuid().ToString("N"));
                await SaveRepositoryClaimAsync(operationDirectory, repositoryState, cancellationToken).ConfigureAwait(false);
            }

            OnProgress?.Invoke($"Creating private repository {repositoryFullName}...");
            try
            {
                object createRequest = repositoryState is null
                    ? new { name = repositoryName, @private = true }
                    : new
                    {
                        name = repositoryName,
                        @private = true,
                        description = GetRepositoryOperationMarker(repositoryState.Value),
                    };
                repository = await _rest.PostAsync(
                    $"orgs/{organization}/repos",
                    createRequest,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (
                exception.StatusCode is { } statusCode
                && (int)statusCode is >= 400 and < 500)
            {
                if (repositoryState is not null)
                {
                    DeleteRepositoryState(operationDirectory);
                }

                if (compensateBeforeWriteAsync is not null)
                {
                    try
                    {
                        await compensateBeforeWriteAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception compensationException)
                    {
                        throw new InvalidOperationException(
                            "Repository creation failed and the reserved fixture Project could not be removed automatically.",
                            new AggregateException(exception, compensationException));
                    }
                }

                throw;
            }

            if (repositoryState is not null)
            {
                await SaveRepositoryClaimAsync(
                    operationDirectory,
                    repositoryState with
                    {
                        Status = ClaimedRepositoryStatus,
                        Value = GetRepositoryId(repository.Value),
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }
        else if (requireNewResources)
        {
            if (allowExistingEmptyRepository)
            {
                ValidatePrivateRepository(repositoryFullName, repository.Value);
            }

            var repositoryIsEmpty = allowExistingEmptyRepository
                && await IsRepositoryEmptyAsync(repositoryFullName, cancellationToken).ConfigureAwait(false);
            ValidateRepositoryRequirement(
                repositoryFullName,
                requireNewResources,
                allowExistingEmptyRepository,
                repositoryExists: true,
                repositoryIsEmpty);
            repositoryState = new RepositoryOperationState(
                GetApiHost(),
                repositoryFullName,
                FallbackPendingRepositoryStatus,
                GetRepositoryId(repository.Value));
            await SaveRepositoryClaimAsync(
                operationDirectory,
                repositoryState,
                cancellationToken).ConfigureAwait(false);
        }

        if (!beforeWriteInvoked && beforeWriteAsync is not null)
        {
            await beforeWriteAsync(cancellationToken).ConfigureAwait(false);
        }

        if (repositoryState?.Status == FallbackPendingRepositoryStatus)
        {
            repositoryState = repositoryState with { Status = ClaimedRepositoryStatus };
            await SaveRepositoryClaimAsync(
                operationDirectory,
                repositoryState,
                cancellationToken).ConfigureAwait(false);
        }

        await EnsureReadmeAsync(repositoryFullName, repositoryName, cancellationToken).ConfigureAwait(false);
        await EnsureIssuesAsync(repositoryFullName, cancellationToken).ConfigureAwait(false);
        await EnsureBugLabelAsync(repositoryFullName, cancellationToken).ConfigureAwait(false);
        return await EnsurePullRequestAsync(repositoryFullName, cancellationToken).ConfigureAwait(false);
    }

    private static (ProjectRef? Project, bool OwnedByOperation) SelectProjectForOperation(
        IReadOnlyList<ProjectRef> matches,
        string organization,
        string title,
        ProjectImportLog projectLog,
        ImportLog? itemLog,
        bool requireNewResources)
    {
        var claimedProjectId = projectLog.CreatedProjectId ?? itemLog?.ProjectId;
        if (claimedProjectId is not null)
        {
            var claimed = matches.FirstOrDefault(
                project => string.Equals(project.Id, claimedProjectId, StringComparison.Ordinal));
            if (claimed is not null)
            {
                RejectUnownedDuplicateProjects(matches, claimed, title, organization, requireNewResources);
                return (claimed, true);
            }

            return (matches.Count > 0 ? matches[0] : null, false);
        }

        if (projectLog.PendingProject is { } pending
            && string.Equals(pending.OwnerLogin, organization, StringComparison.Ordinal)
            && string.Equals(pending.Title, title, StringComparison.Ordinal))
        {
            var baseline = new HashSet<string>(pending.ExistingProjectIds, StringComparer.Ordinal);
            var candidates = matches.Where(project => !baseline.Contains(project.Id)).ToArray();
            if (candidates.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Pending fixture Project operation '{pending.OperationId}' matches multiple same-title Projects.");
            }

            if (candidates.Length == 1)
            {
                RejectUnownedDuplicateProjects(matches, candidates[0], title, organization, requireNewResources);
                return (candidates[0], true);
            }
        }

        return (matches.Count > 0 ? matches[0] : null, false);
    }

    private static void RejectUnownedDuplicateProjects(
        IReadOnlyList<ProjectRef> matches,
        ProjectRef owned,
        string title,
        string organization,
        bool requireNewResources)
    {
        if (requireNewResources
            && matches.Any(project => !string.Equals(project.Id, owned.Id, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Fixture project '{title}' has an unrelated same-title Project in organization '{organization}'.");
        }
    }

    private async Task ValidateClaimedRepositoryAsync(
        string repositoryFullName,
        string operationDirectory,
        CancellationToken cancellationToken)
    {
        var repositoryState = await LoadRepositoryClaimAsync(operationDirectory, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Fixture repository '{repositoryFullName}' has no ownership record for this operation.");
        ValidateRepositoryStateIdentity(repositoryState, repositoryFullName);
        var repository = await _rest.GetAsync($"repos/{repositoryFullName}", cancellationToken).ConfigureAwait(false);
        if (repositoryState.Status == PendingRepositoryStatus)
        {
            var reconciled = await ReconcilePendingRepositoryAsync(
                repositoryState,
                repository,
                operationDirectory,
                cancellationToken).ConfigureAwait(false);
            ValidatePrivateRepository(repositoryFullName, reconciled);
            return;
        }

        ValidateClaimedRepository(repositoryState, repositoryFullName, repository);
        ValidatePrivateRepository(repositoryFullName, repository!.Value);
    }

    private async Task<JsonElement> ReconcilePendingRepositoryAsync(
        RepositoryOperationState pending,
        JsonElement? repository,
        string operationDirectory,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; repository is null && attempt < 2; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
            repository = await _rest.GetAsync($"repos/{pending.FullName}", cancellationToken).ConfigureAwait(false);
        }

        if (repository is null)
        {
            throw new InvalidOperationException(
                $"Pending repository operation '{pending.Value}' is not visible after reconciliation polling. Do not resend it until the target is reconciled manually.");
        }

        if (!repository.Value.TryGetProperty("description", out var description)
            || !string.Equals(
                description.GetString(),
                GetRepositoryOperationMarker(pending.Value),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Repository '{pending.FullName}' does not match pending operation '{pending.Value}'; refusing to claim it.");
        }

        await SaveRepositoryClaimAsync(
            operationDirectory,
            pending with
            {
                Status = ClaimedRepositoryStatus,
                Value = GetRepositoryId(repository.Value),
            },
            cancellationToken).ConfigureAwait(false);
        return repository.Value;
    }

    private void ValidateRepositoryStateIdentity(
        RepositoryOperationState repositoryState,
        string repositoryFullName)
    {
        if (!string.Equals(repositoryState.ApiHost, GetApiHost(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"{RepositoryClaimFileName} belongs to API host '{repositoryState.ApiHost}', not '{GetApiHost()}'.");
        }

        if (!string.Equals(repositoryState.FullName, repositoryFullName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"{RepositoryClaimFileName} claims '{repositoryState.FullName}', not '{repositoryFullName}'.");
        }
    }

    private static void ValidateClaimedRepository(
        RepositoryOperationState repositoryState,
        string repositoryFullName,
        JsonElement? repository)
    {
        if (repository is null
            || !string.Equals(repositoryState.Value, GetRepositoryId(repository.Value), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Fixture repository '{repositoryFullName}' no longer matches the repository recorded by this operation.");
        }
    }

    private static async Task<RepositoryOperationState?> LoadRepositoryClaimAsync(
        string operationDirectory,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(operationDirectory, RepositoryClaimFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var lines = (await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        if (lines.Length != 4
            || (lines[2] != PendingRepositoryStatus
                && lines[2] != FallbackPendingRepositoryStatus
                && lines[2] != ClaimedRepositoryStatus))
        {
            throw new InvalidDataException($"{RepositoryClaimFileName} is malformed.");
        }

        return new RepositoryOperationState(lines[0], lines[1], lines[2], lines[3]);
    }

    private static async Task SaveRepositoryClaimAsync(
        string operationDirectory,
        RepositoryOperationState repositoryState,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(operationDirectory);
        var path = Path.Combine(operationDirectory, RepositoryClaimFileName);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllLinesAsync(
                temporaryPath,
                [
                    repositoryState.ApiHost,
                    repositoryState.FullName,
                    repositoryState.Status,
                    repositoryState.Value,
                ],
                Encoding.UTF8,
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static void DeleteRepositoryState(string operationDirectory)
        => File.Delete(Path.Combine(operationDirectory, RepositoryClaimFileName));

    private static FileStream AcquireFixtureOperationLock(string operationDirectory)
    {
        Directory.CreateDirectory(operationDirectory);
        try
        {
            return new FileStream(
                Path.Combine(operationDirectory, "fixture-operation.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                $"Another fixture operation is already using '{operationDirectory}'.",
                exception);
        }
    }

    private static FileStream AcquireFixtureRepositoryLock(
        string operationLogDirectory,
        string apiHost,
        string repositoryFullName)
    {
        var repositoryLockDirectory = Path.Combine(operationLogDirectory, "repository-locks");
        Directory.CreateDirectory(repositoryLockDirectory);
        var repositoryKey = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{apiHost}\n{repositoryFullName.ToLowerInvariant()}")))[..16]
            .ToLowerInvariant();
        try
        {
            return new FileStream(
                Path.Combine(repositoryLockDirectory, repositoryKey + ".lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                $"Another fixture operation is already using repository '{repositoryFullName}'.",
                exception);
        }
    }

    private static string GetRepositoryOperationMarker(string operationId)
        => $"ghpmv fixture operation {operationId}";

    private string GetApiHost()
        => _rest.BaseUri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();

    private static string GetRepositoryId(JsonElement repository)
    {
        if (!repository.TryGetProperty("id", out var id)
            || string.IsNullOrWhiteSpace(id.ToString()))
        {
            throw new InvalidDataException("GitHub repository response did not contain an id.");
        }

        return id.ToString();
    }

    private static void ValidatePrivateRepository(string repositoryFullName, JsonElement repository)
    {
        if (!repository.TryGetProperty("private", out var isPrivate)
            || isPrivate.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException("GitHub repository response did not contain a private visibility value.");
        }

        if (!isPrivate.GetBoolean())
        {
            throw new InvalidOperationException(
                $"Fixture repository '{repositoryFullName}' must be private.");
        }
    }

    private async Task<bool> IsRepositoryEmptyAsync(string repositoryFullName, CancellationToken cancellationToken)
    {
        JsonElement? contents;
        try
        {
            contents = await _rest.GetAsync($"repos/{repositoryFullName}/contents", cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
        {
            contents = null;
        }

        var issues = await _rest.GetAsync($"repos/{repositoryFullName}/issues?state=all&per_page=1", cancellationToken).ConfigureAwait(false);
        return contents is null
            && issues is { ValueKind: JsonValueKind.Array }
            && issues.Value.GetArrayLength() == 0;
    }

    private async Task EnsureReadmeAsync(string repositoryFullName, string repositoryName, CancellationToken cancellationToken)
    {
        if (await _rest.GetAsync($"repos/{repositoryFullName}/contents/README.md", cancellationToken).ConfigureAwait(false) is not null)
        {
            return;
        }

        OnProgress?.Invoke("Creating initial commit (README.md)...");
        var content = Convert.ToBase64String(Encoding.UTF8.GetBytes($"# {repositoryName}\n\nPermanent fixture repository for ghpmv integration tests.\n"));
        await _rest.PutAsync(
            $"repos/{repositoryFullName}/contents/README.md",
            new { message = "Initial commit", content },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureIssuesAsync(string repositoryFullName, CancellationToken cancellationToken)
    {
        for (var number = 1; number <= 2; number++)
        {
            var existing = await _rest.GetAsync($"repos/{repositoryFullName}/issues/{number}", cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                OnProgress?.Invoke($"Creating Fixture issue {number}...");
                var created = await _rest.PostAsync(
                    $"repos/{repositoryFullName}/issues",
                    new { title = $"Fixture issue {number}", body = $"Permanent fixture issue {number}." },
                    cancellationToken).ConfigureAwait(false);
                EnsureExpectedIssue(created, repositoryFullName, number);
                continue;
            }

            EnsureExpectedIssue(existing.Value, repositoryFullName, number);
        }
    }

    private static void EnsureExpectedIssue(JsonElement issue, string repositoryFullName, int expectedNumber)
    {
        var actualNumber = issue.GetProperty("number").GetInt32();
        if (actualNumber != expectedNumber)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Fixture repository '{repositoryFullName}' must contain Issue #{expectedNumber}, but GitHub created Issue #{actualNumber}. Use an empty fixture repository so Issue #1 and #2 can be reserved for the fixture."));
        }

        if (issue.TryGetProperty("pull_request", out _))
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Fixture repository '{repositoryFullName}' must contain Issue #{expectedNumber}, but #{expectedNumber} is a pull request. Use an empty fixture repository so Issue #1 and #2 can be reserved for the fixture."));
        }
    }

    private async Task EnsureBugLabelAsync(string repositoryFullName, CancellationToken cancellationToken)
    {
        if (await _rest.GetAsync($"repos/{repositoryFullName}/labels/bug", cancellationToken).ConfigureAwait(false) is not null)
        {
            return;
        }

        OnProgress?.Invoke("Creating label 'bug'...");
        await _rest.PostAsync(
            $"repos/{repositoryFullName}/labels",
            new { name = "bug", color = "d73a4a", description = "Fixture label for Auto-add workflow tests" },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> EnsurePullRequestAsync(string repositoryFullName, CancellationToken cancellationToken)
    {
        var branchName = "fixture-pr-branch";
        if (await FindOpenFixturePullRequestNumberAsync(repositoryFullName, cancellationToken).ConfigureAwait(false) is { } existingNumber)
        {
            return existingNumber;
        }

        var repository = await _rest.GetAsync($"repos/{repositoryFullName}", cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Repository '{repositoryFullName}' was not found after creation.");
        var defaultBranch = repository.GetProperty("default_branch").GetString() ?? "main";
        var baseRef = await _rest.GetAsync($"repos/{repositoryFullName}/git/ref/heads/{defaultBranch}", cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Default branch '{defaultBranch}' was not found in '{repositoryFullName}'.");
        var baseSha = baseRef.GetProperty("object").GetProperty("sha").GetString()
            ?? throw new InvalidOperationException($"Default branch '{defaultBranch}' returned no SHA.");

        if (await _rest.GetAsync($"repos/{repositoryFullName}/git/ref/heads/{branchName}", cancellationToken).ConfigureAwait(false) is null)
        {
            await _rest.PostAsync(
                $"repos/{repositoryFullName}/git/refs",
                new { @ref = $"refs/heads/{branchName}", sha = baseSha },
                cancellationToken).ConfigureAwait(false);
        }

        var path = $"repos/{repositoryFullName}/contents/fixture-pr.txt";
        var existingFile = await _rest.GetAsync(path + $"?ref={branchName}", cancellationToken).ConfigureAwait(false);
        if (existingFile is null)
        {
            var content = Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture PR file\n"));
            await _rest.PutAsync(path, new { message = "Add fixture PR file", content, branch = branchName }, cancellationToken).ConfigureAwait(false);
        }

        var pull = await _rest.PostAsync(
            $"repos/{repositoryFullName}/pulls",
            new
            {
                title = "Fixture pull request",
                body = "Permanent fixture PR (kept open for ghpmv integration tests).",
                head = branchName,
                @base = defaultBranch,
            },
            cancellationToken).ConfigureAwait(false);
        var number = pull.GetProperty("number").GetInt32();
        OnProgress?.Invoke($"Created Fixture pull request #{number}.");
        return number;
    }

    private async Task<int?> FindOpenFixturePullRequestNumberAsync(
        string repositoryFullName,
        CancellationToken cancellationToken)
    {
        const string branchName = "fixture-pr-branch";
        var owner = repositoryFullName[..repositoryFullName.IndexOf('/', StringComparison.Ordinal)];
        var head = Uri.EscapeDataString($"{owner}:{branchName}");
        var pulls = await _rest.GetAsync(
            $"repos/{repositoryFullName}/pulls?state=open&head={head}&per_page=1",
            cancellationToken).ConfigureAwait(false);
        var firstOpen = pulls?.EnumerateArray().FirstOrDefault();
        return firstOpen is { ValueKind: JsonValueKind.Object } openPullRequest
            ? openPullRequest.GetProperty("number").GetInt32()
            : null;
    }

    public static ProjectSnapshot CreateSnapshot(string title, string repositoryFullName, string viewerLogin, int pullRequestNumber)
    {
        var today = new DateTime(2026, 1, 1);
        var sprint0Start = today.AddDays(-28).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var sprint1Start = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var sprint2Start = today.AddDays(14).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var sprint3Start = today.AddDays(28).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return new ProjectSnapshot
        {
            SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
            Project = new ProjectInfoSnapshot
            {
                Title = title,
                ShortDescription = "gpm fixture project",
                Readme = "# ghpmv fixture 📦\n\nPermanent fixture project for ghpmv integration tests.\n\n- All custom field types (Text / Number / Date / Single-select / Multi-select / Iteration)\n- An organization multi-select Issue Field with multiple selected values\n- Drafts with 日本語 values, an Issue, a PR, an archived item and an assigned item\n- Views and workflows can be created by running `ghpmv setup --fixture-ui` (C# browser module) 🚀",
                Public = false,
                Closed = false,
            },
            Fields =
            [
                new FieldSnapshot
                {
                    Name = "Status",
                    DataType = "SINGLE_SELECT",
                    Options =
                    [
                        new SingleSelectOptionSnapshot { Id = "todo", Name = "Todo", Color = "GRAY", Description = "Not started" },
                        new SingleSelectOptionSnapshot { Id = "in-progress", Name = "In Progress", Color = "YELLOW", Description = "In progress" },
                        new SingleSelectOptionSnapshot { Id = "done", Name = "Done", Color = "GREEN", Description = "Done" },
                    ],
                },
                new FieldSnapshot { Name = "Fixture Text", DataType = "TEXT" },
                new FieldSnapshot { Name = "Fixture Number", DataType = "NUMBER" },
                new FieldSnapshot { Name = "Fixture Date", DataType = "DATE" },
                new FieldSnapshot
                {
                    Name = "Fixture Select",
                    DataType = "SINGLE_SELECT",
                    Options =
                    [
                        new SingleSelectOptionSnapshot { Id = "alpha", Name = "Alpha", Color = "RED", Description = "First" },
                        new SingleSelectOptionSnapshot { Id = "beta", Name = "Beta", Color = "BLUE", Description = "Second" },
                        new SingleSelectOptionSnapshot { Id = "gamma", Name = "Gamma", Color = "GREEN", Description = "Third" },
                    ],
                },
                new FieldSnapshot
                {
                    Name = "Fixture Sprint",
                    DataType = "ITERATION",
                    IterationConfiguration = new IterationConfigurationSnapshot
                    {
                        Duration = 14,
                        StartDay = 1,
                        CompletedIterations = [new IterationSnapshot { Id = "sprint-0", Title = "Sprint 0", StartDate = sprint0Start, Duration = 14 }],
                        Iterations =
                        [
                            new IterationSnapshot { Id = "sprint-1", Title = "Sprint 1", StartDate = sprint1Start, Duration = 14 },
                            new IterationSnapshot { Id = "sprint-2", Title = "Sprint 2", StartDate = sprint2Start, Duration = 14 },
                            new IterationSnapshot { Id = "sprint-3", Title = "Sprint 3", StartDate = sprint3Start, Duration = 14 },
                        ],
                    },
                },
                new FieldSnapshot
                {
                    Name = "Fixture Areas",
                    DataType = "MULTI_SELECT",
                    Options =
                    [
                        new SingleSelectOptionSnapshot { Id = "backend", Name = "Backend", Color = "PURPLE", Description = "Backend work" },
                        new SingleSelectOptionSnapshot { Id = "frontend", Name = "Frontend", Color = "BLUE", Description = "Frontend work" },
                        new SingleSelectOptionSnapshot { Id = "operations", Name = "Operations", Color = "YELLOW", Description = "Operations work" },
                    ],
                },
                new FieldSnapshot
                {
                    Name = "Fixture Teams",
                    DataType = "MULTI_SELECT",
                    Options =
                    [
                        new SingleSelectOptionSnapshot { Id = "platform", Name = "Platform", Color = "PURPLE", Description = "Platform work" },
                        new SingleSelectOptionSnapshot { Id = "sdk", Name = "SDK", Color = "GREEN", Description = "SDK work" },
                        new SingleSelectOptionSnapshot { Id = "docs", Name = "Docs", Color = "BLUE", Description = "Documentation work" },
                    ],
                    IssueField = new IssueFieldConfigurationSnapshot
                    {
                        Description = "Teams involved in the issue",
                        Visibility = "ALL",
                    },
                },
            ],
            Views = [],
            Workflows = [],
            Items =
            [
                Draft(0, "Fixture draft 1", false, [],
                    Text("日本語テキスト & <special> chars"), Number(3.14), Date(today.AddDays(-21)), Select("Alpha"), ProjectMultiSelect("Backend", "Frontend"), Sprint("Sprint 0"), Status("Todo")),
                Draft(1, "Fixture draft 2", false, [],
                    Text("Café emoji 🚀 – em dash"), Number(-42), Date(today.AddDays(4)), Select("Beta"), ProjectMultiSelect("Operations"), Sprint("Sprint 1"), Status("In Progress")),
                Draft(2, "Fixture draft 3", false, [],
                    Text("plain ascii text"), Number(0), Date(today.AddDays(26)), Select("Gamma"), ProjectMultiSelect("Frontend"), Sprint("Sprint 2"), Status("Done")),
                new ItemSnapshot
                {
                    Type = "ISSUE",
                    Position = 3,
                    IsArchived = false,
                    Repository = repositoryFullName,
                    Number = 1,
                    FieldValues = [Status("Todo"), ProjectMultiSelect("Backend", "Operations"), IssueMultiSelect("Platform", "SDK")],
                },
                new ItemSnapshot { Type = "PULL_REQUEST", Position = 4, IsArchived = false, Repository = repositoryFullName, Number = pullRequestNumber, FieldValues = [Status("In Progress"), ProjectMultiSelect("Frontend", "Operations")] },
                Draft(5, "Fixture archived draft", true, [], Status("Done")),
                Draft(6, "Fixture assigned draft", false, [viewerLogin], Status("Todo")),
            ],
            StatusUpdates =
            [
                new StatusUpdateSnapshot
                {
                    Body = "Fixture migration is complete.",
                    Status = "COMPLETE",
                    StartDate = "2026-01-01",
                    TargetDate = "2026-04-15",
                    Creator = viewerLogin,
                    CreatedAt = "2026-01-05T09:00:00Z",
                    UpdatedAt = "2026-01-05T09:00:00Z",
                },
                new StatusUpdateSnapshot
                {
                    Body = "The fixture is temporarily off track.",
                    Status = "OFF_TRACK",
                    StartDate = null,
                    TargetDate = "2026-04-15",
                    Creator = viewerLogin,
                    CreatedAt = "2026-01-04T09:00:00Z",
                    UpdatedAt = "2026-01-04T09:00:00Z",
                },
                new StatusUpdateSnapshot
                {
                    Body = "A fixture risk was identified.",
                    Status = "AT_RISK",
                    StartDate = "2026-01-01",
                    TargetDate = null,
                    Creator = viewerLogin,
                    CreatedAt = "2026-01-03T09:00:00Z",
                    UpdatedAt = "2026-01-03T09:00:00Z",
                },
                new StatusUpdateSnapshot
                {
                    Body = "Implementation is on track.\n\n- API\n- Browser",
                    Status = "ON_TRACK",
                    StartDate = "2026-01-01",
                    TargetDate = "2026-03-31",
                    Creator = viewerLogin,
                    CreatedAt = "2026-01-02T09:00:00Z",
                    UpdatedAt = "2026-01-02T10:00:00Z",
                },
                new StatusUpdateSnapshot
                {
                    Body = "Fixture kickoff with **Markdown**.",
                    Status = "INACTIVE",
                    StartDate = null,
                    TargetDate = null,
                    Creator = viewerLogin,
                    CreatedAt = "2026-01-01T09:00:00Z",
                    UpdatedAt = "2026-01-01T09:00:00Z",
                },
            ],
            LinkedRepositories = [repositoryFullName],
        };

        static ItemSnapshot Draft(int position, string title, bool archived, IReadOnlyList<string> assignees, params FieldValueSnapshot[] values) => new()
        {
            Type = "DRAFT_ISSUE",
            Position = position,
            IsArchived = archived,
            Draft = new DraftIssueSnapshot { Title = title, Body = null, Creator = null, CreatedAt = null, Assignees = assignees },
            FieldValues = values,
        };

        static FieldValueSnapshot Text(string value) => new() { FieldName = "Fixture Text", Text = value };
        static FieldValueSnapshot Number(double value) => new() { FieldName = "Fixture Number", Number = value };
        static FieldValueSnapshot Date(DateTime value) => new() { FieldName = "Fixture Date", Date = value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        static FieldValueSnapshot Select(string value) => new() { FieldName = "Fixture Select", SingleSelectOptionName = value };
        static FieldValueSnapshot ProjectMultiSelect(params string[] values) => new() { FieldName = "Fixture Areas", IsIssueField = false, MultiSelectOptionNames = values };
        static FieldValueSnapshot IssueMultiSelect(params string[] values) => new() { FieldName = "Fixture Teams", IsIssueField = true, MultiSelectOptionNames = values };
        static FieldValueSnapshot Sprint(string value) => new() { FieldName = "Fixture Sprint", IterationTitle = value };
        static FieldValueSnapshot Status(string value) => new() { FieldName = "Status", SingleSelectOptionName = value };
    }

    private async Task EnsureMultiSelectIssueFieldValueAsync(
        string repositoryFullName,
        ImportResult project,
        CancellationToken cancellationToken)
    {
        const string fieldName = "Fixture Teams";
        if (!project.IssueFieldIds.TryGetValue(fieldName, out var fieldId)
            || !project.IssueFieldOptionIds.TryGetValue(fieldName, out var options)
            || !options.TryGetValue("Platform", out var platformId)
            || !options.TryGetValue("SDK", out var sdkId))
        {
            throw new InvalidOperationException(
                $"Fixture Issue Field '{fieldName}' or its expected options were not mapped.");
        }

        var separator = repositoryFullName.IndexOf('/', StringComparison.Ordinal);
        var owner = repositoryFullName[..separator];
        var name = repositoryFullName[(separator + 1)..];
        var data = await _graphQl.QueryAsync(
            """
            query($owner: String!, $name: String!) {
              repository(owner: $owner, name: $name) {
                issue(number: 1) { id }
              }
            }
            """,
            new { owner, name },
            cancellationToken).ConfigureAwait(false);
        var issueId = data.GetProperty("repository").GetProperty("issue").GetProperty("id").GetString()
            ?? throw new GitHubGraphQLException($"Fixture issue '{repositoryFullName}#1' returned no id.");

        await _graphQl.MutationAsync(
            "setIssueFieldValue",
            """
            mutation($issueId: ID!, $issueFields: [IssueFieldCreateOrUpdateInput!]!, $clientMutationId: String!) {
              setIssueFieldValue(input: { issueId: $issueId, issueFields: $issueFields, clientMutationId: $clientMutationId }) {
                issue { id }
              }
            }
            """,
            new
            {
                issueId,
                issueFields = new[]
                {
                    new
                    {
                        fieldId,
                        multiSelectOptionIds = new[] { platformId, sdkId },
                    },
                },
            },
            MutationRetryPolicy.Idempotent,
            target: issueId,
            requiredResultPath: "issue.id",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureExistingSelectValuesAsync(
        ProjectSnapshot snapshot,
        ImportResult project,
        CancellationToken cancellationToken)
    {
        var data = await _graphQl.QueryAsync(
            """
            query($projectId: ID!) {
              node(id: $projectId) {
                ... on ProjectV2 {
                  items(first: 100, archivedStates: [ARCHIVED, NOT_ARCHIVED]) {
                    nodes {
                      id
                      isArchived
                      content {
                        __typename
                        ... on DraftIssue { title }
                        ... on Issue { number repository { nameWithOwner } }
                        ... on PullRequest { number repository { nameWithOwner } }
                      }
                    }
                  }
                }
              }
            }
            """,
            new { projectId = project.ProjectId },
            cancellationToken).ConfigureAwait(false);
        var itemIds = data.GetProperty("node").GetProperty("items").GetProperty("nodes")
            .EnumerateArray()
            .Where(node => node.GetProperty("content").ValueKind == JsonValueKind.Object)
            .ToDictionary(
                node => GetFixtureItemIdentity(node.GetProperty("content")),
                node => (
                    Id: node.GetProperty("id").GetString()
                        ?? throw new GitHubGraphQLException("Fixture Project item id was null."),
                    IsArchived: node.GetProperty("isArchived").GetBoolean()),
                StringComparer.Ordinal);

        foreach (var item in snapshot.Items)
        {
            var selectValues = item.FieldValues
                .Where(value => value.SingleSelectOptionName is not null
                    || value is { IsIssueField: not true, MultiSelectOptionNames: not null })
                .ToArray();
            if (selectValues.Length == 0)
            {
                continue;
            }

            var identity = GetFixtureItemIdentity(item);
            if (!itemIds.TryGetValue(identity, out var itemReference))
            {
                throw new InvalidOperationException(
                    $"Existing fixture item '{identity}' was not found; recreate the preview fixture.");
            }

            if (itemReference.IsArchived)
            {
                await SetFixtureItemArchivedAsync(
                    project.ProjectId,
                    itemReference.Id,
                    archived: false,
                    cancellationToken).ConfigureAwait(false);
            }

            try
            {
                foreach (var value in selectValues)
                {
                    if (!project.FieldIds.TryGetValue(value.FieldName, out var fieldId)
                        || !project.OptionIds.TryGetValue(value.FieldName, out var options))
                    {
                        throw new InvalidOperationException(
                            $"Fixture select field '{value.FieldName}' was not mapped.");
                    }

                    object valueInput;
                    if (value.SingleSelectOptionName is { } optionName)
                    {
                        if (!options.TryGetValue(optionName, out var optionId))
                        {
                            throw new InvalidOperationException(
                                $"Fixture select value '{value.FieldName}={optionName}' was not mapped.");
                        }

                        valueInput = new { singleSelectOptionId = optionId };
                    }
                    else
                    {
                        var optionIds = value.MultiSelectOptionNames!
                            .Select(optionName => options.TryGetValue(optionName, out var optionId)
                                ? optionId
                                : throw new InvalidOperationException(
                                    $"Fixture multi-select value '{value.FieldName}={optionName}' was not mapped."))
                            .ToArray();
                        valueInput = new { multiSelectOptionIds = optionIds };
                    }

                    await _graphQl.MutationAsync(
                        "updateProjectV2ItemFieldValue",
                        """
                        mutation($projectId: ID!, $itemId: ID!, $fieldId: ID!, $value: ProjectV2FieldValue!, $clientMutationId: String!) {
                          updateProjectV2ItemFieldValue(input: { projectId: $projectId, itemId: $itemId, fieldId: $fieldId, value: $value, clientMutationId: $clientMutationId }) {
                            projectV2Item { id }
                          }
                        }
                        """,
                        new
                        {
                            projectId = project.ProjectId,
                            itemId = itemReference.Id,
                            fieldId,
                            value = valueInput,
                        },
                        MutationRetryPolicy.Idempotent,
                        target: itemReference.Id,
                        requiredResultPath: "projectV2Item.id",
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                if (itemReference.IsArchived)
                {
                    await SetFixtureItemArchivedAsync(
                        project.ProjectId,
                        itemReference.Id,
                        archived: true,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task SetFixtureItemArchivedAsync(
        string projectId,
        string itemId,
        bool archived,
        CancellationToken cancellationToken)
    {
        var operationName = archived ? "archiveProjectV2Item" : "unarchiveProjectV2Item";
        var mutation = archived
            ? """
              mutation($projectId: ID!, $itemId: ID!, $clientMutationId: String!) {
                archiveProjectV2Item(input: { projectId: $projectId, itemId: $itemId, clientMutationId: $clientMutationId }) {
                  item { id }
                }
              }
              """
            : """
              mutation($projectId: ID!, $itemId: ID!, $clientMutationId: String!) {
                unarchiveProjectV2Item(input: { projectId: $projectId, itemId: $itemId, clientMutationId: $clientMutationId }) {
                  item { id }
                }
              }
              """;
        await _graphQl.MutationAsync(
            operationName,
            mutation,
            new { projectId, itemId },
            MutationRetryPolicy.Idempotent,
            target: itemId,
            requiredResultPath: "item.id",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string GetFixtureItemIdentity(ItemSnapshot item) =>
        item.Type switch
        {
            "DRAFT_ISSUE" when item.Draft is not null => $"DRAFT_ISSUE:{item.Draft.Title}",
            "ISSUE" or "PULL_REQUEST" when item.Repository is not null && item.Number is not null =>
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{item.Type}:{item.Repository.ToLowerInvariant()}:{item.Number.Value}"),
            _ => throw new InvalidOperationException($"Unsupported fixture item type '{item.Type}'."),
        };

    private static string GetFixtureItemIdentity(JsonElement content)
    {
        var type = content.GetProperty("__typename").GetString();
        return type switch
        {
            "DraftIssue" => $"DRAFT_ISSUE:{content.GetProperty("title").GetString()}",
            "Issue" or "PullRequest" => string.Create(
                CultureInfo.InvariantCulture,
                $"{(type == "Issue" ? "ISSUE" : "PULL_REQUEST")}:{content.GetProperty("repository").GetProperty("nameWithOwner").GetString()?.ToLowerInvariant()}:{content.GetProperty("number").GetInt32()}"),
            _ => throw new InvalidOperationException($"Unsupported existing fixture item type '{type}'."),
        };
    }


    private async Task<List<ProjectRef>> FindProjectsByTitleAsync(string organization, string title, CancellationToken cancellationToken)
    {
        List<ProjectRef> matches = [];
        await foreach (var node in _graphQl.QueryPaginatedAsync(
            """
            query($login: String!, $after: String) {
              organization(login: $login) {
                projectsV2(first: 50, after: $after) {
                  nodes { id number title url }
                  pageInfo { hasNextPage endCursor }
                }
              }
            }
            """,
            new { login = organization },
            "organization.projectsV2",
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(node.GetProperty("title").GetString(), title, StringComparison.Ordinal))
            {
                matches.Add(new ProjectRef(
                    node.GetProperty("id").GetString() ?? string.Empty,
                    node.GetProperty("number").GetInt32(),
                    node.GetProperty("url").GetString() ?? string.Empty));
            }
        }

        return matches;
    }

    private sealed record ProjectRef(string Id, int Number, string Url);

    internal sealed record FixtureStatusUpdate(string Id, StatusUpdateSnapshot Update);

    internal sealed record FixtureStatusReconciliation(
        IReadOnlyDictionary<int, string> CanonicalMatches,
        bool ImportRequired,
        bool LogChanged);
}

public sealed record FixtureProjectSetupResult(
    int ProjectNumber,
    string Url,
    bool Created,
    bool OwnedByOperation = false)
{
    public bool ShouldSkipUiSetup(bool projectExplicitlySelected, bool uiSetupCompleted)
        => !projectExplicitlySelected
            && !Created
            && (!OwnedByOperation || uiSetupCompleted);
}
