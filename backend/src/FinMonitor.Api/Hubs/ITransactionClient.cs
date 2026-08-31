using FinMonitor.Domain.Models;

namespace FinMonitor.Api.Hubs;

// The one method the server ever invokes on connected clients - see TransactionHub for why
// it's typed instead of a magic-string SendAsync.
public interface ITransactionClient
{
    Task TransactionReceived(Transaction transaction);
}
