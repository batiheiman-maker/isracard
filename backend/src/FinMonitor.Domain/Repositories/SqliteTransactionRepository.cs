using System.Globalization;
using FinMonitor.Domain.Models;
using Microsoft.Data.Sqlite;

namespace FinMonitor.Domain.Repositories;

/// <summary>
/// Shared-file repository used in distributed mode: every replica points at the same SQLite
/// file (a mounted volume/PVC) so GET /api/transactions is consistent across pods, closing the
/// gap that a per-pod in-memory store would leave even with the SignalR Redis backplane in place.
/// </summary>
public sealed class SqliteTransactionRepository : ITransactionRepository
{
    private readonly string _connectionString;

    public SqliteTransactionRepository(string connectionString)
    {
        _connectionString = connectionString;
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Transactions (
                TransactionId TEXT PRIMARY KEY,
                Amount TEXT NOT NULL,
                Currency TEXT NOT NULL,
                Status TEXT NOT NULL,
                Timestamp TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public bool TryAdd(Transaction transaction)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO Transactions (TransactionId, Amount, Currency, Status, Timestamp)
            VALUES (@id, @amount, @currency, @status, @timestamp);
            """;
        command.Parameters.AddWithValue("@id", transaction.TransactionId.ToString());
        command.Parameters.AddWithValue("@amount", transaction.Amount.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@currency", transaction.Currency);
        command.Parameters.AddWithValue("@status", transaction.Status.ToString());
        command.Parameters.AddWithValue("@timestamp", transaction.Timestamp.ToString("o", CultureInfo.InvariantCulture));

        return command.ExecuteNonQuery() == 1;
    }

    public Transaction? GetById(Guid transactionId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT TransactionId, Amount, Currency, Status, Timestamp FROM Transactions WHERE TransactionId = @id;";
        command.Parameters.AddWithValue("@id", transactionId.ToString());

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTransaction(reader) : null;
    }

    public IReadOnlyList<Transaction> GetAll()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT TransactionId, Amount, Currency, Status, Timestamp FROM Transactions ORDER BY Timestamp DESC;";

        using var reader = command.ExecuteReader();
        var results = new List<Transaction>();
        while (reader.Read())
        {
            results.Add(ReadTransaction(reader));
        }
        return results;
    }

    private static Transaction ReadTransaction(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        decimal.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
        reader.GetString(2),
        Enum.Parse<TransactionStatus>(reader.GetString(3)),
        DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture));

    public static void ClearPools() => SqliteConnection.ClearAllPools();
}
