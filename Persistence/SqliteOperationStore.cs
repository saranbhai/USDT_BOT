using System.Text.Json;
using Microsoft.Data.Sqlite;
using MyBot.Core;

namespace MyBot.Persistence;

public sealed class SqliteOperationStore : IOperationStore
{
    private readonly SqliteConnection connection;
    private bool disposed;

    public SqliteOperationStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        string fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        bool exists = File.Exists(fullPath);
        if (exists && new FileInfo(fullPath).Length == 0)
        {
            throw new InvalidDataException("The operation journal is empty. Restore the journal before continuing.");
        }

        var opened = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = exists ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());

        try
        {
            opened.Open();
            using var command = opened.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            if (!string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The operation journal failed its integrity check. Restore the journal before continuing.");
            }

            if (exists)
            {
                command.CommandText = "SELECT id, payload FROM operations LIMIT 0;";
                using var reader = command.ExecuteReader();
            }

            command.CommandText = "PRAGMA journal_mode = WAL;";
            if (!string.Equals(command.ExecuteScalar() as string, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The operation journal could not enable durable WAL storage.");
            }

            command.CommandText = "PRAGMA synchronous = FULL;";
            command.ExecuteNonQuery();

            if (!exists)
            {
                using var transaction = opened.BeginTransaction();
                command.Transaction = transaction;
                command.CommandText = "CREATE TABLE operations (id TEXT NOT NULL PRIMARY KEY, payload TEXT NOT NULL);";
                command.ExecuteNonQuery();
                transaction.Commit();
            }

            connection = opened;
        }
        catch
        {
            opened.Dispose();
            throw;
        }
    }

    public void Insert(OperationRecord operation)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Validate(operation);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO operations (id, payload) VALUES ($id, $payload);";
        command.Parameters.AddWithValue("$id", operation.Id);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(operation));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void Save(OperationRecord operation)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Validate(operation);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE operations SET payload = $payload WHERE id = $id;";
        command.Parameters.AddWithValue("$id", operation.Id);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(operation));
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("The operation is missing from the journal. No new request can be submitted safely.");
        }

        transaction.Commit();
    }

    public OperationRecord Get(string id)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM operations WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        var payload = command.ExecuteScalar() as string
            ?? throw new KeyNotFoundException("The requested operation was not found in the journal.");
        return Read(id, payload);
    }

    public IReadOnlyList<OperationRecord> List()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, payload FROM operations ORDER BY rowid DESC;";
        using var reader = command.ExecuteReader();
        var operations = new List<OperationRecord>();
        while (reader.Read())
        {
            operations.Add(Read(reader.GetString(0), reader.GetString(1)));
        }

        return operations.OrderByDescending(operation => operation.CreatedAt).ToArray();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        connection.Dispose();
    }

    private static OperationRecord Read(string id, string payload)
    {
        OperationRecord? operation;
        try
        {
            operation = JsonSerializer.Deserialize<OperationRecord>(payload);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The operation journal contains an unreadable record. Restore it before continuing.", exception);
        }

        if (operation is null || operation.Id != id || !Enum.IsDefined(operation.Stage))
        {
            throw new InvalidDataException("The operation journal contains an invalid record. Restore it before continuing.");
        }

        return operation;
    }

    private static void Validate(OperationRecord operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Id);
        if (!Enum.IsDefined(operation.Stage))
        {
            throw new ArgumentException("The operation has an invalid stage.", nameof(operation));
        }
    }
}
