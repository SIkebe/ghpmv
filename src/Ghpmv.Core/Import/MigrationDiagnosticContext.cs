using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Import;

/// <summary>Identity metadata only. Never put bodies, field values or request payloads here.</summary>
public sealed record MigrationProjectIdentity
{
    public string? Owner { get; init; }
    public string? OwnerType { get; init; }
    public string? Host { get; init; }
    public int? Number { get; init; }
    public string? Title { get; init; }
    public string? Id { get; init; }
}

public sealed record MigrationElementIdentity
{
    public required string Kind { get; init; }
    public string? Name { get; init; }
    public string? DataType { get; init; }
    public string? SourceId { get; init; }
    public string? TargetId { get; init; }
    public string? Repository { get; init; }
    public string? TargetRepository { get; init; }
    public int? Number { get; init; }
    public int? Position { get; init; }
}

public sealed record MigrationDiagnosticContext
{
    public MigrationProjectIdentity? Source { get; init; }
    public MigrationProjectIdentity? Target { get; init; }
    public string? Stage { get; init; }
    public MigrationElementIdentity? Element { get; init; }
    public MigrationElementIdentity? Item { get; init; }
    public string? Operation { get; init; }
}

/// <summary>
/// Lexically scoped, async-flow-local identity. Failures retain an immutable snapshot,
/// not a mutable "last operation". Attachment never changes the exception type.
/// </summary>
public static class MigrationDiagnostics
{
    private static readonly AsyncLocal<MigrationDiagnosticContext?> Ambient = new();
    private static readonly ConditionalWeakTable<Exception, MigrationDiagnosticContext> Failures = new();

    public static MigrationDiagnosticContext? Current => Ambient.Value;

    public static IDisposable Begin(MigrationDiagnosticContext context)
    {
        var previous = Ambient.Value;
        Ambient.Value = Clean(context);
        return new Scope(previous);
    }

    public static IDisposable ForElement(MigrationElementIdentity element, string operation) =>
        Begin((Current ?? new()) with { Element = element, Operation = operation });

    public static IDisposable ForItem(ItemSnapshot item, string? targetId, string? targetRepository, string operation)
    {
        var identity = new MigrationElementIdentity
        {
            Kind = item.Type,
            Name = item.Type == "DRAFT_ISSUE" ? item.Draft?.Title : null,
            Repository = item.Repository,
            TargetRepository = targetRepository,
            Number = item.Number,
            Position = item.Position,
            TargetId = targetId,
        };
        return Begin((Current ?? new()) with { Item = identity, Element = identity, Operation = operation });
    }

    public static MigrationProjectIdentity Source(ProjectSnapshot snapshot) =>
        (snapshot.Source ?? new()) with { Title = snapshot.Source?.Title ?? snapshot.Project.Title };

    public static IDisposable ForBrowserProject(ProjectSnapshot snapshot, string owner, string ownerType, int number) =>
        Begin((Current ?? new()) with
        {
            Source = Source(snapshot),
            Target = (Current?.Target ?? new()) with { Owner = owner, OwnerType = ownerType, Number = number },
            Stage = "importing-browser-enrichment",
            Item = null,
            Element = new() { Kind = "Project" },
            Operation = "browser-open-project",
        });

    public static MigrationDiagnosticContext? Get(Exception exception)
    {
        if (Failures.TryGetValue(exception, out var context))
        {
            return context;
        }

        return exception.InnerException is { } inner ? Get(inner) : null;
    }

    public static void Attach(Exception exception, MigrationDiagnosticContext? context = null, string? operation = null)
    {
        if (Get(exception) is not null) return;
        context ??= Current;
        if (context is not null)
        {
            var captured = Clean(context with { Operation = operation ?? context.Operation });
            Failures.GetValue(exception, _ => captured);
        }
    }

    public static string Warning(string message) => Current is { } context
        ? $"{Text(message, 4096)} [{string.Join("; ", Lines(context))}]"
        : Text(message, 4096)!;

    // An exception filter captures before lexical scopes unwind, without catching or wrapping.
    public static bool Capture(Exception exception, string? operation = null)
    {
        Attach(exception, operation: operation);
        return false;
    }

    public static string Failure(Exception exception)
    {
        Attach(exception);
        var message = exception switch
        {
            GitHubGraphQLException graphQl => ApiFailureSummary(graphQl),
            HttpRequestException http when GitHubRestClient.GetFailureDiagnostic(http) is not null => http.Message,
            _ => $"{exception.GetType().Name}: migration operation failed.",
        };
        var context = Get(exception);
        return context is null ? message : $"{message} [{string.Join("; ", Lines(context))}]";
    }

    public static string ApiFailureSummary(GitHubGraphQLException exception) =>
        exception is AmbiguousMutationResultException
            ? "Mutation result is ambiguous. Automatic retry was stopped to avoid duplicates."
            : string.Create(CultureInfo.InvariantCulture,
                $"GitHub GraphQL request failed (HTTP {(exception.StatusCode is { } status ? $"{(int)status} {status}" : "unavailable")}, code {GraphQLDiagnosticSanitizer.ErrorType(exception.ErrorType) ?? "unknown"}, request ID {GraphQLDiagnosticSanitizer.RequestId(exception.RequestId) ?? "unavailable"}, retries {exception.RetryCount}).");

    public static IEnumerable<string> Lines(MigrationDiagnosticContext context)
    {
        context = Clean(context);
        if (context.Source is { } source) yield return $"source: {Project(source)}";
        if (context.Target is { } target) yield return $"target: {Project(target)}";
        if (context.Item is { } item && item != context.Element) yield return $"item: {Element(item)}";
        if (context.Element is { } element) yield return $"element: {Element(element)}";
        if (context.Operation is { } operation) yield return $"operation: {operation}";
        if (context.Stage is { } stage) yield return $"stage: {stage}";
    }

    private static string Project(MigrationProjectIdentity identity) =>
        $"{identity.Owner ?? "(owner unknown)"} / Project {identity.Number?.ToString(CultureInfo.InvariantCulture) ?? "(number unknown)"}"
        + (identity.Title is null ? "" : $" \"{identity.Title}\"")
        + (identity.Host is null ? "" : $" @ {identity.Host}")
        + (identity.Id is null ? "" : $" [ID {identity.Id}]");

    private static string Element(MigrationElementIdentity identity) =>
        identity.Kind
        + (identity.Name is null ? "" : $" \"{identity.Name}\"")
        + (identity.Repository is null ? "" : $" {identity.Repository}")
        + (identity.Number is null ? "" : $"#{identity.Number.Value.ToString(CultureInfo.InvariantCulture)}")
        + (identity.DataType is null ? "" : $" ({identity.DataType})")
        + (identity.TargetRepository is null ? "" : $" [target repository {identity.TargetRepository}"
            + (identity.Number is null ? "" : $"#{identity.Number.Value.ToString(CultureInfo.InvariantCulture)}") + "]")
        + (identity.SourceId is null ? "" : $" [source ID {identity.SourceId}]")
        + (identity.TargetId is null ? "" : $" [target ID {identity.TargetId}]")
        + (identity.Position is null ? "" : $" [position {identity.Position.Value.ToString(CultureInfo.InvariantCulture)}]");

    public static string? Text(string? value, int maximumLength = 256)
    {
        if (value is null) return null;
        var result = new StringBuilder();
        foreach (var character in value.Take(maximumLength))
        {
            if (char.IsControl(character) || char.GetUnicodeCategory(character) is UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                result.Append(CultureInfo.InvariantCulture, $"\\u{(int)character:x4}");
            }
            else
            {
                result.Append(character);
            }
        }
        if (value.Length > maximumLength) result.Append('…');
        return result.ToString();
    }

    public static string? SafeUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return null;
        var builder = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" };
        return Text(builder.Uri.GetLeftPart(UriPartial.Path));
    }

    private static MigrationDiagnosticContext Clean(MigrationDiagnosticContext value) => value with
    {
        Source = Clean(value.Source),
        Target = Clean(value.Target),
        Element = Clean(value.Element),
        Item = Clean(value.Item),
        Stage = Text(value.Stage),
        Operation = Text(value.Operation),
    };

    private static MigrationProjectIdentity? Clean(MigrationProjectIdentity? value) => value is null ? null : value with
    {
        Owner = Text(value.Owner),
        OwnerType = Text(value.OwnerType),
        Title = Text(value.Title),
        Id = Text(value.Id),
        Host = Host(value.Host),
    };

    private static MigrationElementIdentity? Clean(MigrationElementIdentity? value) => value is null ? null : value with
    {
        Kind = Text(value.Kind)!,
        Name = Text(value.Name),
        DataType = Text(value.DataType),
        SourceId = Text(value.SourceId),
        TargetId = Text(value.TargetId),
        Repository = Text(value.Repository),
        TargetRepository = Text(value.TargetRepository),
    };

    private sealed class Scope(MigrationDiagnosticContext? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }

    private static string? Host(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? Text(uri.Host)
            : value is not null && Uri.CheckHostName(value) != UriHostNameType.Unknown ? Text(value) : null;
}
