import type { TransactionStatus } from "../types/transaction";

const STATUS_CLASS: Record<TransactionStatus, string> = {
  Completed: "status-badge status-completed",
  Failed: "status-badge status-failed",
  Pending: "status-badge status-pending",
};

export function StatusBadge({ status }: { status: TransactionStatus }) {
  return <span className={STATUS_CLASS[status]}>{status}</span>;
}
