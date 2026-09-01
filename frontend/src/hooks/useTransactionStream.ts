import { useCallback, useEffect, useRef, useState } from "react";
import { createHubConnection, TRANSACTION_RECEIVED_EVENT } from "../api/signalrClient";
import { getTransactions } from "../api/transactionsApi";
import type { Transaction } from "../types/transaction";

const MAX_TRANSACTIONS = 1000;
const FLUSH_INTERVAL_MS = 120;
// Self-healing safety net: a dropped connection is handled by onreconnected below, but two
// failure modes never trip a disconnect at all - TransactionBroadcastQueue's bounded channel
// dropping the oldest still-queued item under sustained overload, and TransactionBroadcastWorker
// swallowing a broadcast failure (e.g. a Redis hiccup) rather than crash the loop. A periodic
// resync catches those silent gaps too, now that resync is cheap (Redis-backed on the server).
const RESYNC_INTERVAL_MS = 30_000;

export type ConnectionStatus = "connecting" | "connected" | "reconnecting" | "disconnected";

// Incoming SignalR messages are buffered in a ref and flushed on a fixed interval instead of
// re-rendering per message, so a burst of 100 near-simultaneous transactions costs a handful of
// renders, not 100 - this is what keeps the UI responsive under the "no freeze" requirement.
export function useTransactionStream() {
  const [transactions, setTransactions] = useState<Transaction[]>([]);
  const [status, setStatus] = useState<ConnectionStatus>("connecting");
  // Cursor for paging further back than the live window below, from the *last* page fetched via
  // loadMore (or the initial snapshot, before any loadMore call) - null once the server reports
  // no more (or before the first fetch resolves). Deliberately separate from resync()'s cursor-
  // less "give me the fresh head" calls, which never advance or reset this: paging backward
  // through history and refreshing the live head are independent concerns.
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [isLoadingMore, setIsLoadingMore] = useState(false);
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

    // Same (Timestamp DESC, TransactionId DESC) ordering GetRecentAsync uses everywhere else on
    // the server - keeps render order correct even when cross-pod live pushes arrive slightly
    // out of order (pod A's push landing after pod B's, purely from network/scheduling jitter,
    // even though both were written to Postgres in the correct order), without needing a
    // monotonic sequence number to detect/correct it.
    setTransactions((prev) => {
      const merged = [...fresh, ...prev];
      merged.sort((a, b) => {
        const byTime = new Date(b.timestamp).getTime() - new Date(a.timestamp).getTime();
        return byTime !== 0 ? byTime : b.transactionId.localeCompare(a.transactionId);
      });
      // Once loadMore has grown the list past the live window, stop enforcing the cap entirely
      // (keep every item, including whatever this flush just added) - a live push landing
      // afterward must not silently discard older history the user explicitly asked to see.
      // Below that point, the normal cap still applies as usual.
      const cap = prev.length > MAX_TRANSACTIONS ? merged.length : MAX_TRANSACTIONS;
      return merged.slice(0, cap);
    });
  }, []);

  // Fetches the next older page (via the cursor from the initial snapshot or the previous
  // loadMore call) and appends it after what's currently shown. A plain append rather than a
  // merge-and-sort like flush() above is correct here: the cursor contract guarantees every item
  // in this page is strictly older than everything already displayed, so appending preserves the
  // overall (Timestamp DESC, TransactionId DESC) ordering without needing to re-sort. Still
  // dedupes against seenIdsRef for safety, since it's the same id-space live pushes/resyncs use.
  const loadMore = useCallback(async () => {
    if (nextCursor === null || isLoadingMore) return;
    setIsLoadingMore(true);
    try {
      const { items, nextCursor: newCursor } = await getTransactions(nextCursor);
      const fresh = items.filter((t) => {
        if (seenIdsRef.current.has(t.transactionId)) return false;
        seenIdsRef.current.add(t.transactionId);
        return true;
      });
      if (fresh.length > 0) {
        setTransactions((prev) => [...prev, ...fresh]);
      }
      setNextCursor(newCursor);
    } catch {
      // Best-effort: leave nextCursor as-is so the user can just try "Load more" again.
    } finally {
      setIsLoadingMore(false);
    }
  }, [nextCursor, isLoadingMore]);

  const enqueueMany = useCallback(
    (incoming: Transaction[]) => {
      if (incoming.length === 0) return;
      queueRef.current.push(...incoming);
      if (flushTimerRef.current === null) {
        flushTimerRef.current = window.setTimeout(flush, FLUSH_INTERVAL_MS);
      }
    },
    [flush],
  );

  // The whole "did I miss anything" story now: just re-fetch the recent list (cheap - Redis-
  // backed on the server) and merge it in. Used on reconnect, on a periodic tick, and (via a
  // direct snapshot seed, not this function) on initial mount. Routed through enqueueMany rather
  // than a blind replace so a live push that arrives while this is in flight can never be
  // clobbered - flush()'s dedup-by-transactionId and sort-by-timestamp merge it in correctly
  // regardless of arrival order.
  const resync = useCallback(async () => {
    try {
      const { items: snapshot } = await getTransactions();
      enqueueMany(snapshot);
    } catch {
      // Best-effort: a failed resync just means this attempt didn't refresh anything - the next
      // reconnect, periodic tick, or live push will eventually catch things up.
    }
  }, [enqueueMany]);

  useEffect(() => {
    let isMounted = true;
    const connection = createHubConnection();

    connection.on(TRANSACTION_RECEIVED_EVENT, (transaction: Transaction) => enqueueMany([transaction]));
    connection.onreconnecting(() => isMounted && setStatus("reconnecting"));
    connection.onclose(() => isMounted && setStatus("disconnected"));

    // A dropped connection (network blip, backplane hiccup) can mean broadcasts were sent while
    // this client wasn't listening - the automatic reconnect handles the transport, but doesn't
    // itself re-deliver what was missed. Re-fetching the recent snapshot closes that gap.
    connection.onreconnected(() => {
      if (!isMounted) return;
      setStatus("connected");
      void resync();
    });

    let resyncTimer: number | null = null;
    const scheduleNextResync = () => {
      resyncTimer = window.setTimeout(async () => {
        await resync();
        if (isMounted) scheduleNextResync();
      }, RESYNC_INTERVAL_MS);
    };

    (async () => {
      try {
        const { items: snapshot, nextCursor: initialCursor } = await getTransactions();
        if (!isMounted) return;
        seenIdsRef.current = new Set(snapshot.map((t) => t.transactionId));
        setTransactions(snapshot.slice(0, MAX_TRANSACTIONS));
        setNextCursor(initialCursor);

        await connection.start();
        if (isMounted) {
          setStatus("connected");
          scheduleNextResync();
        }
      } catch {
        if (isMounted) setStatus("disconnected");
      }
    })();

    return () => {
      isMounted = false;
      if (flushTimerRef.current !== null) window.clearTimeout(flushTimerRef.current);
      if (resyncTimer !== null) window.clearTimeout(resyncTimer);
      connection.stop();
    };
  }, [enqueueMany, resync]);

  return { transactions, status, loadMore, isLoadingMore, hasMore: nextCursor !== null };
}
