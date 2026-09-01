using FinMonitor.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace FinMonitor.Domain.Repositories;

// Maps straight onto the domain record - Transaction's positional constructor is bound by EF
// Core via parameter-name matching, so no separate persistence entity class is needed. Targets
// the exact same `transactions` table/columns that PostgresSchemaInitializer creates.
public sealed class FinMonitorDbContext : DbContext
{
    public FinMonitorDbContext(DbContextOptions<FinMonitorDbContext> options) : base(options)
    {
    }

    public DbSet<Transaction> Transactions => Set<Transaction>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Transaction>(entity =>
        {
            entity.ToTable("transactions");
            entity.HasKey(t => t.TransactionId);

            entity.Property(t => t.TransactionId).HasColumnName("transaction_id");
            entity.Property(t => t.Amount).HasColumnName("amount").HasColumnType("numeric");
            entity.Property(t => t.Currency).HasColumnName("currency");
            entity.Property(t => t.Status).HasColumnName("status").HasConversion<string>();
            entity.Property(t => t.Timestamp).HasColumnName("timestamp");

            // Mirrors idx_transactions_timestamp_id, used by GetRecentAsync's keyset pagination.
            entity.HasIndex(t => new { t.Timestamp, t.TransactionId });
        });
    }
}
