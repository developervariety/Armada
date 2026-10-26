import type { ReactNode } from 'react';

export interface DataTableColumn<T> {
  key: string;
  label: string;
  required?: boolean;
  defaultHidden?: boolean;
  fixed?: boolean;
  render: (row: T) => ReactNode;
  cellClassName?: string;
  headerTitle?: string;
  className?: string;
}

export interface DataTableSelection<T> {
  isSelected: (row: T) => boolean;
  onToggle: (row: T) => void;
  allSelected: boolean;
  onToggleAll: (checked: boolean) => void;
  selectAllLabel: string;
  rowLabel: (row: T) => string;
}

export interface DataTableProps<T> {
  tableKey: string;
  columns: DataTableColumn<T>[];
  rows: T[];
  rowKey: (row: T) => string;
  onRowClick?: (row: T) => void;
  selection?: DataTableSelection<T>;
  emptyMessage?: ReactNode;
  placeholder?: ReactNode;
  pagination?: {
    pageNumber: number;
    pageSize: number;
    totalPages: number;
    totalRecords: number;
    onPageChange: (page: number) => void;
    onPageSizeChange: (size: number) => void;
  };
  autoRefresh?: { seconds: number; onChange: (seconds: number) => void };
  onRefresh?: () => Promise<void>;
  refreshTitle?: string;
  toolbarExtra?: ReactNode;
}
