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

    public static bool IsCompleted(string completionPath, int projectNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(completionPath);
        if (!File.Exists(completionPath))
        {
            return false;
        }

        var expected = projectNumber.ToString(CultureInfo.InvariantCulture);
        return string.Equals(File.ReadAllText(completionPath, Encoding.UTF8).Trim(), expected, StringComparison.Ordinal);
    }

    public static async Task MarkCompletedAsync(
        string completionPath,
        int projectNumber,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(completionPath);
        var directory = Path.GetDirectoryName(completionPath)
            ?? throw new ArgumentException("The completion path must include a directory.", nameof(completionPath));
        Directory.CreateDirectory(directory);
        var temporaryPath = completionPath + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                projectNumber.ToString(CultureInfo.InvariantCulture),
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
