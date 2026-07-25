using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Export;

/// <summary>
/// Complete project field data supplied by a source outside the public Projects GraphQL
/// field connection. Linkage is carried per entry so an ordinary field and an organization
/// Issue Field may share a name.
/// </summary>
public sealed record ProjectFieldCatalog
{
    public required IReadOnlyList<ProjectFieldCatalogEntry> Entries { get; init; }

    public IReadOnlyList<FieldSnapshot> Fields => [.. Entries.Select(entry => entry.Field)];
}

public sealed record ProjectFieldCatalogEntry(FieldSnapshot Field, bool IsIssueField);
