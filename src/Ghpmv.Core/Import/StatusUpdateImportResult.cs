namespace Ghpmv.Core.Import;

/// <summary>Result of importing Project status update history.</summary>
public sealed record StatusUpdateImportResult
{
    public required int Created { get; init; }

    public required int Resumed { get; init; }

    public required int AlreadyComplete { get; init; }
}
