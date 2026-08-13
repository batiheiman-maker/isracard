import { useState, type FormEvent } from "react";
import type { CreateTransactionRequest, TransactionStatus } from "../types/transaction";
import { TRANSACTION_STATUSES } from "../types/transaction";

interface TransactionFormProps {
  onSubmit: (request: CreateTransactionRequest) => Promise<void>;
  submitting: boolean;
}

export function TransactionForm({ onSubmit, submitting }: TransactionFormProps) {
  const [amount, setAmount] = useState("1500.50");
  const [currency, setCurrency] = useState("USD");
  const [status, setStatus] = useState<TransactionStatus>("Completed");

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    await onSubmit({ amount: Number(amount), currency, status });
  }

  return (
    <form className="transaction-form" onSubmit={handleSubmit}>
      <label>
        Amount
        <input
          type="number"
          step="0.01"
          min="0.01"
          value={amount}
          onChange={(e) => setAmount(e.target.value)}
          required
        />
      </label>
      <label>
        Currency
        <input
          type="text"
          value={currency}
          maxLength={3}
          onChange={(e) => setCurrency(e.target.value.toUpperCase())}
          required
        />
      </label>
      <label>
        Status
        <select value={status} onChange={(e) => setStatus(e.target.value as TransactionStatus)}>
          {TRANSACTION_STATUSES.map((s) => (
            <option key={s} value={s}>
              {s}
            </option>
          ))}
        </select>
      </label>
      <button type="submit" disabled={submitting}>
        {submitting ? "Sending..." : "Send Transaction"}
      </button>
    </form>
  );
}
