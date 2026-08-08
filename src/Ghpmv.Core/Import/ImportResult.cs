using System.Collections.ObjectModel;

namespace Ghpmv.Core.Import;

/// <summary>
/// Result of <see cref="ProjectImporter.ImportAsync"/>: the target project identity
/// plus field and View mappings needed by later import phases.
/// </summary>
public sealed record ImportResult
{
    /// <summary>Node ID of the target project.</summary>
    public required string ProjectId { get; init; }

    /// <summary>Project number in the target organization.</summary>
    public required int ProjectNumber { get; init; }

    /// <summary>Web URL of the target project.</summary>
    public required string Url { get; init; }

    /// <summary>Whether this run created, updated, or skipped the target project.</summary>
    public required ProjectImportOutcome Outcome { get; init; }

    /// <summary>True when the project was created by this run.</summary>
    public bool Created => Outcome == ProjectImportOutcome.Created;

    /// <summary>Field name → field node ID.</summary>
    public required IReadOnlyDictionary<string, string> FieldIds { get; init; }

    /// <summary>Field name → (single- or multi-select option name → option ID).</summary>
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> OptionIds { get; init; }

    /// <summary>Field name → (iteration title → iteration ID). Includes completed iterations.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> IterationIds { get; init; }

    /// <summary>Linked organization Issue Field name → Issue Field node ID.</summary>
    public IReadOnlyDictionary<string, string> IssueFieldIds { get; init; } =
        ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Issue Field name → (select option name → option ID).</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> IssueFieldOptionIds { get; init; } =
        ReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>.Empty;

    /// <summary>Source view number → target view number.</summary>
    public IReadOnlyDictionary<int, int> ViewNumbers { get; init; } =
        ReadOnlyDictionary<int, int>.Empty;

    /// <summary>Number of recoverable warnings emitted while importing views through GraphQL.</summary>
    public int ViewWarningCount { get; init; }
}
