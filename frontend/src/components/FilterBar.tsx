import type { TransactionStatus } from "../types/transaction";

export type StatusFilter = "All" | TransactionStatus;

const FILTERS: StatusFilter[] = ["All", "Pending", "Completed", "Failed"];

interface FilterBarProps {
  value: StatusFilter;
  onChange: (filter: StatusFilter) => void;
  totalCount: number;
  visibleCount: number;
}

export function FilterBar({ value, onChange, totalCount, visibleCount }: FilterBarProps) {
  return (
    <div className="filter-bar">
      <div className="filter-buttons">
        {FILTERS.map((filter) => (
          <button
            key={filter}
            className={filter === value ? "filter-btn filter-btn-active" : "filter-btn"}
            onClick={() => onChange(filter)}
          >
            {filter}
          </button>
        ))}
      </div>
      <span className="filter-count">
        Showing {visibleCount} of {totalCount}
      </span>
    </div>
  );
}
