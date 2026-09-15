namespace Ghpmv.Cli;

using System.Runtime.ExceptionServices;

internal sealed class ImportFailureFinalizer(
    ImportFailureDiagnostics diagnostics,
    string directory,
    Action<string>? writeError = null,
    Func<Exception, CancellationToken, Task<string>>? saveFailureAsync = null)
{
    private readonly Action<string> _writeError = writeError ?? Console.Error.WriteLine;
    private readonly Func<Exception, CancellationToken, Task<string>> _saveFailureAsync =
        saveFailureAsync ?? ((exception, cancellationToken) =>
            diagnostics.SaveFailureAsync(directory, exception, cancellationToken));

    public async Task<Exception?> CompleteAsync(
        Exception? importFailure,
        Func<Task>? restoreTemplateAsync,
        Func<ValueTask>? disposeBrowserAsync)
    {
        if (restoreTemplateAsync is not null)
        {
            try
            {
                await restoreTemplateAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                diagnostics.RecordCleanupFailure("restoring-template-state", exception);
                diagnostics.WriteProgress(
                    $"error: failed to restore the target project's template state: {exception.Message}",
                    $"error: failed to restore the target project's template state: {ImportFailureDiagnostics.FormatExceptionForReport(exception)}");
                importFailure = Combine(
                    importFailure,
                    exception,
                    "Import failed and the target project's template state could not be restored.");
            }
        }

        if (disposeBrowserAsync is not null)
        {
            try
            {
                await disposeBrowserAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                diagnostics.RecordCleanupFailure("disposing-browser-session", exception);
                diagnostics.WriteProgress(
                    $"error: failed to close the browser session: {exception.Message}",
                    $"error: failed to close the browser session: {ImportFailureDiagnostics.FormatExceptionForReport(exception)}");
                importFailure = Combine(
                    importFailure,
                    exception,
                    "Import failed and the browser session could not be closed.");
            }
        }

        if (importFailure is not null && Directory.Exists(directory))
        {
            try
            {
                var diagnosticPath = await _saveFailureAsync(
                    importFailure,
                    CancellationToken.None).ConfigureAwait(false);
                _writeError($"Detailed error log: {diagnosticPath}");
            }
            catch (Exception diagnosticException)
            {
                _writeError(
                    $"warning: failed to write {ImportFailureDiagnostics.FileName}: {diagnosticException.Message}");
            }
        }

        return importFailure;
    }

    public static void ThrowIfCleanupOnlyFailure(
        Exception? failureBeforeCleanup,
        Exception? failureAfterCleanup)
    {
        if (failureBeforeCleanup is null && failureAfterCleanup is not null)
        {
            ExceptionDispatchInfo.Capture(failureAfterCleanup).Throw();
        }
    }

    private static Exception Combine(
        Exception? current,
        Exception additional,
        string message) =>
        current is null
            ? additional
            : new AggregateException(message, current, additional);
}
