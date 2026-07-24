using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Export;

/// <summary>
/// Complete project field data supplied by a source outside the public Projects GraphQL
/// field connection. <see cref="IssueFieldNames"/> identifies fields linked from the
/// organization Issue Field catalog.
/// </summary>
public sealed record ProjectFieldCatalog
{
    public required IReadOnlyList<FieldSnapshot> Fields { get; init; }

    public required IReadOnlySet<string> IssueFieldNames { get; init; }
}
