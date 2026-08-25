using System.Text.Json;

namespace Ghpmv.Core.Snapshot;

/// <summary>Reads and writes the single <c>snapshot.json</c> file inside a snapshot directory.</summary>
public static class SnapshotFile
{
    public const string FileName = "snapshot.json";

    /// <summary>Writes the snapshot as indented UTF-8 JSON and returns the file path.</summary>
    public static async Task<string> SaveAsync(ProjectSnapshot snapshot, string directory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);

        var stream = File.Create(path);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, snapshot, SnapshotJsonContext.Default.ProjectSnapshot, cancellationToken).ConfigureAwait(false);
        }

        return path;
    }

    /// <summary>Loads a snapshot from the <c>snapshot.json</c> file inside <paramref name="directory"/>.</summary>
    public static async Task<ProjectSnapshot> LoadAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var path = Path.Combine(directory, FileName);
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var schemaVersion = root.TryGetProperty("schemaVersion", out var schemaVersionElement)
                ? schemaVersionElement.GetInt32()
                : 0;
            if (schemaVersion != ProjectSnapshot.CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"'{path}' uses unsupported schema version {schemaVersion}; expected {ProjectSnapshot.CurrentSchemaVersion}.");
            }

            if (!root.GetProperty("project").TryGetProperty("template", out _))
            {
                throw new InvalidDataException($"'{path}' is missing required property 'project.template'.");
            }

            return root.Deserialize(SnapshotJsonContext.Default.ProjectSnapshot)
                ?? throw new InvalidDataException($"'{path}' contained a null snapshot.");
        }
    }
}
