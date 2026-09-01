using System.Text.Json;
using System.Text.Json.Serialization;
using FinMonitor.Domain.DTOs;
using FinMonitor.Domain.Models;
using StackExchange.Redis;

namespace FinMonitor.Domain.Repositories;

/// <summary>
/// Decorates another <see cref="ITransactionRepository"/> with a Redis-backed cache of the most
/// recent <see cref="CacheCapacity"/> transactions, so <see cref="GetRecentAsync"/> (the common
/// "load the dashboard" read) can be served without hitting the database, safely across
/// multiple API pod replicas sharing one Redis instance.
///
/// The wrapped repository stays the single source of truth throughout: every write goes there
/// first, and every read path that can hit Redis has a DB fallback. A Redis failure - on read or
/// write - never fails a request or loses data; it only means slower reads until the cache
/// self-heals (the next cache-miss re-warms it from the database).
/// </summary>
public sealed class RedisRecentTransactionsCache : ITransactionRepository, IStorageInitializer
{
    private const int CacheCapacity = 1_000;

    private const string ListKey = "finmonitor:recent-transactions";

    // Purely internal cache storage, never returned over the wire directly - PascalCase is fine,
    // it just needs to round-trip with itself (and to store Status as text, not an int, so it
    // reads back correctly regardless of underlying enum numbering changes).
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ITransactionRepository _inner;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisRecentTransactionsCache> _logger;

    public RedisRecentTransactionsCache(
        [FromKeyedServices("db")]ITransactionRepository inner,
        IConnectionMultiplexer redis,
        ILogger<RedisRecentTransactionsCache> logger)
    {
        _inner = inner;
        _redis = redis;
        _logger = logger;
    }

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        _inner is IStorageInitializer initializer
            ? initializer.InitializeAsync(cancellationToken)
            : Task.CompletedTask;

    // Not cached - only the "recent" list is; a single-id lookup gains little from Redis and
    // adding it would mean two places to keep consistent instead of one.
    public Task<Transaction?> GetByIdAsync(Guid transactionId, CancellationToken cancellationToken = default) =>
        _inner.GetByIdAsync(transactionId, cancellationToken);

    public async Task<Transaction?> TryAddAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        var stored = await _inner.TryAddAsync(transaction, cancellationToken);
        if (stored is null)
        {
            return null;
        }

        try
        {
            await PushToCacheAsync(stored);
        }
        catch (RedisException ex)
        {
            // Best-effort: the DB write above already succeeded (durability preserved) - a Redis
            // hiccup here must never fail the request or lose the write, only leave the cache
            // slightly stale until the next cache-miss falls back to the DB and re-warms it.
            _logger.LogWarning(ex,
                "Failed to update the recent-transactions Redis cache for {TransactionId}; the write itself succeeded.",
                stored.TransactionId);
        }

        return stored;
    }

    public async Task<PagedResult<Transaction>> GetRecentAsync(int limit, TransactionCursor? cursor, CancellationToken cancellationToken = default)
    {
        // The cache only ever serves "give me the fresh head" - any cursor-driven page, or a
        // limit beyond what the cache holds, goes straight to the database unchanged. A cursor
        // computed from a cache-served item still works correctly against that DB-backed
        // continuation query below, since TransactionCursor is just (Timestamp, TransactionId)
        // and doesn't care where the page before it came from.
        if (cursor is not null || limit > CacheCapacity)
        {
            return await _inner.GetRecentAsync(limit, cursor, cancellationToken);
        }

        List<Transaction>? cached;
        try
        {
            cached = await ReadFromCacheAsync(limit);
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Failed to read the recent-transactions Redis cache; falling back to the database.");
            cached = null;
        }

        if (cached is { Count: > 0 })
        {
            string? hitCursor = cached.Count == limit
                ? new TransactionCursor(cached[^1].Timestamp, cached[^1].TransactionId).Encode()
                : null;
            return new PagedResult<Transaction>(cached, hitCursor);
        }
        // counnt is 0:
        var page = await _inner.GetRecentAsync(CacheCapacity, null, cancellationToken);
        try
        {
            await RefillCacheAsync(page.Items);
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Failed to warm the recent-transactions Redis cache from the database.");
        }

        if (limit >= page.Items.Count)
        {
            return page;
        }

        var truncated = page.Items.Take(limit).ToList();
        var nextCursor = new TransactionCursor(truncated[^1].Timestamp, truncated[^1].TransactionId).Encode();
        return new PagedResult<Transaction>(truncated, nextCursor);
    }

    private async Task<List<Transaction>> ReadFromCacheAsync(int limit)
    {
        var db = _redis.GetDatabase();
        var values = await db.ListRangeAsync(ListKey, 0, limit - 1);

        var result = new List<Transaction>(values.Length);
        foreach (var json in values)
        {
            var transaction = JsonSerializer.Deserialize<Transaction>(json!, JsonOptions);
            if (transaction is not null)
            {
                result.Add(transaction);
            }
        }

        return result;
    }

    private async Task PushToCacheAsync(Transaction transaction)
    {
        var db = _redis.GetDatabase();
        var json = JsonSerializer.Serialize(transaction, JsonOptions);
        await db.ListLeftPushAsync(ListKey, json);
        await db.ListTrimAsync(ListKey, 0, CacheCapacity - 1);
    }

    private async Task RefillCacheAsync(IReadOnlyList<Transaction> transactions)
    {
        if (transactions.Count == 0)
        {
            return;
        }

        var db = _redis.GetDatabase();
        var values = transactions
            .Select(t => (RedisValue)JsonSerializer.Serialize(t, JsonOptions))
            .ToArray();
        await db.ListRightPushAsync(ListKey, values);
    }
}
