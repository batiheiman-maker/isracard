import { useMemo, useState } from "react";
import { FilterBar, type StatusFilter } from "../components/FilterBar";
import { TransactionGrid } from "../components/TransactionGrid";
import { useTransactionStream } from "../hooks/useTransactionStream";

const STATUS_LABEL: Record<string, string> = {
  connecting: "Connecting...",
  connected: "Live",
  reconnecting: "Reconnecting...",
  disconnected: "Disconnected",
};

export function MonitorPage() {
  const { transactions, status, loadMore, isLoadingMore, hasMore } = useTransactionStream();
  const [filter, setFilter] = useState<StatusFilter>("All");

  const visible = useMemo(
    () => (filter === "All" ? transactions : transactions.filter((t) => t.status === filter)),
    [transactions, filter],
  );

  return (
    <div className="page">
      <div className="page-header">
        <h1>Live Dashboard</h1>
        <span className={`connection-status connection-${status}`}>{STATUS_LABEL[status]}</span>
      </div>
      <p className="page-subtitle">
        Connected to the backend's real-time layer. Transactions posted from the Simulator page appear
        here instantly.
      </p>

      <FilterBar value={filter} onChange={setFilter} totalCount={transactions.length} visibleCount={visible.length} />
      <TransactionGrid transactions={visible} />

      {hasMore && (
        <div className="load-more">
          <button className="btn-secondary" onClick={() => void loadMore()} disabled={isLoadingMore}>
            {isLoadingMore ? "Loading..." : "Load more"}
          </button>
        </div>
      )}
    </div>
  );
}
