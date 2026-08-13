export type TransactionStatus = "Pending" | "Completed" | "Failed";

export interface Transaction {
  transactionId: string;
  amount: number;
  currency: string;
  status: TransactionStatus;
  timestamp: string;
}

export interface CreateTransactionRequest {
  transactionId: string;
  amount: number;
  currency: string;
  status: TransactionStatus;
  timestamp?: string;
}

export const TRANSACTION_STATUSES: TransactionStatus[] = ["Pending", "Completed", "Failed"];
