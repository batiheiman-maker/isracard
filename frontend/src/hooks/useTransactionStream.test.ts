import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  useTransactionStream,
  MAX_CATCH_UP_ITERATIONS,
  MAX_CATCH_UP_RESUME_CYCLES,
  CATCH_UP_RESUME_DELAY_MS,
} from "./useTransactionStream";
import { createHubConnection, TRANSACTION_RECEIVED_EVENT } from "../api/signalrClient";
import { getTransactions, getTransactionsSince } from "../api/transactionsApi";
import type { Transaction } from "../types/transaction";

vi.mock("../api/signalrClient");
vi.mock("../api/transactionsApi");

// Deliberately not using @testing-library/react's waitFor anywhere in this file: it polls via
// its own internal timer, which never progresses under vi.useFakeTimers() (confirmed directly -
// it hangs for the full test timeout even when the awaited condition is already true at the
// moment waitFor is called). Every assertion below instead follows a vi.advanceTimersByTimeAsync
// call that has already flushed exactly the timers/microtasks the assertion depends on, so a
// plain expect() is both correct and sufficient.

const FLUSH_INTERVAL_MS = 120; // mirrors the hook's own private constant

function makeTransaction(overrides: Partial<Transaction>): Transaction {
  return {
    transactionId: "id",
    amount: 10,
    currency: "USD",
    status: "Completed",
    timestamp: "2026-08-31T00:00:00.000Z",
    sequence: 1,
    ...overrides,
  };
}

function createMockConnection() {
  const handlers: Record<string, (...args: any[]) => void> = {};
  const connection = {
    on: vi.fn((event: string, cb: (...args: any[]) => void) => {
      handlers[event] = cb;
    }),
    onreconnecting: vi.fn((cb: () => void) => {
      handlers.reconnecting = cb;
    }),
    onreconnected: vi.fn((cb: () => void) => {
      handlers.reconnected = cb;
    }),
    onclose: vi.fn((cb: () => void) => {
      handlers.close = cb;
    }),
    start: vi.fn().mockResolvedValue(undefined),
    stop: vi.fn().mockResolvedValue(undefined),
  };
  return { connection, handlers };
}

function createDeferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

describe("useTransactionStream", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.resetAllMocks();
  });

  describe("ordering (Finding A)", () => {
    it("does not let an older catch-up batch appear above a newer live transaction", async () => {
      const { connection, handlers } = createMockConnection();
      vi.mocked(createHubConnection).mockReturnValue(connection as any);

      const snapshotTxn = makeTransaction({ transactionId: "t1", sequence: 1 });
      const liveTxn = makeTransaction({ transactionId: "t3", sequence: 3, amount: 30 });
      const missedTxn = makeTransaction({ transactionId: "t2", sequence: 2, amount: 20 });

      vi.mocked(getTransactions).mockResolvedValue({ items: [snapshotTxn], nextCursor: null });
      vi.mocked(getTransactionsSince).mockResolvedValue([missedTxn]);

      const { result } = renderHook(() => useTransactionStream());
      await act(async () => {
        await vi.advanceTimersByTimeAsync(0);
      });
      expect(result.current.status).toBe("connected");
      expect(result.current.transactions).toEqual([snapshotTxn]);

      act(() => {
        handlers[TRANSACTION_RECEIVED_EVENT](liveTxn);
      });
      await act(async () => {
        await vi.advanceTimersByTimeAsync(FLUSH_INTERVAL_MS + 30);
      });
      expect(result.current.transactions.map((t) => t.transactionId)).toEqual(["t3", "t1"]);

      await act(async () => {
        await vi.advanceTimersByTimeAsync(600); // gap-check debounce
      });
      await act(async () => {
        await vi.advanceTimersByTimeAsync(FLUSH_INTERVAL_MS + 30);
      });

      expect(getTransactionsSince).toHaveBeenCalledWith(1);
      // The older catch-up item (t2, sequence 2) must land BELOW the newer live item (t3), not
      // prepended above it - this is the exact bug scenario from Finding A.
      expect(result.current.transactions.map((t) => t.transactionId)).toEqual(["t3", "t2", "t1"]);
    });
  });

  describe("single-flight + bounded resumption (Finding B)", () => {
    it("resumes with a second pass after a pass hits MAX_CATCH_UP_ITERATIONS", async () => {
      const { connection, handlers } = createMockConnection();
      vi.mocked(createHubConnection).mockReturnValue(connection as any);
      vi.mocked(getTransactions).mockResolvedValue({ items: [], nextCursor: null });

      let seq = 0;
      let call = 0;
      vi.mocked(getTransactionsSince).mockImplementation(async () => {
        call += 1;
        if (call <= MAX_CATCH_UP_ITERATIONS) {
          seq += 1;
          return [makeTransaction({ transactionId: `t${seq}`, sequence: seq })];
        }
        return [];
      });

      renderHook(() => useTransactionStream());
      await act(async () => {
        await vi.advanceTimersByTimeAsync(0);
      });

      await act(async () => {
        handlers.reconnected();
        await vi.advanceTimersByTimeAsync(0);
      });
      expect(getTransactionsSince).toHaveBeenCalledTimes(MAX_CATCH_UP_ITERATIONS);

      await act(async () => {
        await vi.advanceTimersByTimeAsync(CATCH_UP_RESUME_DELAY_MS);
      });
      expect(getTransactionsSince).toHaveBeenCalledTimes(MAX_CATCH_UP_ITERATIONS + 1);

      await act(async () => {
        await vi.advanceTimersByTimeAsync(CATCH_UP_RESUME_DELAY_MS * MAX_CATCH_UP_RESUME_CYCLES);
      });
      // No further resumption: pass 2 came back empty, so nothing further should be pending.
      expect(getTransactionsSince).toHaveBeenCalledTimes(MAX_CATCH_UP_ITERATIONS + 1);
    });

    it("coalesces a trigger arriving mid-chain into at most one more pass, never a parallel chain", async () => {
      const { connection, handlers } = createMockConnection();
      vi.mocked(createHubConnection).mockReturnValue(connection as any);
      vi.mocked(getTransactions).mockResolvedValue({ items: [], nextCursor: null });

      const deferredFirstCall = createDeferred<Transaction[]>();
      let callCount = 0;
      vi.mocked(getTransactionsSince).mockImplementation(async () => {
        callCount += 1;
        if (callCount === 1) return deferredFirstCall.promise;
        return [];
      });

      renderHook(() => useTransactionStream());
      await act(async () => {
        await vi.advanceTimersByTimeAsync(0);
      });

      // First trigger: reconnect. Starts the chain and issues call #1, held pending.
      await act(async () => {
        handlers.reconnected();
        await vi.advanceTimersByTimeAsync(0);
      });
      expect(getTransactionsSince).toHaveBeenCalledTimes(1);

      // Second trigger arrives while call #1 is still unresolved - must coalesce, not start a
      // second overlapping chain (i.e. must NOT issue a second call while #1 is still in flight).
      await act(async () => {
        handlers.reconnected();
        await vi.advanceTimersByTimeAsync(0);
      });
      expect(getTransactionsSince).toHaveBeenCalledTimes(1);

      // Resolve the held call with an empty batch - pass 1 completes immediately
      // (moreLikelyPending: false, since runCatchUpPass itself only keeps looping while a batch
      // comes back non-empty), but the coalesced pending flag should still force exactly one
      // more pass.
      await act(async () => {
        deferredFirstCall.resolve([]);
        await vi.advanceTimersByTimeAsync(0);
      });
      await act(async () => {
        await vi.advanceTimersByTimeAsync(CATCH_UP_RESUME_DELAY_MS);
      });

      expect(getTransactionsSince).toHaveBeenCalledTimes(2);

      // Confirm no further, unbounded resumption happened beyond the single coalesced pass.
      await act(async () => {
        await vi.advanceTimersByTimeAsync(CATCH_UP_RESUME_DELAY_MS * MAX_CATCH_UP_RESUME_CYCLES);
      });
      expect(getTransactionsSince).toHaveBeenCalledTimes(2);
    });

    it("reaches the expected final sequence with no duplicates after a multi-pass catch-up", async () => {
      const { connection, handlers } = createMockConnection();
      vi.mocked(createHubConnection).mockReturnValue(connection as any);
      vi.mocked(getTransactions).mockResolvedValue({ items: [], nextCursor: null });

      // 25 missing items: pass 1 delivers MAX_CATCH_UP_ITERATIONS batches of 1 (hits the cap),
      // pass 2 delivers the remaining 5 in one batch, then empty.
      const total = MAX_CATCH_UP_ITERATIONS + 5;
      let seq = 0;
      let call = 0;
      vi.mocked(getTransactionsSince).mockImplementation(async () => {
        call += 1;
        if (call <= MAX_CATCH_UP_ITERATIONS) {
          seq += 1;
          return [makeTransaction({ transactionId: `t${seq}`, sequence: seq })];
        }
        if (call === MAX_CATCH_UP_ITERATIONS + 1) {
          const batch: Transaction[] = [];
          while (seq < total) {
            seq += 1;
            batch.push(makeTransaction({ transactionId: `t${seq}`, sequence: seq }));
          }
          return batch;
        }
        return [];
      });

      const { result } = renderHook(() => useTransactionStream());
      await act(async () => {
        await vi.advanceTimersByTimeAsync(0);
      });

      await act(async () => {
        handlers.reconnected();
        await vi.advanceTimersByTimeAsync(0);
      });
      await act(async () => {
        await vi.advanceTimersByTimeAsync(CATCH_UP_RESUME_DELAY_MS);
      });
      await act(async () => {
        await vi.advanceTimersByTimeAsync(FLUSH_INTERVAL_MS + 30);
      });

      expect(result.current.transactions).toHaveLength(total);

      const ids = result.current.transactions.map((t) => t.transactionId);
      expect(new Set(ids).size).toBe(ids.length); // no duplicates

      const sequences = result.current.transactions.map((t) => t.sequence).sort((a, b) => a - b);
      expect(sequences[0]).toBe(1);
      expect(sequences[sequences.length - 1]).toBe(total);
    });

    it("falls back to a fresh snapshot and resynchronizes when the resume-cycle ceiling is exhausted", async () => {
      const { connection, handlers } = createMockConnection();
      vi.mocked(createHubConnection).mockReturnValue(connection as any);

      const fallbackSnapshot = [
        makeTransaction({ transactionId: "fresh-2", sequence: 200 }),
        makeTransaction({ transactionId: "fresh-1", sequence: 199 }),
      ];
      vi.mocked(getTransactions)
        .mockResolvedValueOnce({ items: [], nextCursor: null }) // initial mount snapshot
        .mockResolvedValueOnce({ items: fallbackSnapshot, nextCursor: null }); // fallback resync

      // Every /since call keeps returning a full, non-empty batch - the chain never naturally
      // catches up, so it must run the entire MAX_CATCH_UP_RESUME_CYCLES * MAX_CATCH_UP_ITERATIONS
      // budget and then fall back, rather than resuming forever.
      let seq = 0;
      vi.mocked(getTransactionsSince).mockImplementation(async () => {
        seq += 1;
        return [makeTransaction({ transactionId: `t${seq}`, sequence: seq })];
      });

      const { result } = renderHook(() => useTransactionStream());
      await act(async () => {
        await vi.advanceTimersByTimeAsync(0);
      });

      await act(async () => {
        handlers.reconnected();
        await vi.advanceTimersByTimeAsync(0);
      });
      await act(async () => {
        await vi.advanceTimersByTimeAsync(
          CATCH_UP_RESUME_DELAY_MS * MAX_CATCH_UP_RESUME_CYCLES + 100,
        );
      });

      // Ran to full exhaustion (not more, not less), then fell back to a second snapshot fetch.
      expect(getTransactionsSince).toHaveBeenCalledTimes(
        MAX_CATCH_UP_RESUME_CYCLES * MAX_CATCH_UP_ITERATIONS,
      );
      expect(getTransactions).toHaveBeenCalledTimes(2);
      await act(async () => {
        await vi.advanceTimersByTimeAsync(FLUSH_INTERVAL_MS + 30);
      });

      // The fallback snapshot is merged in, not swapped in place of what's there: the exhausted
      // chain's own already-delivered partial progress (t1..t100, from its 100 successful /since
      // calls) is real, durable data - resyncFromSnapshot must not discard it, only fill in what
      // it couldn't reach. Both the fresh snapshot items and the full partial backlog are present,
      // deduplicated, and correctly ordered by sequence (highest first).
      const ids = result.current.transactions.map((t) => t.transactionId);
      expect(ids).toHaveLength(MAX_CATCH_UP_RESUME_CYCLES * MAX_CATCH_UP_ITERATIONS + 2); // 100 + 2
      expect(new Set(ids).size).toBe(ids.length); // no duplicates
      expect(ids[0]).toBe("fresh-2"); // sequence 200 - highest, sorted first
      expect(ids[1]).toBe("fresh-1"); // sequence 199
      expect(ids).toContain("t100"); // the exhausted chain's own progress, preserved not discarded

      // lastSequenceRef was reset to the fallback snapshot's own high-water mark (200), not left
      // at wherever the exhausted chain's own `since` had reached - proven indirectly: a live
      // push exactly contiguous with that mark must not be treated as a gap.
      const sinceCallsSoFar = vi.mocked(getTransactionsSince).mock.calls.length;
      act(() => {
        handlers[TRANSACTION_RECEIVED_EVENT](
          makeTransaction({ transactionId: "next", sequence: 201 }),
        );
      });
      await act(async () => {
        await vi.advanceTimersByTimeAsync(FLUSH_INTERVAL_MS + 30);
      });
      expect(getTransactionsSince).toHaveBeenCalledTimes(sinceCallsSoFar);
    });

    it("does not lose a live transaction that arrives while the fallback snapshot request is in flight", async () => {
      const { connection, handlers } = createMockConnection();
      vi.mocked(createHubConnection).mockReturnValue(connection as any);

      // Every /since call keeps returning a full, non-empty batch - forces the chain to exhaust
      // MAX_CATCH_UP_RESUME_CYCLES and trigger the snapshot fallback.
      let seq = 0;
      vi.mocked(getTransactionsSince).mockImplementation(async () => {
        seq += 1;
        return [makeTransaction({ transactionId: `t${seq}`, sequence: seq })];
      });

      const deferredSnapshot = createDeferred<{ items: Transaction[]; nextCursor: string | null }>();
      vi.mocked(getTransactions)
        .mockResolvedValueOnce({ items: [], nextCursor: null }) // initial mount snapshot
        .mockReturnValueOnce(deferredSnapshot.promise); // fallback resync - held pending

      const { result } = renderHook(() => useTransactionStream());
      await act(async () => {
        await vi.advanceTimersByTimeAsync(0);
      });

      await act(async () => {
        handlers.reconnected();
        await vi.advanceTimersByTimeAsync(0);
      });
      await act(async () => {
        await vi.advanceTimersByTimeAsync(
          CATCH_UP_RESUME_DELAY_MS * MAX_CATCH_UP_RESUME_CYCLES + 100,
        );
      });

      // The fallback snapshot request has started but is being held unresolved.
      expect(getTransactions).toHaveBeenCalledTimes(2);

      // A newer live transaction arrives WHILE the snapshot request is still in flight.
      const liveTxn = makeTransaction({ transactionId: "live-during-fetch", sequence: 500 });
      act(() => {
        handlers[TRANSACTION_RECEIVED_EVENT](liveTxn);
      });
      await act(async () => {
        await vi.advanceTimersByTimeAsync(FLUSH_INTERVAL_MS + 30);
      });

      // Already visible before the snapshot resolves.
      expect(result.current.transactions.map((t) => t.transactionId)).toContain(
        "live-during-fetch",
      );

      // The snapshot resolves WITHOUT that transaction - simulating the race: the DB read behind
      // this snapshot happened just before the live write became visible.
      const fallbackSnapshot = [
        makeTransaction({ transactionId: "fresh-2", sequence: 200 }),
        makeTransaction({ transactionId: "fresh-1", sequence: 199 }),
      ];
      await act(async () => {
        deferredSnapshot.resolve({ items: fallbackSnapshot, nextCursor: null });
        await vi.advanceTimersByTimeAsync(0);
      });
      await act(async () => {
        await vi.advanceTimersByTimeAsync(FLUSH_INTERVAL_MS + 30);
      });

      // The live transaction must still be present - the snapshot must not have erased it - and
      // the snapshot's own items must still have been merged in correctly (gap still recovered).
      const ids = result.current.transactions.map((t) => t.transactionId);
      expect(ids).toContain("live-during-fetch");
      expect(ids).toContain("fresh-2");
      expect(ids).toContain("fresh-1");

      // lastSequenceRef must not have regressed to the snapshot's lower max (200): a subsequent
      // live push exactly contiguous with the live transaction's own sequence (500) must not be
      // treated as a gap.
      const sinceCallsSoFar = vi.mocked(getTransactionsSince).mock.calls.length;
      act(() => {
        handlers[TRANSACTION_RECEIVED_EVENT](
          makeTransaction({ transactionId: "next", sequence: 501 }),
        );
      });
      await act(async () => {
        await vi.advanceTimersByTimeAsync(FLUSH_INTERVAL_MS + 30);
      });
      expect(getTransactionsSince).toHaveBeenCalledTimes(sinceCallsSoFar);
    });
  });
});
