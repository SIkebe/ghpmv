using System.Globalization;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Import;

/// <summary>
/// Recreates Project status update history and persists each target node id in
/// <c>import-log.json</c> before advancing to the next update.
/// </summary>
public sealed class StatusUpdateImporter
{
    private static readonly HashSet<string> SupportedStatuses =
        new(["INACTIVE", "ON_TRACK", "AT_RISK", "OFF_TRACK", "COMPLETE"], StringComparer.Ordinal);

    private readonly GitHubGraphQLClient _client;

    public StatusUpdateImporter(GitHubGraphQLClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public Action<string>? OnProgress { get; set; }

    /// <summary>
    /// Whether to prepend source attribution before creation. Fixture setup disables this
    /// because it is creating the source history rather than a migrated copy.
    /// </summary>
    public bool AddAttributionNote { get; init; } = true;

    public async Task<StatusUpdateImportResult> ImportAsync(
        ProjectSnapshot snapshot,
        ImportResult target,
        string logDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);

        if (snapshot.StatusUpdates is null)
        {
            OnProgress?.Invoke("Status updates were not captured by this schema-v1 snapshot; leaving the target history unchanged.");
            return EmptyResult();
        }

        ValidateStatusUpdates(snapshot.StatusUpdates);
        var log = await LoadLogAsync(snapshot, target.ProjectId, logDirectory, cancellationToken).ConfigureAwait(false);
        ValidateLogAgainstSnapshot(log, snapshot.StatusUpdates.Count, target.ProjectId);

        var ordered = snapshot.StatusUpdates
            .Select((update, sourceIndex) => new OrderedStatusUpdate(
                update,
                sourceIndex,
                DateTimeOffset.Parse(update.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)))
            .OrderBy(entry => entry.CreatedAt)
            // The snapshot is newest-first. For identical source timestamps, create the
            // higher sequence index first so GitHub's newest-first read preserves sequence.
            .ThenByDescending(entry => entry.SourceIndex)
            .ToList();
        var created = 0;
        var resumed = 0;
        var alreadyComplete = 0;

        for (var importIndex = 0; importIndex < ordered.Count; importIndex++)
        {
            var entry = ordered[importIndex];
            var key = entry.SourceIndex.ToString(CultureInfo.InvariantCulture);
            var prefix = string.Create(CultureInfo.InvariantCulture, $"[{importIndex + 1}/{ordered.Count}]");
            if (log.StatusUpdates.ContainsKey(key))
            {
                OnProgress?.Invoke($"{prefix} Status update at snapshot sequence {entry.SourceIndex}: already complete.");
                alreadyComplete++;
                continue;
            }

            string? targetId = null;
            if (log.PendingStatusUpdates.TryGetValue(key, out var pending))
            {
                throw new StatusUpdateReconciliationRequiredException(
                    pending.OperationId,
                    pending.ProjectId,
                    entry.SourceIndex,
                    Path.Combine(logDirectory, ImportLog.FileName));
            }
            else
            {
                var operationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                pending = new PendingStatusUpdateOperation
                {
                    OperationId = operationId,
                    ProjectId = target.ProjectId,
                };
                log.PendingStatusUpdates[key] = pending;
                await log.SaveAsync(logDirectory, cancellationToken).ConfigureAwait(false);
                OnProgress?.Invoke($"{prefix} Creating status update at snapshot sequence {entry.SourceIndex}...");

                try
                {
                    targetId = await CreateAsync(target.ProjectId, entry.Update, operationId, cancellationToken).ConfigureAwait(false);
                }
                catch (AmbiguousMutationResultException)
                {
                    throw;
                }
                catch
                {
                    log.PendingStatusUpdates.Remove(key);
                    await log.SaveAsync(logDirectory, CancellationToken.None).ConfigureAwait(false);
                    throw;
                }

                created++;
            }

            log.StatusUpdates[key] = targetId;
            log.PendingStatusUpdates.Remove(key);
            await log.SaveAsync(logDirectory, cancellationToken).ConfigureAwait(false);
        }

        OnProgress?.Invoke(string.Create(
            CultureInfo.InvariantCulture,
            $"Status update import finished: {created} created, {resumed} resumed, {alreadyComplete} already complete."));
        return new StatusUpdateImportResult
        {
            Created = created,
            Resumed = resumed,
            AlreadyComplete = alreadyComplete,
        };
    }

    /// <summary>Adds source attribution that GitHub's create API cannot preserve.</summary>
    public static string BuildImportedBody(StatusUpdateSnapshot update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var note = update.Creator is { Length: > 0 } creator
            ? $"> _Originally created by @{creator} on {update.CreatedAt}._"
            : $"> _Originally created on {update.CreatedAt}._";
        return string.IsNullOrEmpty(update.Body) ? note : note + "\n\n" + update.Body;
    }

    private static async Task<ImportLog> LoadLogAsync(
        ProjectSnapshot snapshot,
        string projectId,
        string logDirectory,
        CancellationToken cancellationToken)
    {
        var fingerprint = ImportLog.ComputeSnapshotFingerprint(snapshot);
        var log = await ImportLog.LoadAsync(logDirectory, cancellationToken).ConfigureAwait(false);
        if (log is not null
            && (!string.Equals(log.ProjectId, projectId, StringComparison.Ordinal)
                || !string.Equals(log.SourceSnapshotFingerprint, fingerprint, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"{ImportLog.FileName} in '{logDirectory}' belongs to a different source snapshot or target project. Use a separate log directory or restore the matching snapshot and target before resuming.");
        }

        return log ?? new ImportLog
        {
            ProjectId = projectId,
            SourceSnapshotFingerprint = fingerprint,
        };
    }

    private static void ValidateLogAgainstSnapshot(ImportLog log, int count, string projectId)
    {
        if (log.StatusUpdates.Keys.Concat(log.PendingStatusUpdates.Keys).Any(key =>
                !int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || index < 0
                || index >= count)
            || log.PendingStatusUpdates.Values.Any(pending =>
                !string.Equals(pending.ProjectId, projectId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"{ImportLog.FileName} contains status update state that does not match the selected snapshot and target project.");
        }
    }

    private static void ValidateStatusUpdates(IReadOnlyList<StatusUpdateSnapshot> updates)
    {
        for (var index = 0; index < updates.Count; index++)
        {
            var update = updates[index];
            if (update.Status is not null && !SupportedStatuses.Contains(update.Status))
            {
                throw new InvalidDataException(
                    $"Status update at snapshot sequence {index} has unsupported status '{update.Status}'.");
            }

            if (!DateTimeOffset.TryParse(
                    update.CreatedAt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out _))
            {
                throw new InvalidDataException(
                    $"Status update at snapshot sequence {index} has invalid createdAt '{update.CreatedAt}'.");
            }

            ValidateDate(update.StartDate, "startDate", index);
            ValidateDate(update.TargetDate, "targetDate", index);
        }
    }

    private static void ValidateDate(string? value, string propertyName, int index)
    {
        if (value is not null
            && !DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            throw new InvalidDataException(
                $"Status update at snapshot sequence {index} has invalid {propertyName} '{value}'.");
        }
    }

    private async Task<string> CreateAsync(
        string projectId,
        StatusUpdateSnapshot update,
        string clientMutationId,
        CancellationToken cancellationToken)
    {
        var data = await _client.MutationAsync(
            "createProjectV2StatusUpdate",
            """
            mutation($projectId: ID!, $body: String!, $status: ProjectV2StatusUpdateStatus, $startDate: Date, $targetDate: Date, $clientMutationId: String!) {
              createProjectV2StatusUpdate(input: { projectId: $projectId, body: $body, status: $status, startDate: $startDate, targetDate: $targetDate, clientMutationId: $clientMutationId }) {
                statusUpdate { id }
              }
            }
            """,
            new
            {
                projectId,
                body = AddAttributionNote ? BuildImportedBody(update) : update.Body,
                status = update.Status,
                startDate = update.StartDate,
                targetDate = update.TargetDate,
            },
            target: projectId,
            clientMutationId: clientMutationId,
            requiredResultPath: "statusUpdate.id",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return data.GetProperty("createProjectV2StatusUpdate")
            .GetProperty("statusUpdate")
            .GetProperty("id")
            .GetString()
            ?? throw new GitHubGraphQLException("createProjectV2StatusUpdate returned an empty status update id.");
    }

    private static StatusUpdateImportResult EmptyResult() => new()
    {
        Created = 0,
        Resumed = 0,
        AlreadyComplete = 0,
    };

    private sealed record OrderedStatusUpdate(
        StatusUpdateSnapshot Update,
        int SourceIndex,
        DateTimeOffset CreatedAt);
}
