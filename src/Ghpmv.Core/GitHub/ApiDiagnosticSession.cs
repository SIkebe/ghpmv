using System.Runtime.CompilerServices;

namespace Ghpmv.Core.GitHub;

/// <summary>Explicitly shared by all API clients in one invocation, independently of async scopes.</summary>
public sealed class ApiDiagnosticSession
{
    private static readonly ConditionalWeakTable<Exception, ApiRequestAttempt> Attempts = new();

    public string RunId { get; } = Guid.NewGuid().ToString("N");

    internal SensitiveApiDiagnostics? SensitiveDiagnostics { get; set; }

    /// <summary>The absolute path of a file actually created by this invocation, even if incomplete.</summary>
    public string? SensitiveDiagnosticsFile => SensitiveDiagnostics?.CreatedFilePath;

    /// <summary>disabled, not-created, available, partial, or unavailable.</summary>
    public string SensitiveDiagnosticsState => SensitiveDiagnostics?.CaptureState ?? "disabled";

    internal ApiRequestAttempt BeginAttempt() => new(RunId, SensitiveDiagnostics is not null);

    public static ApiRequestAttempt? GetAttempt(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Attempts.TryGetValue(exception, out var attempt) ? attempt
            : exception is not AggregateException && exception.InnerException is { } inner ? GetAttempt(inner)
            : null;
    }

    internal static T Attach<T>(T exception, ApiRequestAttempt attempt) where T : Exception
    {
        Attempts.TryAdd(exception, attempt);
        return exception;
    }
}

/// <summary>Local identity of one application-level HTTP send; contains no request or response content.</summary>
public sealed class ApiRequestAttempt
{
    internal ApiRequestAttempt(string runId, bool captureEnabled)
    {
        RunId = runId;
        SensitiveResponseCapture = captureEnabled ? "not-captured" : "disabled";
    }

    public string RunId { get; }
    public string AttemptId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>disabled, not-captured, captured (flushed record), or failed (possibly partial record).</summary>
    public string SensitiveResponseCapture { get; internal set; }
}
