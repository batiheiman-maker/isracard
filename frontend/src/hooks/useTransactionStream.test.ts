import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useTransactionStream } from "./useTransactionStream";
import { createHubConnection, TRANSACTION_RECEIVED_EVENT } from "../api/signalrClient";
import { getTransactions } from "../api/transactionsApi";
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
const RESYNC_INTERVAL_MS = 30_000; // mirrors the hook's own private constant
const MAX_TRANSACTIONS = 1000; // mirrors the hook's own private constant

function makeTransaction(overrides: Partial<Transaction>): Transaction {
  return {
    transactionId: "id",
    amount: 10,
    currency: "USD",
    status: "Completed",
    timestamp: "2026-08-31T00:00:00.000Z",
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

  it("loads the initial snapshot on mount and reports connected", async () => {
    const { connection } = createMockConnection();
    vi.mocked(createHubConnection).mockReturnValue(connection as any);
    const snapshotTxn = makeTransaction({ transactionId: "t1" });
    vi.mocked(getTransactions).mockResolvedValue({ items: [snapshotTxn], nextCursor: null });

    const { result } = renderHook(() => useTransactionStream());
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });

    expect(result.current.status).toBe("connected");
    expect(result.current.transactions).toEqual([snapshotTxn]);
    expect(connection.start).toHaveBeenCalledTimes(1);
  });

  it("adds a live-pushed transaction and dedupes a repeat of the same id", async () => {
    const { connection, handlers } = createMockConnection();
    vi.mocked(createHubConnection).mockReturnValue(connection as any);
    vi.mocked(getTransactions).mockResolvedValue({ items: [], nextCursor: null });

    const { result } = renderHook(() => useTransactionStream());
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });

    const liveTxn = makeTransaction({ transactionId: "live-1" });
    act(() => {
      handlers[TRANSACTION_RECEIVED_EVENT](liveTxn);
      handlers[TRANSACTION_RECEIVED_EVENT](liveTxn); // duplicate push of the same transaction
    });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(FLUSH_INTERVAL_MS + 30);
    });

    expect(result.current.transactions.map((t) => t.transactionId)).toEqual(["live-1"]);
  });

  it("orders transactions by timestamp descending, tie-broken by transactionId descending", async () => {
    const { connection, handlers } = createMockConnection();
    vi.mocked(createHubConnection).mockReturnValue(connection as any);
    const older = makeTransaction({ transactionId: "older", timestamp: "2026-08-31T00:00:00.000Z" });
    vi.mocked(getTransactions).mockResolvedValue({ items: [older], nextCursor: null });

    const { result } = renderHook(() => useTransactionStream());
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });

    // Two live pushes with the same timestamp as each other, both newer than the snapshot item -
    // arrival order is intentionally reversed from the expected tie-broken sort order, so this
    // only passes if the hook actually sorts rather than just prepending in arrival order.
    const sameInstantA = makeTransaction({
      transactionId: "aaa",
      timestamp: "2026-08-31T00:01:00.000Z",
    });
    const sameInstantB = makeTransaction({
      transactionId: "bbb",
      timestamp: "2026-08-31T00:01:00.000Z",
    });
    act(() => {
      handlers[TRANSACTION_RECEIVED_EVENT](sameInstantA);
      handlers[TRANSACTION_RECEIVED_EVENT](sameInstantB);
    });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(FLUSH_INTERVAL_MS + 30);
    });

    expect(result.current.transactions.map((t) => t.transactionId)).toEqual(["bbb", "aaa", "older"]);
  });

  it("caps rendered transactions at MAX_TRANSACTIONS, dropping the oldest", async () => {
    const { connection, handlers } = createMockConnection();
    vi.mocked(createHubConnection).mockReturnValue(connection as any);
    const base = new Date("2026-08-31T00:00:00.000Z").getTime();
    // Oldest-first snapshot of exactly MAX_TRANSACTIONS items.
    const snapshot = Array.from({ length: MAX_TRANSACTIONS }, (_, i) =>
      makeTransaction({
        transactionId: `s${i}`,
        timestamp: new Date(base + i * 1000).toISOString(),
      }),
    ).reverse();
    vi.mocked(getTransactions).mockResolvedValue({ items: snapshot, nextCursor: null });

    const { result } = renderHook(() => useTransactionStream());
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(result.current.transactions).toHaveLength(MAX_TRANSACTIONS);

    const newestLive = makeTransaction({
      transactionId: "newest",
      timestamp: new Date(base + MAX_TRANSACTIONS * 1000).toISOString(),
    });
    act(() => {
      handlers[TRANSACTION_RECEIVED_EVENT](newestLive);
    });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(FLUSH_INTERVAL_MS + 30);
    });

    expect(result.current.transactions).toHaveLength(MAX_TRANSACTIONS);
    expect(result.current.transactions[0].transactionId).toBe("newest");
    // The single oldest snapshot item (s0, pushed out by the newest live push) is gone.
    expect(result.current.transactions.map((t) => t.transactionId)).not.toContain("s0");
  });

  it("re-fetches the recent list on reconnect and merges it in", async () => {
    const { connection, handlers } = createMockConnection();
    vi.mocked(createHubConnection).mockReturnValue(connection as any);
    const initial = makeTransaction({ transactionId: "initial" });
    const resynced = makeTransaction({ transactionId: "resynced" });
    vi.mocked(getTransactions)
      .mockResolvedValueOnce({ items: [initial], nextCursor: null }) // initial mount snapshot
      .mockResolvedValueOnce({ items: [initial, resynced], nextCursor: null }); // reconnect resync

    const { result } = renderHook(() => useTransactionStream());
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });

    await act(async () => {
      handlers.reconnected();
      await vi.advanceTimersByTimeAsync(0);
    });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(FLUSH_INTERVAL_MS + 30);
    });

    expect(getTransactions).toHaveBeenCalledTimes(2);
    expect(result.current.status).toBe("connected");
    expect(result.current.transactions.map((t) => t.transactionId).sort()).toEqual(
      ["initial", "resynced"].sort(),
    );
  });

  it("does not lose a live transaction that arrives while a reconnect resync is in flight", async () => {
    const { connection, handlers } = createMockConnection();
    vi.mocked(createHubConnection).mockReturnValue(connection as any);

    const deferredResync = createDeferred<{ items: Transaction[]; nextCursor: string | null }>();
    vi.mocked(getTransactions)
      .mockResolvedValueOnce({ items: [], nextCursor: null }) // initial mount snapshot
      .mockReturnValueOnce(deferredResync.promise); // reconnect resync - held pending

    const { result } = renderHook(() => useTransactionStream());
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });

    await act(async () => {
      handlers.reconnected();
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(getTransactions).toHaveBeenCalledTimes(2);

    // A live transaction arrives WHILE the resync request is still in flight.
    const liveTxn = makeTransaction({
      transactionId: "live-during-fetch",
      timestamp: "2026-08-31T00:05:00.000Z",
    });
    act(() => {
      handlers[TRANSACTION_RECEIVED_EVENT](liveTxn);
    });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(FLUSH_INTERVAL_MS + 30);
    });
    expect(result.current.transactions.map((t) => t.transactionId)).toContain("live-during-fetch");

    // The resync resolves WITHOUT that transaction - simulating the race where the DB read
    // behind it happened just before the live write became visible.
    const resyncedTxn = makeTransaction({
      transactionId: "resynced",
      timestamp: "2026-08-31T00:04:00.000Z",
    });
    await act(async () => {
      deferredResync.resolve({ items: [resyncedTxn], nextCursor: null });
      await vi.advanceTimersByTimeAsync(0);
    });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(FLUSH_INTERVAL_MS + 30);
    });

    const ids = result.current.transactions.map((t) => t.transactionId);
    expect(ids).toContain("live-during-fetch");
    expect(ids).toContain("resynced");
  });

  it("hasMore reflects the initial snapshot's cursor, and loadMore fetches and appends the next page", async () => {
    const { connection } = createMockConnection();
    vi.mocked(createHubConnection).mockReturnValue(connection as any);
    const initial = makeTransaction({ transactionId: "initial" });
    const older = makeTransaction({ transactionId: "older" });
    vi.mocked(getTransactions)
      .mockResolvedValueOnce({ items: [initial], nextCursor: "cursor-1" }) // initial mount snapshot
      .mockResolvedValueOnce({ items: [older], nextCursor: null }); // loadMore page

    const { result } = renderHook(() => useTransactionStream());
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(result.current.hasMore).toBe(true);

    await act(async () => {
      await result.current.loadMore();
    });

    expect(getTransactions).toHaveBeenLastCalledWith("cursor-1");
    expect(result.current.transactions.map((t) => t.transactionId)).toEqual(["initial", "older"]);
    expect(result.current.hasMore).toBe(false);
  });

  it("loadMore is a no-op once there is nothing more to load", async () => {
    const { connection } = createMockConnection();
    vi.mocked(createHubConnection).mockReturnValue(connection as any);
    vi.mocked(getTransactions).mockResolvedValue({ items: [], nextCursor: null });

    const { result } = renderHook(() => useTransactionStream());
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(result.current.hasMore).toBe(false);

    await act(async () => {
      await result.current.loadMore();
    });

    expect(getTransactions).toHaveBeenCalledTimes(1); // only the initial snapshot call
  });

  it("loadMore dedupes against transactions already shown", async () => {
    const { connection } = createMockConnection();
    vi.mocked(createHubConnection).mockReturnValue(connection as any);
    const initial = makeTransaction({ transactionId: "initial" });
    vi.mocked(getTransactions)
      .mockResolvedValueOnce({ items: [initial], nextCursor: "cursor-1" })
      .mockResolvedValueOnce({ items: [initial], nextCursor: null }); // overlapping page

    const { result } = renderHook(() => useTransactionStream());
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });

    await act(async () => {
      await result.current.loadMore();
    });

    expect(result.current.transactions.map((t) => t.transactionId)).toEqual(["initial"]);
  });

  it("does not truncate history loaded via loadMore when a later live push triggers a flush", async () => {
    const { connection, handlers } = createMockConnection();
    vi.mocked(createHubConnection).mockReturnValue(connection as any);
    const base = new Date("2026-08-31T00:00:00.000Z").getTime();
    const snapshot = Array.from({ length: MAX_TRANSACTIONS }, (_, i) =>
      makeTransaction({ transactionId: `s${i}`, timestamp: new Date(base + i * 1000).toISOString() }),
    ).reverse();
    const older = Array.from({ length: 5 }, (_, i) =>
      makeTransaction({ transactionId: `o${i}`, timestamp: new Date(base - (i + 1) * 1000).toISOString() }),
    );
    vi.mocked(getTransactions)
      .mockResolvedValueOnce({ items: snapshot, nextCursor: "cursor-1" })
      .mockResolvedValueOnce({ items: older, nextCursor: null });

    const { result } = renderHook(() => useTransactionStream());
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });

    await act(async () => {
      await result.current.loadMore();
    });
    expect(result.current.transactions).toHaveLength(MAX_TRANSACTIONS + 5);

    act(() => {
      handlers[TRANSACTION_RECEIVED_EVENT](
        makeTransaction({
          transactionId: "newest",
          timestamp: new Date(base + MAX_TRANSACTIONS * 1000).toISOString(),
        }),
      );
    });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(FLUSH_INTERVAL_MS + 30);
    });

    expect(result.current.transactions).toHaveLength(MAX_TRANSACTIONS + 6);
    expect(result.current.transactions.map((t) => t.transactionId)).toContain("o4");
  });

  it("periodically re-fetches the recent list while connected as a self-healing safety net", async () => {
    const { connection } = createMockConnection();
    vi.mocked(createHubConnection).mockReturnValue(connection as any);
    vi.mocked(getTransactions).mockResolvedValue({ items: [], nextCursor: null });

    renderHook(() => useTransactionStream());
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(getTransactions).toHaveBeenCalledTimes(1);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(RESYNC_INTERVAL_MS);
    });
    expect(getTransactions).toHaveBeenCalledTimes(2);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(RESYNC_INTERVAL_MS);
    });
    expect(getTransactions).toHaveBeenCalledTimes(3);
  });
});
