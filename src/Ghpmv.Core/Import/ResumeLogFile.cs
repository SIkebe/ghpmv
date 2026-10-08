using System.Runtime.ExceptionServices;

namespace Ghpmv.Core.Import;

internal static class ResumeLogFile
{
    internal static async Task<string> SaveAsync(
        string directory,
        string fileName,
        string? backupFileName,
        Func<Stream, CancellationToken, Task> writeAsync,
        CancellationToken cancellationToken,
        Action<string>? deleteTemporaryFile = null)
    {
        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backupPath = backupFileName is null
            ? null
            : Path.GetFullPath(Path.Combine(directory, backupFileName));
        var stage = "creating-directory";
        Exception? failure = null;
        FileStream? stream = null;
        var temporaryFileCreated = false;

        Exception Capture(Exception exception, string failedStage)
        {
            ResumeLogSaveFailure.Attach(exception, new()
            {
                Stage = failedStage,
                LogPath = path,
                DirectoryPath = failedStage == "creating-directory" ? Path.GetDirectoryName(path) : null,
                TemporaryPath = failedStage == "creating-directory" ? null : temporaryPath,
                BackupPath = failedStage == "replacing-log" ? backupPath : null,
                ExecutingProcessId = Environment.ProcessId,
            });
            return exception;
        }

        try
        {
            Directory.CreateDirectory(directory);
            stage = "opening-temporary-file";
            stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            temporaryFileCreated = true;
            stage = "writing-temporary-file";
            await writeAsync(stream, cancellationToken).ConfigureAwait(false);
            stage = "flushing-temporary-file";
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = Capture(exception, stage);
        }

        if (stream is not null)
        {
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = Combine(failure, Capture(exception, "closing-temporary-file"));
            }
        }

        if (failure is null)
        {
            try
            {
                if (backupPath is not null && File.Exists(path))
                {
                    stage = "replacing-log";
                    File.Replace(temporaryPath, path, backupPath);
                }
                else
                {
                    stage = "moving-log";
                    File.Move(temporaryPath, path, overwrite: backupPath is null);
                }
            }
            catch (Exception exception)
            {
                failure = Capture(exception, stage);
            }
        }

        if (temporaryFileCreated)
        {
            try
            {
                (deleteTemporaryFile ?? File.Delete)(temporaryPath);
            }
            catch (Exception exception)
            {
                failure = Combine(failure, Capture(exception, "deleting-temporary-file"));
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return path;
    }

    private static Exception Combine(Exception? primary, Exception additional) =>
        primary is null
            ? additional
            : new AggregateException("Resume log saving and cleanup failed.", primary, additional);
}
