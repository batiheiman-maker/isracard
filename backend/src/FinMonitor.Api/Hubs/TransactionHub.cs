using Microsoft.AspNetCore.SignalR;

namespace FinMonitor.Api.Hubs;

// Push-only hub: the server broadcasts on new transactions, clients never invoke methods on it.
// Hub<ITransactionClient> (not plain Hub) makes Clients.All/Clients.Group/etc. expose only the
// methods ITransactionClient declares, strongly typed.
public sealed class TransactionHub : Hub<ITransactionClient>
{
}
