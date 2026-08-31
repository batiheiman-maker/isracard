import axios from "axios";
import type { CreateTransactionRequest, PagedResult, Transaction } from "../types/transaction";

const client = axios.create({ baseURL: "/api" });

// Keyset pagination: pass the previous call's nextCursor to get the next page of strictly
// older rows. Omit cursor for the first/most-recent page.
export async function getTransactions(cursor?: string, limit?: number): Promise<PagedResult<Transaction>> {
  const response = await client.get<PagedResult<Transaction>>("/transactions", {
    params: { cursor, limit },
  });
  return response.data;
}

// Catch-up path for SignalR reconnects: returns only what arrived after `sequence`, not the
// whole table - called from useTransactionStream's onreconnected handler so a client that was
// briefly disconnected doesn't silently miss transactions broadcast while it was down.
export async function getTransactionsSince(sequence: number): Promise<Transaction[]> {
  const response = await client.get<Transaction[]>(`/transactions/since/${sequence}`);
  return response.data;
}

export async function createTransaction(request: CreateTransactionRequest): Promise<Transaction> {
  const response = await client.post<Transaction>("/transactions", request);
  return response.data;
}
