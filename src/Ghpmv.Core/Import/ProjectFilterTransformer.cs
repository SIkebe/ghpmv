using System.Collections.ObjectModel;
using System.Text;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Import;

/// <summary>
/// Structurally maps identity-bearing GitHub Projects filter qualifier values while
/// preserving all other text exactly.
/// </summary>
public static class ProjectFilterTransformer
{
    private static readonly HashSet<string> UserQualifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "assignee",
        "author",
    };

    private static readonly HashSet<string> PassthroughQualifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "is",
        "iteration",
        "label",
        "milestone",
        "no",
        "reason",
        "status",
        "type",
        "updated",
    };

    /// <summary>Transforms supported qualifier values using source-to-target mappings.</summary>
    public static FilterTransformResult Transform(
        string filter,
        IReadOnlyDictionary<string, string>? userMapping = null,
        IReadOnlyDictionary<string, string>? repositoryMapping = null,
        IReadOnlyDictionary<string, string>? organizationMapping = null,
        IReadOnlySet<string>? projectFieldQualifiers = null)
    {
        ArgumentNullException.ThrowIfNull(filter);
        userMapping ??= ReadOnlyDictionary<string, string>.Empty;
        repositoryMapping ??= ReadOnlyDictionary<string, string>.Empty;
        organizationMapping = MergeOrganizationMappings(repositoryMapping, organizationMapping);

        var changes = new List<FilterTokenChange>();
        var unresolved = new List<FilterIdentifier>();
        var unchanged = new List<FilterIdentifier>();
        var unsupported = new List<FilterIdentifier>();
        var builder = new StringBuilder(filter.Length);
        var position = 0;
        while (position < filter.Length)
        {
            if (filter[position] == '"')
            {
                var literalEnd = FindQuotedValueEnd(filter, position);
                builder.Append(filter, position, literalEnd - position);
                position = literalEnd;
                continue;
            }

            if (!IsQualifierBoundary(filter, position)
                || !IsQualifierStart(filter[position])
                || !TryReadQualifierValue(filter, position, out var qualifier, out var rawValue, out var tokenEnd))
            {
                builder.Append(filter[position]);
                position++;
                continue;
            }

            builder.Append(qualifier).Append(':');
            var quoted = rawValue.Length >= 2 && rawValue[0] == '"' && rawValue[^1] == '"';
            var value = quoted ? rawValue[1..^1] : rawValue;
            var mapping = MappingFor(qualifier, userMapping, repositoryMapping, organizationMapping);

            if (mapping is null)
            {
                builder.Append(rawValue);
                if (!PassthroughQualifiers.Contains(qualifier)
                    && projectFieldQualifiers?.Contains(qualifier) != true)
                {
                    unsupported.Add(new FilterIdentifier(qualifier, value));
                }
                else
                {
                    unchanged.Add(new FilterIdentifier(qualifier, value));
                }
            }
            else
            {
                if (quoted)
                {
                    builder.Append('"');
                }

                var values = value.Split(',');
                for (var index = 0; index < values.Length; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(',');
                    }

                    var item = values[index];
                    var identity = item.Trim();
                    var leadingWhitespace = item[..(item.Length - item.TrimStart().Length)];
                    var trailingWhitespace = item[item.TrimEnd().Length..];
                    var mappingIdentity = MappingIdentity(qualifier, identity, out var mappedPrefix);
                    if (mapping.TryGetValue(mappingIdentity, out var mapped))
                    {
                        var mappedValue = string.Concat(mappedPrefix, mapped);
                        builder.Append(leadingWhitespace).Append(mappedValue).Append(trailingWhitespace);
                        if (!string.Equals(identity, mappedValue, StringComparison.Ordinal))
                        {
                            changes.Add(new FilterTokenChange(qualifier, identity, mappedValue));
                        }
                        else
                        {
                            unchanged.Add(new FilterIdentifier(qualifier, identity));
                        }
                    }
                    else
                    {
                        builder.Append(item);
                        if (IsMappingRequired(qualifier, identity))
                        {
                            unresolved.Add(new FilterIdentifier(qualifier, mappingIdentity));
                        }
                        else if (identity.Length > 0)
                        {
                            unchanged.Add(new FilterIdentifier(qualifier, identity));
                        }
                    }
                }

                if (quoted)
                {
                    builder.Append('"');
                }
            }

            position = tokenEnd;
        }

        return new FilterTransformResult(filter, builder.ToString(), changes, unresolved, unchanged, unsupported);
    }

    /// <summary>Applies filter mappings to all Views and Workflows in a snapshot.</summary>
    public static ProjectSnapshot TransformSnapshot(
        ProjectSnapshot snapshot,
        IReadOnlyDictionary<string, string>? userMapping = null,
        IReadOnlyDictionary<string, string>? repositoryMapping = null,
        IReadOnlyDictionary<string, string>? organizationMapping = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var projectFieldQualifiers = BuildProjectFieldQualifiers(snapshot.Fields);
        return snapshot with
        {
            Views = snapshot.Views.Select(view => view.Filter is null
                ? view
                : view with
                {
                    Filter = Transform(
                        view.Filter,
                        userMapping,
                        repositoryMapping,
                        organizationMapping,
                        projectFieldQualifiers).Transformed,
                }).ToList(),
            Workflows = snapshot.Workflows.Select(workflow => workflow.Ui?.Filter is null
                ? workflow
                : workflow with
                {
                    Ui = workflow.Ui with
                    {
                        Filter = Transform(
                            workflow.Ui.Filter,
                            userMapping,
                            repositoryMapping,
                            organizationMapping,
                            projectFieldQualifiers).Transformed,
                    },
                }).ToList(),
        };
    }

    /// <summary>Returns all View and Workflow filter transformation results with their locations.</summary>
    public static IReadOnlyList<SnapshotFilterTransform> AnalyzeSnapshot(
        ProjectSnapshot snapshot,
        IReadOnlyDictionary<string, string>? userMapping = null,
        IReadOnlyDictionary<string, string>? repositoryMapping = null,
        IReadOnlyDictionary<string, string>? organizationMapping = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var projectFieldQualifiers = BuildProjectFieldQualifiers(snapshot.Fields);
        var results = new List<SnapshotFilterTransform>();
        results.AddRange(snapshot.Views.Where(view => view.Filter is not null).Select(view =>
            new SnapshotFilterTransform(
                $"view '{view.Name}'",
                Transform(
                    view.Filter!,
                    userMapping,
                    repositoryMapping,
                    organizationMapping,
                    projectFieldQualifiers))));
        results.AddRange(snapshot.Workflows.Where(workflow => workflow.Ui?.Filter is not null).Select(workflow =>
            new SnapshotFilterTransform(
                $"workflow '{workflow.Name}'",
                Transform(
                    workflow.Ui!.Filter!,
                    userMapping,
                    repositoryMapping,
                    organizationMapping,
                    projectFieldQualifiers))));
        return results;
    }

    /// <summary>
    /// Builds the filter qualifier names GitHub derives from simple value-only custom field names.
    /// Built-in identity fields require mapping support and names containing other punctuation
    /// remain unsupported rather than being guessed.
    /// </summary>
    public static IReadOnlySet<string> BuildProjectFieldQualifiers(IEnumerable<FieldSnapshot> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var qualifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields.Where(field =>
            field.DataType is "TEXT" or "NUMBER" or "DATE" or "SINGLE_SELECT" or "ITERATION" or "MULTI_SELECT"))
        {
            if (TryBuildProjectFieldQualifier(field.Name, out var qualifier))
            {
                qualifiers.Add(qualifier);
            }
        }

        return qualifiers;
    }

    public static bool TryBuildProjectFieldQualifier(string fieldName, out string qualifier)
    {
        ArgumentNullException.ThrowIfNull(fieldName);
        var builder = new StringBuilder(fieldName.Length);
        var pendingSeparator = false;
        foreach (var character in fieldName)
        {
            if (character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' || char.IsDigit(character))
            {
                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(char.ToLowerInvariant(character));
                pendingSeparator = false;
            }
            else if (char.IsWhiteSpace(character) || character == '-')
            {
                pendingSeparator = true;
            }
            else if (character == '_')
            {
                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(character);
                pendingSeparator = false;
            }
            else
            {
                qualifier = string.Empty;
                return false;
            }
        }

        qualifier = builder.ToString();
        return qualifier.Length > 0;
    }

    /// <summary>
    /// Merges the negative custom-field qualifier GitHub uses to persist visible Board columns.
    /// </summary>
    public static string? ApplyBoardVisibilityFilter(
        ViewSnapshot view,
        IReadOnlyList<FieldSnapshot> fields)
        => ApplyBoardVisibilityFilter(view, fields, view.Filter);

    /// <summary>
    /// Merges Board visibility into an explicitly supplied target filter.
    /// </summary>
    public static string? ApplyBoardVisibilityFilter(
        ViewSnapshot view,
        IReadOnlyList<FieldSnapshot> fields,
        string? baseFilter)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(fields);

        if (view.Ui?.VisibleColumns is null)
        {
            return baseFilter;
        }

        if (!string.Equals(view.Layout, "BOARD_LAYOUT", StringComparison.Ordinal)
            || view.VerticalGroupByFields.Count != 1)
        {
            throw new InvalidOperationException(
                $"view '{view.Name}': visible Board columns require a Board layout with exactly one column-by field");
        }

        var fieldName = view.VerticalGroupByFields[0];
        var matchingFields = fields
            .Where(field => string.Equals(field.Name, fieldName, StringComparison.Ordinal))
            .ToArray();
        if (matchingFields.Length != 1)
        {
            throw new InvalidOperationException(
                $"view '{view.Name}': visible Board column field '{fieldName}' does not uniquely exist in the snapshot");
        }

        if (!TryBuildProjectFieldQualifier(fieldName, out var qualifier))
        {
            throw new InvalidOperationException(
                $"view '{view.Name}': column-by field '{fieldName}' cannot be represented as a Project filter qualifier");
        }

        var field = matchingFields[0];
        var allValues = GetBoardColumnValues(view, field);
        var visibleValueList = view.Ui.VisibleColumns.Select(column =>
        {
            if (!string.Equals(column.FieldName, fieldName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"view '{view.Name}': visible Board column '{column.FieldName}' does not use column-by field '{fieldName}'");
            }

            return field.DataType switch
            {
                "SINGLE_SELECT" when column.SingleSelectOptionName is not null
                    && column.IterationTitle is null => column.SingleSelectOptionName,
                "ITERATION" when column.IterationTitle is not null
                    && column.SingleSelectOptionName is null => column.IterationTitle,
                _ => throw new InvalidOperationException(
                    $"view '{view.Name}': visible Board column has an invalid identity for field '{fieldName}' ({field.DataType})"),
            };
        }).ToArray();
        var visibleValues = visibleValueList.ToHashSet(StringComparer.Ordinal);
        if (visibleValues.Count != visibleValueList.Length)
        {
            throw new InvalidOperationException(
                $"view '{view.Name}': visible Board columns contain duplicate logical values for field '{fieldName}'");
        }

        var unknownVisibleValues = visibleValues.Except(allValues, StringComparer.Ordinal).ToArray();
        if (unknownVisibleValues.Length > 0)
        {
            throw new InvalidOperationException(
                $"view '{view.Name}': visible Board columns are missing from field '{fieldName}': {string.Join(", ", unknownVisibleValues)}");
        }

        var filterWithoutVisibility = RemoveNegativeQualifier(baseFilter, qualifier);
        var hiddenValues = allValues.Where(value => !visibleValues.Contains(value)).ToArray();
        if (hiddenValues.Length == 0)
        {
            return filterWithoutVisibility;
        }

        var visibilityFilter = $"-{qualifier}:{string.Join(",", hiddenValues.Select(FormatFilterValue))}";
        return string.IsNullOrWhiteSpace(filterWithoutVisibility)
            ? visibilityFilter
            : $"{filterWithoutVisibility} {visibilityFilter}";
    }

    /// <summary>Removes complete top-level negative qualifier tokens, including comma-separated quoted values.</summary>
    public static string? RemoveNegativeQualifier(string? filter, string qualifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifier);
        if (string.IsNullOrWhiteSpace(filter))
        {
            return null;
        }

        var matches = new List<(int Start, int End)>();
        var position = 0;
        while (position < filter.Length)
        {
            if (filter[position] == '"')
            {
                position = FindQuotedValueEnd(filter, position);
                continue;
            }

            var qualifierStart = position + 1;
            if (filter[position] != '-'
                || qualifierStart >= filter.Length
                || !IsQualifierBoundary(filter, qualifierStart)
                || !IsQualifierStart(filter[qualifierStart])
                || !TryReadQualifierValue(
                    filter,
                    qualifierStart,
                    out var candidate,
                    out _,
                    out var tokenEnd)
                || !string.Equals(candidate, qualifier, StringComparison.OrdinalIgnoreCase))
            {
                position++;
                continue;
            }

            while (tokenEnd < filter.Length && filter[tokenEnd] == ',')
            {
                var valueStart = tokenEnd + 1;
                if (valueStart >= filter.Length
                    || char.IsWhiteSpace(filter[valueStart])
                    || filter[valueStart] is '(' or ')')
                {
                    break;
                }

                var valueEnd = filter[valueStart] == '"'
                    ? FindQuotedValueEnd(filter, valueStart)
                    : FindUnquotedValueEnd(filter, valueStart);
                if (valueEnd == valueStart)
                {
                    break;
                }

                tokenEnd = valueEnd;
            }

            matches.Add((position, tokenEnd));
            position = tokenEnd;
        }

        if (matches.Count == 0)
        {
            return filter;
        }

        var removalSpans = new List<(int Start, int End)>();
        foreach (var match in matches)
        {
            var removeStart = match.Start;
            var removeEnd = match.End;
            var previousContent = removeStart - 1;
            while (previousContent >= 0 && char.IsWhiteSpace(filter[previousContent]))
            {
                previousContent--;
            }
            var nextContent = removeEnd;
            while (nextContent < filter.Length && char.IsWhiteSpace(filter[nextContent]))
            {
                nextContent++;
            }
            if (previousContent >= 0
                && filter[previousContent] == '('
                && nextContent < filter.Length
                && filter[nextContent] == ')')
            {
                removeStart = previousContent;
                removeEnd = nextContent + 1;
            }

            if (removalSpans.Count > 0
                && IsOnlyWhitespace(filter, removalSpans[^1].End, removeStart))
            {
                removalSpans[^1] = (removalSpans[^1].Start, removeEnd);
            }
            else
            {
                removalSpans.Add((removeStart, removeEnd));
            }
        }

        var builder = new StringBuilder(filter);
        for (var index = removalSpans.Count - 1; index >= 0; index--)
        {
            var (removeStart, removeEnd) = removalSpans[index];

            while (removeStart > 0 && char.IsWhiteSpace(filter[removeStart - 1]))
            {
                removeStart--;
            }

            while (removeEnd < filter.Length && char.IsWhiteSpace(filter[removeEnd]))
            {
                removeEnd++;
            }

            var replacement = removeStart > 0 && removeEnd < filter.Length ? " " : string.Empty;
            builder.Remove(removeStart, removeEnd - removeStart);
            builder.Insert(removeStart, replacement);
        }

        var normalized = builder.ToString();
        return normalized.Length == 0 ? null : normalized;
    }

    private static bool IsOnlyWhitespace(string value, int start, int end)
    {
        if (start > end)
        {
            return true;
        }

        for (var index = start; index < end; index++)
        {
            if (!char.IsWhiteSpace(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static string[] GetBoardColumnValues(ViewSnapshot view, FieldSnapshot field)
        => field.DataType switch
        {
            "SINGLE_SELECT" when field.Options is not null
                => field.Options.Select(option => option.Name).Distinct(StringComparer.Ordinal).ToArray(),
            "ITERATION" when field.IterationConfiguration is not null
                => field.IterationConfiguration.CompletedIterations
                    .Concat(field.IterationConfiguration.Iterations)
                    .Select(iteration => iteration.Title)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
            _ => throw new InvalidOperationException(
                $"view '{view.Name}': Board column visibility is unsupported for field '{field.Name}' ({field.DataType})"),
        };

    private static string FormatFilterValue(string value)
    {
        if (value.Length > 0 && value.All(character =>
                char.IsLetterOrDigit(character) || character is '_' or '-' or '.'))
        {
            return value;
        }

        return $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    /// <summary>Returns Auto-add repository mapping results with their workflow locations.</summary>
    public static IReadOnlyList<SnapshotRepositoryResolution> AnalyzeAutoAddRepositories(
        ProjectSnapshot snapshot,
        IReadOnlyDictionary<string, string> repositoryMapping)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(repositoryMapping);
        return snapshot.Workflows
            .Where(workflow => workflow.Ui?.Repository is { Length: > 0 })
            .Select(workflow => new SnapshotRepositoryResolution(
                $"workflow '{workflow.Name}'",
                ResolveRepository(workflow.Ui!.Repository!, repositoryMapping)))
            .ToList();
    }

    /// <summary>
    /// Infers source-to-target organization mappings only when every repository mapping
    /// for a source owner points to the same target owner.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildOrganizationMapping(
        IReadOnlyDictionary<string, string> repositoryMapping)
    {
        ArgumentNullException.ThrowIfNull(repositoryMapping);
        var candidates = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, target) in repositoryMapping)
        {
            if (!TrySplitRepository(source, out var sourceOwner, out _)
                || !TrySplitRepository(target, out var targetOwner, out _))
            {
                continue;
            }

            if (!candidates.TryGetValue(sourceOwner, out var targetOwners))
            {
                targetOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                candidates[sourceOwner] = targetOwners;
            }

            targetOwners.Add(targetOwner);
        }

        return candidates
            .Where(pair => pair.Value.Count == 1)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Single(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Resolves an Auto-add repository short name without guessing ambiguous targets.</summary>
    public static RepositoryResolution ResolveRepository(
        string sourceRepository,
        IReadOnlyDictionary<string, string> repositoryMapping)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRepository);
        ArgumentNullException.ThrowIfNull(repositoryMapping);

        var targets = repositoryMapping
            .Where(pair => string.Equals(ShortName(pair.Key), sourceRepository, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return targets.Count switch
        {
            0 => new RepositoryResolution(sourceRepository, null, RepositoryResolutionStatus.Unmapped),
            1 => new RepositoryResolution(sourceRepository, ShortName(targets[0]), RepositoryResolutionStatus.Mapped),
            _ => new RepositoryResolution(sourceRepository, null, RepositoryResolutionStatus.Ambiguous),
        };

        static string ShortName(string repository)
        {
            var separator = repository.LastIndexOf('/');
            return separator < 0 ? repository : repository[(separator + 1)..];
        }
    }

    private static Dictionary<string, string> MergeOrganizationMappings(
        IReadOnlyDictionary<string, string> repositoryMapping,
        IReadOnlyDictionary<string, string>? explicitMapping)
    {
        var result = new Dictionary<string, string>(
            BuildOrganizationMapping(repositoryMapping),
            StringComparer.OrdinalIgnoreCase);
        foreach (var (source, target) in explicitMapping ?? ReadOnlyDictionary<string, string>.Empty)
        {
            result[source] = target;
        }

        return result;
    }

    /// <summary>Enumerates supported identity-bearing tokens without changing the filter.</summary>
    public static IReadOnlyList<FilterIdentifier> ExtractIdentifiers(string filter)
        => Transform(filter).Unresolved;

    private static bool TryReadQualifierValue(
        string filter,
        int start,
        out string qualifier,
        out string rawValue,
        out int tokenEnd)
    {
        var separator = start + 1;
        while (separator < filter.Length && IsQualifierPart(filter[separator]))
        {
            separator++;
        }

        if (separator >= filter.Length || filter[separator] != ':' || separator + 1 >= filter.Length)
        {
            qualifier = string.Empty;
            rawValue = string.Empty;
            tokenEnd = start;
            return false;
        }

        var valueStart = separator + 1;
        tokenEnd = filter[valueStart] == '"'
            ? FindQuotedValueEnd(filter, valueStart)
            : FindUnquotedValueEnd(filter, valueStart);
        qualifier = filter[start..separator];
        rawValue = filter[valueStart..tokenEnd];
        return rawValue.Length > 0;
    }

    private static int FindQuotedValueEnd(string filter, int quoteStart)
    {
        var escaped = false;
        for (var index = quoteStart + 1; index < filter.Length; index++)
        {
            if (!escaped && filter[index] == '"')
            {
                return index + 1;
            }

            escaped = !escaped && filter[index] == '\\';
            if (filter[index] != '\\')
            {
                escaped = false;
            }
        }

        return filter.Length;
    }

    private static int FindUnquotedValueEnd(string filter, int valueStart)
    {
        var index = valueStart;
        while (index < filter.Length
            && !char.IsWhiteSpace(filter[index])
            && filter[index] is not '(' and not ')')
        {
            index++;
        }

        return index;
    }

    private static bool IsQualifierBoundary(string filter, int index)
    {
        if (index == 0 || char.IsWhiteSpace(filter[index - 1]) || filter[index - 1] == '(')
        {
            return true;
        }

        return filter[index - 1] == '-'
            && (index == 1 || char.IsWhiteSpace(filter[index - 2]) || filter[index - 2] == '(');
    }

    private static bool IsQualifierStart(char value)
        => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsQualifierPart(char value)
        => IsQualifierStart(value) || char.IsDigit(value) || value is '_' or '-';

    private static IReadOnlyDictionary<string, string>? MappingFor(
        string qualifier,
        IReadOnlyDictionary<string, string> userMapping,
        IReadOnlyDictionary<string, string> repositoryMapping,
        IReadOnlyDictionary<string, string> organizationMapping)
    {
        if (UserQualifiers.Contains(qualifier))
        {
            return userMapping;
        }

        if (string.Equals(qualifier, "repo", StringComparison.OrdinalIgnoreCase))
        {
            return repositoryMapping;
        }

        return string.Equals(qualifier, "org", StringComparison.OrdinalIgnoreCase)
            ? organizationMapping
            : null;
    }

    private static bool TrySplitRepository(string repository, out string owner, out string name)
    {
        var separator = repository.IndexOf('/');
        if (separator <= 0 || separator == repository.Length - 1)
        {
            owner = string.Empty;
            name = string.Empty;
            return false;
        }

        owner = repository[..separator];
        name = repository[(separator + 1)..];
        return true;
    }

    private static bool IsMappingRequired(string qualifier, string value)
        => value.Length > 0
            && (!UserQualifiers.Contains(qualifier)
                || !string.Equals(value, "@me", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(value, "none", StringComparison.OrdinalIgnoreCase));

    private static string MappingIdentity(string qualifier, string value, out string prefix)
    {
        if (UserQualifiers.Contains(qualifier)
            && value.Length > 1
            && value[0] == '@'
            && !string.Equals(value, "@me", StringComparison.OrdinalIgnoreCase))
        {
            prefix = "@";
            return value[1..];
        }

        prefix = string.Empty;
        return value;
    }

}

public sealed record FilterIdentifier(string Qualifier, string Value);

public sealed record FilterTokenChange(string Qualifier, string Source, string Target);

public sealed record FilterTransformResult(
    string Original,
    string Transformed,
    IReadOnlyList<FilterTokenChange> Changes,
    IReadOnlyList<FilterIdentifier> Unresolved,
    IReadOnlyList<FilterIdentifier> Unchanged,
    IReadOnlyList<FilterIdentifier> Unsupported);

public sealed record SnapshotFilterTransform(string Location, FilterTransformResult Result);

public enum RepositoryResolutionStatus
{
    Mapped,
    Unmapped,
    Ambiguous,
}

public sealed record RepositoryResolution(
    string Source,
    string? Target,
    RepositoryResolutionStatus Status);

public sealed record SnapshotRepositoryResolution(string Location, RepositoryResolution Resolution);
