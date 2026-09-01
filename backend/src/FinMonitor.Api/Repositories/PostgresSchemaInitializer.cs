using Npgsql;

namespace FinMonitor.Domain.Repositories;

// Schema setup for the Postgres-backed ITransactionRepository implementation (EfTransactionRepository),
// kept separate from the repository's own EF Core-based data access since it runs as raw
// ADO.NET/SQL against the `transactions` table before EF's model is ever used.
internal static class PostgresSchemaInitializer
{
    // Containers frequently start before Postgres is actually ready to accept connections
    // (docker-compose's `depends_on` only waits for the container to start, not the database
    // inside it; k8s makes no ordering guarantee between pods at all) - retry with backoff
    // instead of letting a transient "connection refused" crash the app on startup.
    private const int MaxStartupRetries = 10;
    private static readonly TimeSpan StartupRetryDelay = TimeSpan.FromSeconds(2);

    // A fixed, arbitrary key for the advisory lock below - has no meaning beyond identifying
    // "the schema-creation lock" consistently across all connections.
    private const long SchemaLockKey = 482715301;

    public static async Task InitializeWithRetryAsync(string connectionString, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await EnsureSchemaAsync(connectionString, cancellationToken);
                return;
            }
            catch (NpgsqlException) when (attempt < MaxStartupRetries)
            {
                await Task.Delay(StartupRetryDelay, cancellationToken);
            }
        }
    }

    private static async Task EnsureSchemaAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
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
            // Note: earlier versions of this app also created a `seq` BIGSERIAL-style column
            // (plus its backing sequence and index) for a since-removed sequence-based catch-up
            // feature. That's intentionally not recreated here for fresh installs; an existing
            // docker-compose volume from before the removal keeps the now-unused column/sequence/
            // index around harmlessly (no DROP is issued - this script only ever adds).
            createCommand.CommandText = """
                CREATE TABLE IF NOT EXISTS transactions (
                    transaction_id UUID PRIMARY KEY,
                    amount NUMERIC NOT NULL,
                    currency TEXT NOT NULL,
                    status TEXT NOT NULL,
                    timestamp TIMESTAMPTZ NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_transactions_timestamp_id
                    ON transactions (timestamp DESC, transaction_id DESC);
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
}
