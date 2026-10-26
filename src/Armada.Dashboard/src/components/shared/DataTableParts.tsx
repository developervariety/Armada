import type { ReactNode } from 'react';
import { useLocale } from '../../context/LocaleContext';
import type { DataTableColumn, DataTableSelection } from './DataTableTypes';

export function ColumnChooser<T>({ columns, isVisible, onToggle, onReset }: {
  columns: DataTableColumn<T>[];
  isVisible: (key: string) => boolean;
  onToggle: (key: string) => void;
  onReset: () => void;
}) {
  const { t } = useLocale();
  return (
    <details className="column-chooser" style={{ position: 'relative' }}>
      <summary className="btn btn-sm">{t('Columns')}</summary>
      <div className="column-chooser-menu" style={{
        position: 'absolute', zIndex: 10, right: 0, minWidth: 190, padding: 10,
        background: 'var(--bg-card)', border: '1px solid var(--border)',
      }}>
        {columns.filter(column => !column.fixed).map(column => (
          <label key={column.key} style={{ display: 'block', whiteSpace: 'nowrap' }}>
            <input type="checkbox" checked={isVisible(column.key)} disabled={column.required}
              onChange={() => onToggle(column.key)} />
            {' '}{column.label}{column.required ? ` (${t('Required')})` : ''}
          </label>
        ))}
        <button className="btn btn-sm" onClick={onReset}>{t('Reset')}</button>
      </div>
    </details>
  );
}

export function TableHeader<T>({ columns, selection }: {
  columns: DataTableColumn<T>[];
  selection?: DataTableSelection<T>;
}) {
  return (
    <thead><tr>
      {selection && <th className="col-checkbox"><input type="checkbox" checked={selection.allSelected}
        aria-label={selection.selectAllLabel} onChange={event => selection.onToggleAll(event.target.checked)} /></th>}
      {columns.map(column => <th key={column.key} title={column.headerTitle} className={column.className}>{column.label}</th>)}
    </tr></thead>
  );
}

function TableRow<T>({ row, columns, onRowClick, selection }: {
  row: T;
  columns: DataTableColumn<T>[];
  onRowClick?: (row: T) => void;
  selection?: DataTableSelection<T>;
}) {
  return (
    <tr className={onRowClick ? 'clickable' : undefined} onClick={() => onRowClick?.(row)}>
      {selection && <td className="col-checkbox" onClick={event => event.stopPropagation()}>
        <input type="checkbox" aria-label={selection.rowLabel(row)} checked={selection.isSelected(row)}
          onChange={() => selection.onToggle(row)} />
      </td>}
      {columns.map(column => <td key={column.key} className={column.cellClassName}>{column.render(row)}</td>)}
    </tr>
  );
}

export function TableBody<T>({ rows, columns, rowKey, onRowClick, selection, emptyMessage }: {
  rows: T[];
  columns: DataTableColumn<T>[];
  rowKey: (row: T) => string;
  onRowClick?: (row: T) => void;
  selection?: DataTableSelection<T>;
  emptyMessage?: ReactNode;
}) {
  if (rows.length === 0) {
    return <tbody><tr><td colSpan={columns.length + (selection ? 1 : 0)}>{emptyMessage}</td></tr></tbody>;
  }
  return <tbody>{rows.map(row => <TableRow key={rowKey(row)} {...{ row, columns, onRowClick, selection }} />)}</tbody>;
}
