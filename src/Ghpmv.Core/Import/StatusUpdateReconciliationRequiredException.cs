namespace Ghpmv.Core.Import;

/// <summary>
/// Thrown when a status-update create may have succeeded but no target node ID was
/// durably persisted, so automatic recovery cannot safely identify the created node.
/// </summary>
public sealed class StatusUpdateReconciliationRequiredException : InvalidOperationException
{
    public StatusUpdateReconciliationRequiredException(
        string operationId,
        string projectId,
        int sourceIndex,
        string importLogPath)
        : base(
            $"Pending status update operation '{operationId}' for snapshot sequence {sourceIndex} "
            + $"has no deterministically persisted target node ID. Automatic content-based reconciliation "
            + $"is disabled to avoid claiming an unrelated or concurrent update. Inspect target project "
            + $"'{projectId}', then reconcile '{importLogPath}' manually: after confirming the exact target "
            + $"node ID, move sequence '{sourceIndex}' from pendingStatusUpdates to statusUpdates; if the "
            + "create did not occur, remove only that pending entry before rerunning.")
    {
        OperationId = operationId;
        ProjectId = projectId;
        SourceIndex = sourceIndex;
        ImportLogPath = importLogPath;
    }

    public string OperationId { get; }

    public string ProjectId { get; }

    public int SourceIndex { get; }

    public string ImportLogPath { get; }
}
