export type TransactionStatus = "Pending" | "Completed" | "Failed";

export interface Transaction {
  transactionId: string;
  amount: number;
  currency: string;
  status: TransactionStatus;
  timestamp: string;
  sequence: number;
}

export interface CreateTransactionRequest {
  transactionId: string;
  amount: number;
  currency: string;
  status: TransactionStatus;
  timestamp?: string;
}

export const TRANSACTION_STATUSES: TransactionStatus[] = ["Pending", "Completed", "Failed"];

// Mirrors the backend's keyset-pagination envelope (PagedResult<T> in DTOs/PagedResult.cs).
// nextCursor is null exactly when the page came back short of the requested limit - no more
// older rows to fetch.
export interface PagedResult<T> {
  items: T[];
  nextCursor: string | null;
}
