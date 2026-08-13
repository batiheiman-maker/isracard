using FinMonitor.Domain.Models;

namespace FinMonitor.Domain.Realtime;

public interface ITransactionBroadcaster
{
    Task BroadcastAsync(Transaction transaction, CancellationToken cancellationToken = default);
}
