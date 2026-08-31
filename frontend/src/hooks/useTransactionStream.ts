import { useCallback, useEffect, useRef, useState } from "react";
import { createHubConnection, TRANSACTION_RECEIVED_EVENT } from "../api/signalrClient";
import { getTransactions, getTransactionsSince } from "../api/transactionsApi";
import type { Transaction } from "../types/transaction";

const MAX_TRANSACTIONS = 500;
const FLUSH_INTERVAL_MS = 120;
// How long to wait after spotting a sequence gap before asking the server what's missing.
// Cross-pod broadcasts can arrive slightly out of order (pod A's push landing after pod B's,
// purely from network/scheduling jitter, even though both were written to Postgres in the
// correct order) - a short debounce lets a merely-reordered item arrive and close the "gap" on
// its own before this fires a network round trip for something that was never actually lost.
const GAP_CHECK_DEBOUNCE_MS = 500;
// Sanity ceiling on how many chained /since/{sequence} calls one catch-up will make - the loop
// already terminates naturally once a call returns nothing, this only guards against an
// unbounded request storm if that contract were ever violated.
const MAX_CATCH_UP_ITERATIONS = 20;

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
  // Highest sequence seen so far (from the initial snapshot or any push) - used to ask
  // "what did I miss?" both on reconnect and on a live-detected gap.
  const lastSequenceRef = useRef<number>(0);
  const gapCheckTimerRef = useRef<number | null>(null);
  // Lowest known-good sequence before a suspected gap - kept as the earliest one seen so a
  // later, smaller gap never overwrites an already-pending earlier one (the earlier range is a
  // superset of the later one).
  const gapSinceSequenceRef = useRef<number | null>(null);

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

  // Pushes a batch into the render queue and advances lastSequenceRef, but does no gap
  // detection of its own - used for catch-up batches (already-known ranges fetched from the
  // server), as opposed to enqueue() below, which is for live pushes and layers gap detection
  // on top of this.
  const enqueueMany = useCallback(
    (incoming: Transaction[]) => {
      if (incoming.length === 0) return;
      incoming.forEach((t) => {
        lastSequenceRef.current = Math.max(lastSequenceRef.current, t.sequence);
        queueRef.current.push(t);
      });
      if (flushTimerRef.current === null) {
        flushTimerRef.current = window.setTimeout(flush, FLUSH_INTERVAL_MS);
      }
    },
    [flush],
  );

  // /since/{sequence} is capped server-side (MaxCatchUpBatch, currently 1000) - a single call
  // can't be assumed to close a gap larger than that. Keeps pulling from the highest sequence
  // actually returned until a call comes back empty, without needing to know the server's cap
  // value: a full-looking batch just means "ask again from here," not "that was everything."
  const catchUpFrom = useCallback(
    async (sinceSequence: number) => {
      let since = sinceSequence;
      for (let i = 0; i < MAX_CATCH_UP_ITERATIONS; i++) {
        let missed: Transaction[];
        try {
          missed = await getTransactionsSince(since);
        } catch {
          // Best-effort: whatever's still missing will be caught by the next live push's gap
          // check, or the next reconnect.
          return;
        }
        if (missed.length === 0) return;
        enqueueMany(missed);
        since = missed.reduce((max, t) => Math.max(max, t.sequence), since);
      }
    },
    [enqueueMany],
  );

  // Debounced gap detection: fetches everything after `sinceSequence` once the window closes
  // without a smaller gap superseding it. A duplicate item this returns is harmless, since
  // enqueueMany's downstream flush() dedupes by transactionId anyway.
  const scheduleGapCheck = useCallback(
    (sinceSequence: number) => {
      if (gapSinceSequenceRef.current === null || sinceSequence < gapSinceSequenceRef.current) {
        gapSinceSequenceRef.current = sinceSequence;
      }
      if (gapCheckTimerRef.current !== null) {
        window.clearTimeout(gapCheckTimerRef.current);
      }
      gapCheckTimerRef.current = window.setTimeout(() => {
        gapCheckTimerRef.current = null;
        const since = gapSinceSequenceRef.current;
        gapSinceSequenceRef.current = null;
        if (since === null) return;
        void catchUpFrom(since);
      }, GAP_CHECK_DEBOUNCE_MS);
    },
    [catchUpFrom],
  );

  // Live pushes from SignalR go through here. Unlike a dropped connection (which
  // onreconnected below already handles), two failure modes never trip a disconnect at all:
  // TransactionBroadcastQueue's bounded channel drops the oldest still-queued item under
  // sustained overload (FullMode.DropOldest), and TransactionBroadcastWorker swallows a broadcast
  // failure (e.g. a Redis hiccup) rather than crash the loop - in both cases the WebSocket stays
  // fully connected throughout, so onreconnected never fires and would never catch this. Every
  // push carries its own sequence number, so a gap is visible immediately, without waiting for a
  // reconnect that may never come.
  const enqueue = useCallback(
    (transaction: Transaction) => {
      if (lastSequenceRef.current > 0 && transaction.sequence > lastSequenceRef.current + 1) {
        scheduleGapCheck(lastSequenceRef.current);
      }
      enqueueMany([transaction]);
    },
    [enqueueMany, scheduleGapCheck],
  );

  useEffect(() => {
    let isMounted = true;
    const connection = createHubConnection();

    connection.on(TRANSACTION_RECEIVED_EVENT, (transaction: Transaction) => enqueue(transaction));
    connection.onreconnecting(() => isMounted && setStatus("reconnecting"));
    connection.onclose(() => isMounted && setStatus("disconnected"));

    // A dropped connection (network blip, backplane hiccup) can mean broadcasts were sent while
    // this client wasn't listening - the automatic reconnect handles the transport, but doesn't
    // itself re-deliver what was missed. Catching up via /since/{sequence} closes that gap
    // instead of silently leaving holes in the dashboard. This is the reconnect-triggered half of
    // catch-up; enqueue()'s gap detection above is the half that covers a connection that never
    // actually drops.
    connection.onreconnected(() => {
      if (!isMounted) return;
      setStatus("connected");
      void catchUpFrom(lastSequenceRef.current);
    });

    (async () => {
      try {
        const { items: snapshot } = await getTransactions();
        if (!isMounted) return;
        snapshot.forEach((t) => {
          seenIdsRef.current.add(t.transactionId);
          lastSequenceRef.current = Math.max(lastSequenceRef.current, t.sequence);
        });
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
      if (gapCheckTimerRef.current !== null) window.clearTimeout(gapCheckTimerRef.current);
      connection.stop();
    };
  }, [enqueue, catchUpFrom]);

  return { transactions, status };
}
