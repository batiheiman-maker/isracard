import { memo } from "react";
import { motion } from "framer-motion";
import type { Transaction } from "../types/transaction";
import { StatusBadge } from "./StatusBadge";

interface TransactionRowProps {
  transaction: Transaction;
}

function TransactionRowImpl({ transaction }: TransactionRowProps) {
  return (
    <motion.tr
      initial={{ opacity: 0, y: -8 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.18 }}
    >
      <td className="col-id" title={transaction.transactionId}>
        {transaction.transactionId.slice(0, 8)}
      </td>
      <td className="col-amount">
        {transaction.amount.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}
      </td>
      <td className="col-currency">{transaction.currency}</td>
      <td className="col-status">
        <StatusBadge status={transaction.status} />
      </td>
      <td className="col-timestamp">{new Date(transaction.timestamp).toLocaleTimeString()}</td>
    </motion.tr>
  );
}

export const TransactionRow = memo(TransactionRowImpl, (prev, next) =>
  prev.transaction.transactionId === next.transaction.transactionId &&
  prev.transaction.status === next.transaction.status);
