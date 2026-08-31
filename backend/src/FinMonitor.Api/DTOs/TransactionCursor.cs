using System.Globalization;
using System.Text;

namespace FinMonitor.Domain.DTOs;

// Opaque keyset-pagination cursor over (Timestamp, TransactionId) - the same ordering
// GetRecentAsync already sorts by. Keyset (not offset/skip) pagination is the correct choice
// here because the table is continuously appended to: an OFFSET-based "page 2" would skip or
// repeat rows whenever a new transaction is inserted between two page requests, since every
// row's offset shifts. A cursor anchored to the last row's own key never drifts like that.
public readonly record struct TransactionCursor(DateTimeOffset Timestamp, Guid TransactionId)
{
    public string Encode() =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Timestamp:O}|{TransactionId}"));

    public static bool TryParse(string? encoded, out TransactionCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrEmpty(encoded))
        {
            return false;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var parts = decoded.Split('|', 2);
            if (parts.Length != 2)
            {
                return false;
            }

            if (!DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp))
            {
                return false;
            }

            if (!Guid.TryParse(parts[1], out var transactionId))
            {
                return false;
            }

            cursor = new TransactionCursor(timestamp, transactionId);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
