import { useState } from "react";
import { createTransaction } from "../api/transactionsApi";
import { TransactionForm } from "../components/TransactionForm";
import type { CreateTransactionRequest, TransactionStatus } from "../types/transaction";
import { TRANSACTION_STATUSES } from "../types/transaction";

const CURRENCIES = ["USD", "EUR", "GBP", "ILS"];

function randomRequest(): CreateTransactionRequest {
  return {
    transactionId: crypto.randomUUID(),
    amount: Math.round((Math.random() * 10000 + 1) * 100) / 100,
    currency: CURRENCIES[Math.floor(Math.random() * CURRENCIES.length)],
    status: TRANSACTION_STATUSES[Math.floor(Math.random() * TRANSACTION_STATUSES.length)] as TransactionStatus,
  };
}

export function AddTransactionPage() {
  const [submitting, setSubmitting] = useState(false);
  const [log, setLog] = useState<string[]>([]);
  const [firing, setFiring] = useState(false);

  function appendLog(message: string) {
    setLog((prev) => [message, ...prev].slice(0, 20));
  }

  async function handleSubmit(request: CreateTransactionRequest) {
    setSubmitting(true);
    try {
      const created = await createTransaction(request);
      appendLog(`Sent ${created.transactionId.slice(0, 8)} - ${created.amount} ${created.currency} (${created.status})`);
    } catch (err) {
      appendLog(`Failed to send transaction: ${(err as Error).message}`);
    } finally {
      setSubmitting(false);
    }
  }

  async function handleGenerateOne() {
    await handleSubmit(randomRequest());
  }

  async function handleFire100() {
    setFiring(true);
    const started = performance.now();
    const results = await Promise.allSettled(
      Array.from({ length: 100 }, () => createTransaction(randomRequest())),
    );
    const succeeded = results.filter((r) => r.status === "fulfilled").length;
    const elapsed = Math.round(performance.now() - started);
    appendLog(`Fired 100 transactions: ${succeeded}/100 succeeded in ${elapsed}ms`);
    setFiring(false);
  }

  return (
    <div className="page">
      <h1>Transaction Simulator</h1>
      <p className="page-subtitle">
        Sends mock transactions to the backend ingestion API, simulating an external system feeding data
        into the engine. Open the Live Dashboard in another tab to watch them arrive in real time.
      </p>

      <TransactionForm onSubmit={handleSubmit} submitting={submitting} />

      <div className="simulator-actions">
        <button onClick={handleGenerateOne} disabled={submitting || firing}>
          Generate random transaction
        </button>
        <button onClick={handleFire100} disabled={submitting || firing} className="btn-secondary">
          {firing ? "Firing..." : "Fire 100 (load test)"}
        </button>
      </div>

      <div className="activity-log">
        <h2>Activity</h2>
        <ul>
          {log.map((entry, i) => (
            <li key={i}>{entry}</li>
          ))}
        </ul>
      </div>
    </div>
  );
}
