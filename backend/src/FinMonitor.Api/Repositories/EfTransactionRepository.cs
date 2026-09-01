using FinMonitor.Domain.DTOs;
using FinMonitor.Domain.Exceptions;
using FinMonitor.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FinMonitor.Domain.Repositories;

/// <summary>
/// Shared-database repository for distributed mode: every pod connects to the same PostgreSQL
/// instance via EF Core, so GET /api/transactions is consistent across pods. See
/// <see cref="FinMonitorDbContext"/> and <see cref="PostgresSchemaInitializer"/> for the
/// table/schema this maps onto.
///
/// A short-lived <see cref="FinMonitorDbContext"/> is created per call via
/// <see cref="IDbContextFactory{TContext}"/> rather than injected directly - DbContext isn't
/// thread-safe for concurrent use, so this lets a single EfTransactionRepository instance be
/// registered as a singleton (consistent with the other ITransactionRepository
/// implementations) while still giving every request its own context.
/// </summary>
public sealed class EfTransactionRepository : ITransactionRepository, IStorageInitializer
{
    private readonly IDbContextFactory<FinMonitorDbContext> _contextFactory;
    private readonly string _connectionString;

    public EfTransactionRepository(IDbContextFactory<FinMonitorDbContext> contextFactory, string connectionString)
    {
        _contextFactory = contextFactory;
        _connectionString = connectionString;
    }

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        PostgresSchemaInitializer.InitializeWithRetryAsync(_connectionString, cancellationToken);

    // Single choke point for translating "Postgres itself failed" into a storage-agnostic
    // exception, so StorageExceptionHandler at the API layer never needs to reference Npgsql
    // or EF Core directly.
    private static async Task<T> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new StorageUnavailableException("An EF Core PostgreSQL operation failed.", ex);
        }
    }

    public Task<Transaction?> TryAddAsync(Transaction transaction, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
            db.Transactions.Add(transaction);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return null;
            }

            return transaction;
        });

    public Task<Transaction?> GetByIdAsync(Guid transactionId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
            return await db.Transactions
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.TransactionId == transactionId, cancellationToken);
        });

    public Task<PagedResult<Transaction>> GetRecentAsync(int limit, TransactionCursor? cursor, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
            IQueryable<Transaction> query = db.Transactions.AsNoTracking();

            if (cursor is { } c)
            {
                query = query.Where(t =>
                    t.Timestamp < c.Timestamp ||
                    (t.Timestamp == c.Timestamp && t.TransactionId.CompareTo(c.TransactionId) < 0));
            }

            var results = await query
                .OrderByDescending(t => t.Timestamp)
                .ThenByDescending(t => t.TransactionId)
                .Take(limit)
                .ToListAsync(cancellationToken);

            string? nextCursor = results.Count == limit
                ? new TransactionCursor(results[^1].Timestamp, results[^1].TransactionId).Encode()
                : null;

            return new PagedResult<Transaction>(results, nextCursor);
        });
}
