using Microsoft.AspNetCore.SignalR;

namespace FinMonitor.Api.Hubs;

// Push-only hub: the server broadcasts on new transactions, clients never invoke methods on it.
public sealed class TransactionHub : Hub
{
}
