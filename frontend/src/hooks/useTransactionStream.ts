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
// Sanity ceiling on how many chained /since/{sequence} calls ONE pass will make.
export const MAX_CATCH_UP_ITERATIONS = 20;
// Sanity ceiling on how many passes a single catch-up chain will chain together when each pass
// keeps coming back "there's probably more" (hit MAX_CATCH_UP_ITERATIONS with the last batch
// still non-empty) or another trigger coalesced in while it ran. Bounds worst-case chained
// /since calls to MAX_CATCH_UP_RESUME_CYCLES * MAX_CATCH_UP_ITERATIONS (100 in the default
// configuration) before falling back to a full snapshot resync (resyncFromSnapshot) instead of
// continuing to chase an ever-growing backlog indefinitely.
export const MAX_CATCH_UP_RESUME_CYCLES = 5;
// Brief pause between chained passes within one catch-up chain - distinct from
// GAP_CHECK_DEBOUNCE_MS (which exists to let reordered live pushes settle, not to pace backlog
// draining) so tuning one doesn't silently retune the other.
export const CATCH_UP_RESUME_DELAY_MS = 500;

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => window.setTimeout(resolve, ms));
}

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
  // Single-flight guards for catchUpFrom below: catchUpInFlightRef is true while any chain (one
  // or more passes) is running; catchUpPendingRef records "something asked for another look
  // while busy" so that demand isn't lost without spawning a second parallel chain.
  const catchUpInFlightRef = useRef(false);
  const catchUpPendingRef = useRef(false);

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

    // Merge and sort by sequence rather than assuming arrival order equals sequence order: a
    // catch-up batch delivers items OLDER than what's already rendered (it fills gaps below an
    // already-shown, higher-sequence live push), so blindly prepending it - as a plain
    // reverse-and-prepend would - puts older transactions visually above newer ones.
    setTransactions((prev) => {
      const merged = [...fresh, ...prev];
      merged.sort((a, b) => b.sequence - a.sequence);
      return merged.slice(0, MAX_TRANSACTIONS);
    });
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

  // One bounded pass: chains /since calls from sinceSequence until a call returns empty (nothing
  // more missing) or the per-pass cap is hit. Returns the highest sequence this pass itself
  // confirmed, and whether the cap was hit while data was still flowing (every call in this pass
  // came back non-empty) - the single-flight controller below decides whether to resume.
  const runCatchUpPass = useCallback(
    async (sinceSequence: number): Promise<{ since: number; moreLikelyPending: boolean }> => {
      let since = sinceSequence;
      for (let i = 0; i < MAX_CATCH_UP_ITERATIONS; i++) {
        let missed: Transaction[];
        try {
          missed = await getTransactionsSince(since);
        } catch {
          // Best-effort: whatever's still missing will be caught by the next live push's gap
          // check, or the next reconnect.
          return { since, moreLikelyPending: false };
        }
        if (missed.length === 0) return { since, moreLikelyPending: false };
        enqueueMany(missed);
        since = missed.reduce((max, t) => Math.max(max, t.sequence), since);
      }
      return { since, moreLikelyPending: true };
    },
    [enqueueMany],
  );

  // Falls back to a full refresh via getTransactions() (the same call the initial mount uses)
  // when a catch-up chain exhausts MAX_CATCH_UP_RESUME_CYCLES without ever catching up - giving
  // up silently there would just reproduce the original permanent-incompleteness problem at a
  // larger threshold. Routes the result through enqueueMany rather than replacing state
  // directly: getTransactions() takes real time to resolve, and a live SignalR push can arrive
  // while it's in flight. A blind replace once it resolves would risk erasing that
  // already-received live transaction (if the snapshot was read just before the live write
  // became visible) and could regress lastSequenceRef backward. Merging via enqueueMany makes
  // that structurally impossible instead of merely unlikely: lastSequenceRef only ever advances
  // (Math.max, per item), seenIdsRef only ever grows, and flush()'s sort-by-sequence-descending
  // merges the snapshot with whatever's already rendered rather than discarding it.
  const resyncFromSnapshot = useCallback(async () => {
    try {
      const { items: snapshot } = await getTransactions();
      enqueueMany(snapshot);
    } catch {
      // Best-effort: if even the fallback snapshot fails, the next live push's own gap check (or
      // the next reconnect) will try catch-up again from wherever lastSequenceRef already is.
    }
  }, [enqueueMany]);

  // /since/{sequence} is capped server-side - a single call, or even one bounded pass, can't be
  // assumed to close an arbitrarily large gap. This is the single-flight controller: gap
  // detection, reconnect, and pass-exhaustion resumption all funnel through here, and at most
  // one chain of passes ever runs at a time. A caller that arrives while a chain is already
  // running doesn't start a second parallel chain - it just marks that another look is wanted
  // (catchUpPendingRef), which the running chain picks up on its own next cycle using the
  // chain's own accumulated `since` (never the coalesced caller's, which is always >= where the
  // chain already is, and would risk skipping a range never delivered live).
  //
  // Not async: if it were, its returned Promise would resolve right after the synchronous
  // claim-or-coalesce check, well before the real work (which runs in an unawaited IIFE)
  // finishes - a misleading signal. Plain void return makes "fire-and-forget" explicit.
  const catchUpFrom = useCallback(
    (sinceSequence: number): void => {
      if (catchUpInFlightRef.current) {
        catchUpPendingRef.current = true;
        return;
      }
      catchUpInFlightRef.current = true;
      catchUpPendingRef.current = false;

      void (async () => {
        let since = sinceSequence;
        let exhausted = false;
        for (let cycle = 0; cycle < MAX_CATCH_UP_RESUME_CYCLES; cycle++) {
          catchUpPendingRef.current = false;
          const result = await runCatchUpPass(since);
          since = result.since;

          const shouldResume = result.moreLikelyPending || catchUpPendingRef.current;
          if (!shouldResume) break;
          if (cycle === MAX_CATCH_UP_RESUME_CYCLES - 1) {
            exhausted = true;
            break;
          }
          await delay(CATCH_UP_RESUME_DELAY_MS);
        }

        if (exhausted) {
          await resyncFromSnapshot();
        }

        catchUpInFlightRef.current = false;
        catchUpPendingRef.current = false;
      })();
    },
    [runCatchUpPass, resyncFromSnapshot],
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
        catchUpFrom(since);
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
      catchUpFrom(lastSequenceRef.current);
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
