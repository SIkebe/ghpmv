namespace Ghpmv.Cli;

using Ghpmv.Core.Import;

internal static class HtmlReportFile
{
    public static string DefaultOutputPath(string inputPath) => Path.ChangeExtension(inputPath, ".html");

    public static async Task<string> WriteAsync(
        string inputPath,
        string? outputPath,
        string language,
        bool overwrite,
        Action<string> writeWarning,
        CancellationToken cancellationToken,
        string logName = ImportLog.FileName)
    {
        var input = Path.GetFullPath(inputPath);
        var isDatabase = string.Equals(Path.GetFileName(input), ResumeStateStore.FileName, StringComparison.OrdinalIgnoreCase);
        var output = Path.GetFullPath(outputPath ?? (isDatabase
            ? DefaultOutputPath(Path.Combine(Path.GetDirectoryName(input)!, logName))
            : DefaultOutputPath(input)));
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("--output must differ from --input.");
        }

        var json = isDatabase
            ? ResumeStateStore.ReadSnapshot(Path.GetDirectoryName(input)!, logName, cancellationToken)?.ToDiagnosticJson()
                ?? throw new InvalidDataException($"The resume database has no '{logName}' state.")
            : await File.ReadAllTextAsync(input, cancellationToken).ConfigureAwait(false);
        var html = HtmlReportGenerator.Generate(json, isDatabase ? ResumeStateStore.FileName : Path.GetFileName(input), language);
        await WriteTextAsync(output, html, overwrite, writeWarning, cancellationToken).ConfigureAwait(false);
        return output;
    }

    private static async Task WriteTextAsync(
        string output, string text, bool overwrite, Action<string> writeWarning, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(output);
        if (name.Equals(ResumeStateStore.FileName, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(ResumeStateStore.FileName + "-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(ResumeStateStore.FileName + ".", StringComparison.OrdinalIgnoreCase)
            || name.Equals(ResumeStateStore.LockFileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Report output must not overwrite the resume database, journal, recovery artifacts, or operation lease.");
        }
        var outputDirectory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(outputDirectory);

        var temporaryOutput = Path.Combine(outputDirectory, $".ghpmv-report-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryOutput, text, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporaryOutput, output, overwrite);
            }
            catch (IOException) when (File.Exists(output) && !overwrite)
            {
                throw new IOException("The report output already exists. Choose another --output path or explicitly use --overwrite.");
            }
        }
        finally
        {
            try
            {
                File.Delete(temporaryOutput);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                writeWarning($"warning: could not remove temporary report file '{temporaryOutput}': {exception.Message}");
            }
        }
    }

    public static async Task WriteResumeReportsAsync(
        string directory, bool writeHtml, Action<string> writeLine, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, ResumeStateStore.FileName);
        try
        {
            _ = File.GetAttributes(path);
        }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }

        // Each log's committed read feeds both formats; an invalid log must not block the other.
        foreach (var name in new[] { ImportLog.FileName, ProjectImportLog.FileName })
        {
            string json;
            try
            {
                var snapshot = ResumeStateStore.ReadSnapshot(directory, name, cancellationToken);
                if (snapshot is null)
                {
                    continue;
                }
                json = snapshot.ToDiagnosticJson();
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                writeLine($"warning: failed to read committed {name} for reports; existing JSON/HTML may be stale: {exception.Message}");
                continue;
            }
            var jsonPath = Path.GetFullPath(Path.Combine(directory, name));
            try
            {
                await WriteTextAsync(jsonPath, json, true, writeLine, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                writeLine($"warning: failed to refresh {name}; any existing export may be stale. Resume state remains in {path}: {exception.Message}");
            }
            if (writeHtml)
            {
                try
                {
                    var html = HtmlReportGenerator.Generate(json, ResumeStateStore.FileName);
                    var output = DefaultOutputPath(jsonPath);
                    await WriteTextAsync(output, html, true, writeLine, cancellationToken).ConfigureAwait(false);
                    writeLine($"HTML report: {output}");
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    writeLine($"warning: failed to refresh HTML for {name}; any existing report may be stale. Resume state remains in {path}: {exception.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Writes an HTML report beside each existing JSON log. Failures are reported as warnings only, so automatic
    /// report generation never changes the command's outcome.
    /// </summary>
    public static async Task WriteBesideAsync(
        IEnumerable<string> inputPaths,
        Action<string> writeLine,
        CancellationToken cancellationToken)
    {
        foreach (var inputPath in inputPaths)
        {
            if (!File.Exists(inputPath))
            {
                continue;
            }

            try
            {
                var output = await WriteAsync(
                    inputPath,
                    outputPath: null,
                    HtmlReportGenerator.SupportedLanguages[0],
                    overwrite: true,
                    writeLine,
                    cancellationToken).ConfigureAwait(false);
                writeLine($"HTML report: {output}");
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                writeLine($"warning: failed to write HTML report for {Path.GetFileName(inputPath)}: {exception.Message}");
            }
        }
    }

    public static void DeleteStale(string inputPath, Action<string> writeWarning)
    {
        var output = DefaultOutputPath(inputPath);
        try
        {
            File.Delete(output);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            writeWarning($"warning: failed to remove stale {Path.GetFileName(output)}: {exception.Message}");
        }
    }
}
