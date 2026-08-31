using FinMonitor.Api.Hubs;
using FinMonitor.Domain.Models;
using FinMonitor.Domain.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace FinMonitor.Api.Realtime;

public sealed class SignalRTransactionBroadcaster : ITransactionBroadcaster
{
    private readonly IHubContext<TransactionHub, ITransactionClient> _hubContext;

    public SignalRTransactionBroadcaster(IHubContext<TransactionHub, ITransactionClient> hubContext)
    {
        _hubContext = hubContext;
    }

    // cancellationToken isn't forwarded to the send itself: ITransactionClient's methods describe
    // what a client receives, not a place to smuggle in a cancellation signal for the server-side
    // send operation - those are different concerns. TransactionBroadcastWorker's own stoppingToken
    // still governs whether a broadcast is attempted at all (see its ExecuteAsync loop).
    public Task BroadcastAsync(Transaction transaction, CancellationToken cancellationToken = default) =>
        _hubContext.Clients.All.TransactionReceived(transaction);
}
