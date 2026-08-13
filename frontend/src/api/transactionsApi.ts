import axios from "axios";
import type { CreateTransactionRequest, Transaction } from "../types/transaction";

const client = axios.create({ baseURL: "/api" });

export async function getTransactions(): Promise<Transaction[]> {
  const response = await client.get<Transaction[]>("/transactions");
  return response.data;
}

export async function createTransaction(request: CreateTransactionRequest): Promise<Transaction> {
  const response = await client.post<Transaction>("/transactions", request);
  return response.data;
}
