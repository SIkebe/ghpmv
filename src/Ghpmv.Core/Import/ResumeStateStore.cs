using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Ghpmv.Core.Import;

/// <summary>
/// Authoritative local resume state. Callers must hold an operation lease across load, remote
/// mutations, and save; SQLite transactions alone cannot serialize that entire operation.
/// </summary>
public static class ResumeStateStore
{
    public const string FileName = "resume-state.db";
    public const string LockFileName = "resume-state.lock";
    private const int SchemaVersion = 1;
    private static readonly string[] LogNames = [ImportLog.FileName, ProjectImportLog.FileName];

    internal static async Task PersistFailureAsync(Exception primary, Func<Task> saveAsync)
    {
        try
        {
            await saveAsync().ConfigureAwait(false);
        }
        catch (Exception persistenceFailure)
        {
            throw new AggregateException("The operation failed and its failure state could not be persisted.",
                primary, persistenceFailure);
        }
    }

    public static IDisposable AcquireOperationLease(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, LockFileName);
        try
        {
            // Keep the inode/path after release: deleting it can let a third process bypass a holder.
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new IOException($"Cannot acquire resume operation lease '{path}'. Another import may be running.", exception);
        }
    }

    internal static async Task<string?> LoadAsync(string directory, string name, CancellationToken cancellationToken)
    {
        if (!await EnsureCreatedAsync(directory, createEmpty: false, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        var snapshots = ReadSnapshots(directory, recover: true, selectedName: null, cancellationToken);
        return snapshots.SingleOrDefault(snapshot => snapshot.Name == name)?.Json;
    }

    internal static async Task SaveAsync(string directory, string name, string json, CancellationToken cancellationToken)
    {
        await EnsureCreatedAsync(directory, createEmpty: true, cancellationToken).ConfigureAwait(false);
        Execute(directory, readOnly: false, connection =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO resume_logs (name, payload, revision) VALUES ($name, $payload, 1)
                ON CONFLICT(name) DO UPDATE SET payload = excluded.payload, revision = revision + 1;
                """;
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$payload", json);
            command.ExecuteNonQuery();
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return true;
        });
    }

    /// <summary>Reads a consistent committed snapshot without creating, migrating, or changing the database.</summary>
    public static IReadOnlyList<ResumeStateSnapshot> ReadSnapshots(string directory, CancellationToken cancellationToken = default) =>
        ReadSnapshots(directory, recover: false, selectedName: null, cancellationToken);

    /// <summary>
    /// Reads only the selected diagnostic log. Unlike import loads, unrelated payloads are not
    /// validated; database schema checks and read-only access still apply.
    /// </summary>
    public static ResumeStateSnapshot? ReadSnapshot(
        string directory, string name, CancellationToken cancellationToken = default)
    {
        if (!LogNames.Contains(name, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unknown resume log '{name}'.", nameof(name));
        }
        return ReadSnapshots(directory, recover: false, name, cancellationToken).SingleOrDefault();
    }

    private static List<ResumeStateSnapshot> ReadSnapshots(
        string directory, bool recover, string? selectedName, CancellationToken cancellationToken) =>
        Execute(directory, readOnly: !recover, connection =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var transaction = connection.BeginTransaction(deferred: true);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = selectedName is null
                ? "SELECT name, payload, revision FROM resume_logs ORDER BY name;"
                : "SELECT name, payload, revision FROM resume_logs WHERE name = $name;";
            if (selectedName is not null)
            {
                command.Parameters.AddWithValue("$name", selectedName);
            }
            using var reader = command.ExecuteReader();
            var snapshots = new List<ResumeStateSnapshot>();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = reader.GetString(0);
                var json = reader.GetString(1);
                Validate(name, json);
                snapshots.Add(new(name, json, reader.GetInt64(2)));
            }
            return snapshots;
        }, stage: "reading-resume-database");

    private static async Task<bool> EnsureCreatedAsync(string directory, bool createEmpty, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(directory, FileName);
        if (Exists(path))
        {
            // The subsequent database read/write validates the schema; never inspect legacy JSON here.
            return true;
        }

        var legacy = new List<(string Name, string Json)>();
        foreach (var name in LogNames)
        {
            var json = await ReadLegacyAsync(Path.Combine(directory, name), cancellationToken).ConfigureAwait(false);
            if (json is not null)
            {
                Validate(name, json);
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("resumeState", out _))
                {
                    throw new InvalidDataException($"'{name}' is a diagnostic export, not a legacy resume log. Restore {FileName}; refusing to resume from a projection.");
                }
                legacy.Add((name, json));
            }
            else if (Directory.Exists(directory)
                && (Exists(Path.Combine(directory, name + ".bak"))
                    || Directory.EnumerateFiles(directory, name + ".*.tmp").Any()))
            {
                throw new InvalidDataException($"'{name}' is missing but recovery artifacts remain. Reconcile them before initializing {FileName}.");
            }
        }
        if (!createEmpty && legacy.Count == 0)
        {
            return false;
        }

        Directory.CreateDirectory(directory);
        // Publish an initialized database only after its entire legacy migration commits.
        // This unique file is used only for initial adoption, never for checkpoint writes.
        var temporary = path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            using (var connection = Open(temporary, SqliteOpenMode.ReadWriteCreate))
            {
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    CREATE TABLE resume_logs (
                        name TEXT PRIMARY KEY NOT NULL,
                        payload TEXT NOT NULL,
                        revision INTEGER NOT NULL CHECK (revision > 0)
                    );
                    PRAGMA user_version = 1;
                    """;
                command.ExecuteNonQuery();
                foreach (var (name, json) in legacy)
                {
                    command.Parameters.Clear();
                    command.CommandText = "INSERT INTO resume_logs VALUES ($name, $payload, 1);";
                    command.Parameters.AddWithValue("$name", name);
                    command.Parameters.AddWithValue("$payload", json);
                    command.ExecuteNonQuery();
                }
                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
            }
            File.Move(temporary, path);
        }
        catch (SqliteException exception)
        {
            throw Failure(path, "initializing-resume-database", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ResumeLogSaveFailure.Attach(exception, new()
            {
                Stage = "initializing-resume-database",
                LogPath = Path.GetFullPath(path),
                TemporaryPath = Path.GetFullPath(temporary),
                ExecutingProcessId = Environment.ProcessId,
            });
            throw;
        }
        return true;
    }

    private static T Execute<T>(string directory, bool readOnly, Func<SqliteConnection, T> action, string? stage = null)
    {
        var path = Path.GetFullPath(Path.Combine(directory, FileName));
        try
        {
            using var connection = Open(path, readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != SchemaVersion)
            {
                throw new InvalidDataException($"'{path}' has an unsupported or incomplete schema. Refusing JSON fallback.");
            }
            command.CommandText = "PRAGMA journal_mode;";
            if (!string.Equals(command.ExecuteScalar()?.ToString(), "delete", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"'{path}' uses an unsupported journal mode; expected DELETE.");
            }
            return action(connection);
        }
        catch (SqliteException exception)
        {
            throw Failure(path, stage ?? (readOnly ? "reading-resume-database" : "committing-resume-database"), exception);
        }
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
            DefaultTimeout = 2,
        }.ToString());
        try
        {
            connection.Open();
            if (mode != SqliteOpenMode.ReadOnly)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA synchronous = FULL;";
                command.ExecuteNonQuery();
            }
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static IOException Failure(string path, string stage, SqliteException cause)
    {
        var exception = new IOException(
            $"Resume database operation failed for '{path}' (SQLite {cause.SqliteErrorCode}/{cause.SqliteExtendedErrorCode}). "
            + "Do not delete the database or resume from JSON exports. " + cause.Message, cause);
        ResumeLogSaveFailure.Attach(exception, new()
        {
            Stage = stage,
            LogPath = path,
            ExecutingProcessId = Environment.ProcessId,
        });
        return exception;
    }

    private static bool Exists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static async Task<string?> ReadLegacyAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void Validate(string name, string json)
    {
        if (name == ImportLog.FileName)
        {
            _ = ImportLog.Deserialize(json);
        }
        else if (name == ProjectImportLog.FileName)
        {
            _ = ProjectImportLog.Deserialize(json);
        }
        else
        {
            throw new InvalidDataException($"Unknown resume log '{name}'.");
        }
    }
}

public sealed record ResumeStateSnapshot(string Name, string Json, long Revision)
{
    public string ToDiagnosticJson()
    {
        try
        {
            var root = JsonNode.Parse(Json)!.AsObject();
            root["resumeState"] = new JsonObject
            {
                ["source"] = ResumeStateStore.FileName,
                ["revision"] = Revision,
                ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["diagnosticOnly"] = true,
            };
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException($"'{Name}' cannot be projected as diagnostic JSON: {exception.Message}", exception);
        }
    }
}
