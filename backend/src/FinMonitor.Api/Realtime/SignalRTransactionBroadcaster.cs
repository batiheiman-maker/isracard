using FinMonitor.Api.Hubs;
using FinMonitor.Domain.Models;
using FinMonitor.Domain.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace FinMonitor.Api.Realtime;

public sealed class SignalRTransactionBroadcaster : ITransactionBroadcaster
{
    public const string TransactionReceivedEvent = "TransactionReceived";

    private readonly IHubContext<TransactionHub> _hubContext;

    public SignalRTransactionBroadcaster(IHubContext<TransactionHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task BroadcastAsync(Transaction transaction, CancellationToken cancellationToken = default) =>
        _hubContext.Clients.All.SendAsync(TransactionReceivedEvent, transaction, cancellationToken);
}
