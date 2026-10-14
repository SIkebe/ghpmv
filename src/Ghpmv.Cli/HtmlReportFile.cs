namespace Ghpmv.Cli;

using Ghpmv.Core.Import;

internal static class HtmlReportFile
{
    public static readonly string[] ResumeLogFileNames =
    [
        ImportLog.FileName,
        ProjectImportLog.FileName,
    ];

    public static string DefaultOutputPath(string inputPath) => Path.ChangeExtension(inputPath, ".html");

    public static async Task<string> WriteAsync(
        string inputPath,
        string? outputPath,
        string language,
        bool overwrite,
        Action<string> writeWarning,
        CancellationToken cancellationToken)
    {
        var input = Path.GetFullPath(inputPath);
        var output = Path.GetFullPath(outputPath ?? DefaultOutputPath(input));
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("--output must differ from --input.");
        }

        var json = await File.ReadAllTextAsync(input, cancellationToken).ConfigureAwait(false);
        var html = HtmlReportGenerator.Generate(json, Path.GetFileName(input), language);
        var outputDirectory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(outputDirectory);

        var temporaryOutput = Path.Combine(outputDirectory, $".ghpmv-report-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryOutput, html, cancellationToken).ConfigureAwait(false);
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

        return output;
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
