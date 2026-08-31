namespace FinMonitor.Api.Health;

// Shared between StorageStartupHostedService (the writer, once, false->true) and the /healthz
// endpoint (the reader, polled repeatedly by k8s probes). volatile ensures the flip is visible
// across threads promptly without needing a lock for what is otherwise a single one-way write.
// MarkReady() instead of a public setter: the only valid transition is false->true, once, and a
// settable property would let any caller flip it back to false (or set it redundantly) with no
// compiler signal that this is a one-way startup event, not a general-purpose flag.
public sealed class StartupHealthState
{
    private volatile bool _isReady;

    public bool IsReady => _isReady;

    public void MarkReady() => _isReady = true;
}
