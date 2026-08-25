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
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"'{path}' must contain a JSON object.");
            }

            if (!root.TryGetProperty("schemaVersion", out var schemaVersionElement)
                || schemaVersionElement.ValueKind != JsonValueKind.Number
                || !schemaVersionElement.TryGetInt32(out var schemaVersion))
            {
                throw new InvalidDataException($"'{path}' is missing required integer 'schemaVersion'.");
            }

            if (schemaVersion != ProjectSnapshot.CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"'{path}' uses unsupported schema version {schemaVersion}; expected {ProjectSnapshot.CurrentSchemaVersion}.");
            }

            if (!root.TryGetProperty("project", out var project)
                || project.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"'{path}' is missing required object 'project'.");
            }

            if (!project.TryGetProperty("template", out var template)
                || template.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new InvalidDataException($"'{path}' is missing required boolean 'project.template'.");
            }

            RequireArray(root, "fields", path);
            RequireArray(root, "views", path);
            RequireArray(root, "workflows", path);
            RequireArray(root, "items", path);
            RequireArray(root, "statusUpdates", path);
            RequireArray(root, "linkedRepositories", path);
            RequireArray(root, "linkedTeams", path);

            return root.Deserialize(SnapshotJsonContext.Default.ProjectSnapshot)
                ?? throw new InvalidDataException($"'{path}' contained a null snapshot.");
        }
    }

    private static void RequireArray(JsonElement root, string propertyName, string path)
    {
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"'{path}' is missing required array '{propertyName}'.");
        }
    }
}
