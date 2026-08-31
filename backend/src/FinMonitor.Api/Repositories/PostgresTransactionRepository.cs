using FinMonitor.Domain.DTOs;
using FinMonitor.Domain.Exceptions;
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
///
/// Every DB call here is genuinely async (OpenAsync/ExecuteReaderAsync/ExecuteNonQueryAsync,
/// CancellationToken threaded through) - a prior version used the synchronous ADO.NET API
/// inside an otherwise-async request pipeline, which blocks a thread-pool thread for the
/// entire network round trip on every call. Under the "100 requests arrive quickly" load this
/// project is meant to survive, that starves the thread pool exactly when it matters most.
/// </summary>
public sealed class PostgresTransactionRepository : ITransactionRepository, IStorageInitializer
{
    // Kept short deliberately: a hung/unreachable Postgres should fail one attempt fast, not
    // tie up the retry loop for Npgsql's much longer default (15s). Combined with the retry
    // loop below and a Kubernetes startupProbe with a generous failureThreshold, this gives
    // Postgres real time to become ready without ever looking "stuck" on a single attempt.
    private const int ConnectTimeoutSeconds = 3;

    private readonly string _connectionString;

    public PostgresTransactionRepository(string connectionString)
    {
        // Construction itself does no I/O - schema setup happens later, via InitializeAsync,
        // driven by StorageStartupHostedService instead of blocking the caller synchronously.
        var connectionStringBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Timeout = ConnectTimeoutSeconds,
        };
        _connectionString = connectionStringBuilder.ConnectionString;
    }

    // Containers frequently start before Postgres is actually ready to accept connections
    // (docker-compose's `depends_on` only waits for the container to start, not the database
    // inside it; k8s makes no ordering guarantee between pods at all) - retry with backoff
    // instead of letting a transient "connection refused" crash the app on startup.
    private const int MaxStartupRetries = 10;
    private static readonly TimeSpan StartupRetryDelay = TimeSpan.FromSeconds(2);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await EnsureSchemaAsync(cancellationToken);
                return;
            }
            catch (NpgsqlException) when (attempt < MaxStartupRetries)
            {
                await Task.Delay(StartupRetryDelay, cancellationToken);
            }
        }
    }

    // A fixed, arbitrary key for the advisory lock below - has no meaning beyond identifying
    // "the schema-creation lock" consistently across all connections.
    private const long SchemaLockKey = 482715301;

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // CREATE TABLE IF NOT EXISTS has a known Postgres race: if several pods start at once
        // against a fresh database, multiple sessions can each see "doesn't exist yet" and
        // collide creating it, failing with a duplicate-key error on an internal catalog index.
        // A session-level advisory lock serializes schema creation across connections without
        // needing any external coordination.
        await using (var lockCommand = connection.CreateCommand())
        {
            lockCommand.CommandText = $"SELECT pg_advisory_lock({SchemaLockKey});";
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            await using var createCommand = connection.CreateCommand();
            // ADD COLUMN IF NOT EXISTS (not just CREATE TABLE IF NOT EXISTS with seq baked in)
            // deliberately: a table created by a version of this app from before `seq` existed
            // otherwise leaves CREATE TABLE a no-op and crashes on "column seq does not exist"
            // the moment the seq index below tries to run - hit for real against a pre-existing
            // docker-compose Postgres volume during development. The sequence is created before
            // the column so its nextval() default can reference it, then attached with OWNED BY
            // so it gets dropped along with the column/table.
            createCommand.CommandText = """
                CREATE TABLE IF NOT EXISTS transactions (
                    transaction_id UUID PRIMARY KEY,
                    amount NUMERIC NOT NULL,
                    currency TEXT NOT NULL,
                    status TEXT NOT NULL,
                    timestamp TIMESTAMPTZ NOT NULL
                );

                CREATE SEQUENCE IF NOT EXISTS transactions_seq_seq;

                ALTER TABLE transactions
                    ADD COLUMN IF NOT EXISTS seq BIGINT NOT NULL DEFAULT nextval('transactions_seq_seq');

                ALTER SEQUENCE transactions_seq_seq OWNED BY transactions.seq;

                CREATE INDEX IF NOT EXISTS idx_transactions_timestamp_id
                    ON transactions (timestamp DESC, transaction_id DESC);

                CREATE INDEX IF NOT EXISTS idx_transactions_seq
                    ON transactions (seq);
                """;
            await createCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await using var unlockCommand = connection.CreateCommand();
            unlockCommand.CommandText = $"SELECT pg_advisory_unlock({SchemaLockKey});";
            await unlockCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    // Single choke point for translating "Postgres itself failed" into a storage-agnostic
    // exception. Every per-request method below (not InitializeAsync/EnsureSchemaAsync - those
    // have their own startup retry loop, a different concern) routes through here, so
    // StorageExceptionHandler at the API layer never needs to reference Npgsql at all - only
    // this one repository does.
    private static async Task<T> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new StorageUnavailableException("A PostgreSQL operation failed.", ex);
        }
    }

    public Task<Transaction?> TryAddAsync(Transaction transaction, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO transactions (transaction_id, amount, currency, status, timestamp)
                VALUES (@id, @amount, @currency, @status, @timestamp)
                ON CONFLICT (transaction_id) DO NOTHING
                RETURNING seq;
                """;
            command.Parameters.AddWithValue("@id", transaction.TransactionId);
            command.Parameters.AddWithValue("@amount", transaction.Amount);
            command.Parameters.AddWithValue("@currency", transaction.Currency);
            command.Parameters.AddWithValue("@status", transaction.Status.ToString());
            command.Parameters.AddWithValue("@timestamp", transaction.Timestamp);

            // RETURNING seq gives us both "did it insert" (a row came back vs. ON CONFLICT
            // suppressed it) and the storage-assigned sequence in a single round trip.
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            return transaction with { Sequence = reader.GetInt64(0) };
        });

    public Task<Transaction?> GetByIdAsync(Guid transactionId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT transaction_id, amount, currency, status, timestamp, seq
                FROM transactions
                WHERE transaction_id = @id;
                """;
            command.Parameters.AddWithValue("@id", transactionId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadTransaction(reader) : null;
        });

    public Task<PagedResult<Transaction>> GetRecentAsync(int limit, TransactionCursor? cursor, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            // ORDER BY + LIMIT here is served by idx_transactions_timestamp_id (created above) -
            // without it this becomes a full sort of the whole table on every request. The cursor
            // branch uses a row-value comparison, which Postgres evaluates lexicographically
            // (timestamp first, transaction_id as tiebreaker) - exactly matching the ORDER BY below,
            // so it keeps using the same index instead of falling back to a full scan.
            if (cursor is { } c)
            {
                command.CommandText = """
                    SELECT transaction_id, amount, currency, status, timestamp, seq
                    FROM transactions
                    WHERE (timestamp, transaction_id) < (@cursorTimestamp, @cursorId)
                    ORDER BY timestamp DESC, transaction_id DESC
                    LIMIT @limit;
                    """;
                command.Parameters.AddWithValue("@cursorTimestamp", c.Timestamp);
                command.Parameters.AddWithValue("@cursorId", c.TransactionId);
            }
            else
            {
                command.CommandText = """
                    SELECT transaction_id, amount, currency, status, timestamp, seq
                    FROM transactions
                    ORDER BY timestamp DESC, transaction_id DESC
                    LIMIT @limit;
                    """;
            }
            command.Parameters.AddWithValue("@limit", limit);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var results = new List<Transaction>();
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(ReadTransaction(reader));
            }

            string? nextCursor = results.Count == limit
                ? new TransactionCursor(results[^1].Timestamp, results[^1].TransactionId).Encode()
                : null;

            return new PagedResult<Transaction>(results, nextCursor);
        });

    // Bounded even for catch-up: a client that reconnects after a very long gap (or a fresh
    // client passing sequence=0) still gets a capped batch, not the entire table history.
    private const int MaxCatchUpBatch = 1_000;

    public Task<IReadOnlyList<Transaction>> GetSinceAsync(long sinceSequence, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT transaction_id, amount, currency, status, timestamp, seq
                FROM transactions
                WHERE seq > @sinceSequence
                ORDER BY seq ASC
                LIMIT @maxCatchUp;
                """;
            command.Parameters.AddWithValue("@sinceSequence", sinceSequence);
            command.Parameters.AddWithValue("@maxCatchUp", MaxCatchUpBatch);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var results = new List<Transaction>();
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(ReadTransaction(reader));
            }
            return (IReadOnlyList<Transaction>)results;
        });

    private static Transaction ReadTransaction(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetDecimal(1),
        reader.GetString(2),
        Enum.Parse<TransactionStatus>(reader.GetString(3)),
        reader.GetFieldValue<DateTimeOffset>(4),
        reader.GetInt64(5));
}
