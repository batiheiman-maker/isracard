import { useCallback, useEffect, useRef, useState } from "react";
import { createHubConnection, TRANSACTION_RECEIVED_EVENT } from "../api/signalrClient";
import { getTransactions } from "../api/transactionsApi";
import type { Transaction } from "../types/transaction";

const MAX_TRANSACTIONS = 500;
const FLUSH_INTERVAL_MS = 120;

export type ConnectionStatus = "connecting" | "connected" | "reconnecting" | "disconnected";

// Incoming SignalR messages are buffered in a ref and flushed on a fixed interval instead of
// re-rendering per message, so a burst of 100 near-simultaneous transactions costs a handful of
// renders, not 100 - this is what keeps the UI responsive under the "no freeze" requirement.
export function useTransactionStream() {
  const [transactions, setTransactions] = useState<Transaction[]>([]);
  const [status, setStatus] = useState<ConnectionStatus>("connecting");
  const queueRef = useRef<Transaction[]>([]);
  const seenIdsRef = useRef<Set<string>>(new Set());
  const flushTimerRef = useRef<number | null>(null);

  const flush = useCallback(() => {
    flushTimerRef.current = null;
    if (queueRef.current.length === 0) return;
    const incoming = queueRef.current;
    queueRef.current = [];

    // Dedup happens here, outside the setState updater: React (StrictMode, concurrent
    // rendering) may invoke a functional updater more than once per commit, so the updater
    // itself must be a pure function of `prev` - any side effect (like mutating this ref)
    // inside it would make a re-invocation see already-"seen" ids and silently drop the batch.
    const fresh = incoming.filter((t) => {
      if (seenIdsRef.current.has(t.transactionId)) return false;
      seenIdsRef.current.add(t.transactionId);
      return true;
    });
    if (fresh.length === 0) return;

    setTransactions((prev) => [...fresh.reverse(), ...prev].slice(0, MAX_TRANSACTIONS));
  }, []);

  const enqueue = useCallback(
    (transaction: Transaction) => {
      queueRef.current.push(transaction);
      if (flushTimerRef.current === null) {
        flushTimerRef.current = window.setTimeout(flush, FLUSH_INTERVAL_MS);
      }
    },
    [flush],
  );

  useEffect(() => {
    let isMounted = true;
    const connection = createHubConnection();

    connection.on(TRANSACTION_RECEIVED_EVENT, (transaction: Transaction) => enqueue(transaction));
    connection.onreconnecting(() => isMounted && setStatus("reconnecting"));
    connection.onreconnected(() => isMounted && setStatus("connected"));
    connection.onclose(() => isMounted && setStatus("disconnected"));

    (async () => {
      try {
        const snapshot = await getTransactions();
        if (!isMounted) return;
        snapshot.forEach((t) => seenIdsRef.current.add(t.transactionId));
        setTransactions(snapshot.slice(0, MAX_TRANSACTIONS));

        await connection.start();
        if (isMounted) setStatus("connected");
      } catch {
        if (isMounted) setStatus("disconnected");
      }
    })();

    return () => {
      isMounted = false;
      if (flushTimerRef.current !== null) window.clearTimeout(flushTimerRef.current);
      connection.stop();
    };
  }, [enqueue]);

  return { transactions, status };
}
