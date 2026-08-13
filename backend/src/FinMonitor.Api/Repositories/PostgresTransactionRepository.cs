using System.Globalization;
using FinMonitor.Domain.Models;
using Npgsql;

namespace FinMonitor.Domain.Repositories;

/// <summary>
/// Shared-database repository for distributed mode: every pod connects to the same PostgreSQL
/// instance, so GET /api/transactions is consistent across pods. Replaces an earlier
/// SQLite-over-shared-volume design - SQLite's own documentation explicitly warns that its
/// file locking is unsafe on network filesystems (the only realistic backing for a
/// multi-node ReadWriteMany volume), and SQLite is single-writer even when locking works,
/// which would bottleneck exactly the concurrent writes multiple pods exist to handle.
/// Postgres is built for concurrent multi-writer access, which is the actual requirement here.
/// </summary>
public sealed class PostgresTransactionRepository : ITransactionRepository
{
    private readonly string _connectionString;

    private PostgresTransactionRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    // Containers frequently start before Postgres is actually ready to accept connections
    // (docker-compose's `depends_on` only waits for the container to start, not the database
    // inside it; k8s makes no ordering guarantee between pods at all) - retry with backoff
    // instead of letting a transient "connection refused" crash the app on startup.
    private const int MaxStartupRetries = 10;
    private static readonly TimeSpan StartupRetryDelay = TimeSpan.FromSeconds(2);

    public static async Task<PostgresTransactionRepository> CreateAsync(string connectionString)
    {
        var repository = new PostgresTransactionRepository(connectionString);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await repository.EnsureSchemaAsync();
                return repository;
            }
            catch (NpgsqlException) when (attempt < MaxStartupRetries)
            {
                await Task.Delay(StartupRetryDelay);
            }
        }
    }

    // A fixed, arbitrary key for the advisory lock below - has no meaning beyond identifying
    // "the schema-creation lock" consistently across all connections.
    private const long SchemaLockKey = 482715301;

    private async Task EnsureSchemaAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        // CREATE TABLE IF NOT EXISTS has a known Postgres race: if several pods start at once
        // against a fresh database, multiple sessions can each see "doesn't exist yet" and
        // collide creating it, failing with a duplicate-key error on an internal catalog index.
        // A session-level advisory lock serializes schema creation across connections without
        // needing any external coordination.
        await using (var lockCommand = connection.CreateCommand())
        {
            lockCommand.CommandText = $"SELECT pg_advisory_lock({SchemaLockKey});";
            await lockCommand.ExecuteNonQueryAsync();
        }

        try
        {
            await using var createCommand = connection.CreateCommand();
            createCommand.CommandText = """
                CREATE TABLE IF NOT EXISTS transactions (
                    transaction_id UUID PRIMARY KEY,
                    amount NUMERIC NOT NULL,
                    currency TEXT NOT NULL,
                    status TEXT NOT NULL,
                    timestamp TIMESTAMPTZ NOT NULL
                );
                """;
            await createCommand.ExecuteNonQueryAsync();
        }
        finally
        {
            await using var unlockCommand = connection.CreateCommand();
            unlockCommand.CommandText = $"SELECT pg_advisory_unlock({SchemaLockKey});";
            await unlockCommand.ExecuteNonQueryAsync();
        }
    }

    private NpgsqlConnection OpenConnection()
    {
        var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public bool TryAdd(Transaction transaction)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO transactions (transaction_id, amount, currency, status, timestamp)
            VALUES (@id, @amount, @currency, @status, @timestamp)
            ON CONFLICT (transaction_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("@id", transaction.TransactionId);
        command.Parameters.AddWithValue("@amount", transaction.Amount);
        command.Parameters.AddWithValue("@currency", transaction.Currency);
        command.Parameters.AddWithValue("@status", transaction.Status.ToString());
        command.Parameters.AddWithValue("@timestamp", transaction.Timestamp);

        return command.ExecuteNonQuery() == 1;
    }

    public Transaction? GetById(Guid transactionId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT transaction_id, amount, currency, status, timestamp FROM transactions WHERE transaction_id = @id;";
        command.Parameters.AddWithValue("@id", transactionId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTransaction(reader) : null;
    }

    public IReadOnlyList<Transaction> GetAll()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT transaction_id, amount, currency, status, timestamp FROM transactions ORDER BY timestamp DESC;";

        using var reader = command.ExecuteReader();
        var results = new List<Transaction>();
        while (reader.Read())
        {
            results.Add(ReadTransaction(reader));
        }
        return results;
    }

    private static Transaction ReadTransaction(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetDecimal(1),
        reader.GetString(2),
        Enum.Parse<TransactionStatus>(reader.GetString(3)),
        reader.GetFieldValue<DateTimeOffset>(4));
}
