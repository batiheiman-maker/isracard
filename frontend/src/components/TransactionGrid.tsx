import type { Transaction } from "../types/transaction";
import { TransactionRow } from "./TransactionRow";

interface TransactionGridProps {
  transactions: Transaction[];
}

// AnimatePresence's exit-tracking silently fails to remove rows from the DOM at this list size
// (100+ rows), which broke client-side filtering. Rows still get an entrance animation (see
// TransactionRow) since that doesn't require AnimatePresence's exit bookkeeping.
export function TransactionGrid({ transactions }: TransactionGridProps) {
  if (transactions.length === 0) {
    return <p className="empty-state">No transactions yet. Generate one from the Simulator page.</p>;
  }

  return (
    <table className="transaction-grid">
      <thead>
        <tr>
          <th>Transaction ID</th>
          <th>Amount</th>
          <th>Currency</th>
          <th>Status</th>
          <th>Timestamp</th>
        </tr>
      </thead>
      <tbody>
        {transactions.map((t) => (
          <TransactionRow key={t.transactionId} transaction={t} />
        ))}
      </tbody>
    </table>
  );
}
