using System.Globalization;
using System.Text;

namespace Ghpmv.Core.Fixtures;

/// <summary>Serializes fixture UI writes and publishes their completion state atomically.</summary>
public static class FixtureUiOperation
{
    private const string LockFileName = "fixture-ui-operation.lock";

    public static FileStream AcquireLock(string operationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationDirectory);
        Directory.CreateDirectory(operationDirectory);
        try
        {
            return new FileStream(
                Path.Combine(operationDirectory, LockFileName),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                $"Another fixture UI operation is already using '{operationDirectory}'.",
                exception);
        }
    }

    public static bool IsCompleted(
        string completionPath,
        int projectNumber,
        string snapshotFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(completionPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotFingerprint);
        if (!File.Exists(completionPath))
        {
            return false;
        }

        var lines = File.ReadAllLines(completionPath, Encoding.UTF8);
        return lines.Length == 2
            && string.Equals(
                lines[0],
                projectNumber.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            && string.Equals(lines[1], snapshotFingerprint, StringComparison.Ordinal);
    }

    public static async Task MarkCompletedAsync(
        string completionPath,
        int projectNumber,
        string snapshotFingerprint,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(completionPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotFingerprint);
        var directory = Path.GetDirectoryName(completionPath)
            ?? throw new ArgumentException("The completion path must include a directory.", nameof(completionPath));
        Directory.CreateDirectory(directory);
        var temporaryPath = completionPath + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                projectNumber.ToString(CultureInfo.InvariantCulture)
                    + Environment.NewLine
                    + snapshotFingerprint,
                Encoding.UTF8,
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, completionPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
