using System.Threading.Channels;
using FinMonitor.Domain.Models;

namespace FinMonitor.Domain.Realtime;

// Decouples "a transaction was durably stored" from "clients were notified" - POST must not
// wait on SignalR/Redis, which can be slow or momentarily unavailable without that being the
// ingestion API's problem. TransactionBroadcastWorker (FinMonitor.Api.Realtime) drains this on
// a background loop.
public sealed class TransactionBroadcastQueue
{
    private const int Capacity = 10_000;

    private readonly Channel<Transaction> _channel = Channel.CreateBounded<Transaction>(
        new BoundedChannelOptions(Capacity)
        {
            // A writer (an HTTP request) must never block on broadcast capacity, so dropping
            // the oldest not-yet-broadcast item under sustained overload - rather than blocking
            // or throwing - is the deliberate trade-off. GET /api/transactions/since/{seq} is
            // the fallback for anything a client missed over the wire as a result.
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    public ChannelReader<Transaction> Reader => _channel.Reader;

    public bool TryEnqueue(Transaction transaction) => _channel.Writer.TryWrite(transaction);
}
