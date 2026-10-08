using System.Runtime.CompilerServices;

namespace Ghpmv.Core.Import;

/// <summary>Operation context, not evidence of which file or process held a lock.</summary>
public sealed record ResumeLogSaveFailure
{
    private static readonly ConditionalWeakTable<Exception, ResumeLogSaveFailure> Failures = new();

    public required string Stage { get; init; }
    public required string LogPath { get; init; }
    public string? DirectoryPath { get; init; }
    public string? TemporaryPath { get; init; }
    public string? BackupPath { get; init; }
    public required int ExecutingProcessId { get; init; }

    public static ResumeLogSaveFailure? Get(Exception exception) =>
        Failures.TryGetValue(exception, out var failure) ? failure : null;

    internal static void Attach(Exception exception, ResumeLogSaveFailure failure) =>
        Failures.GetValue(exception, _ => failure);
}
