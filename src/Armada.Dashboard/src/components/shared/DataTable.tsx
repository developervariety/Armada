import { useLocale } from '../../context/LocaleContext';
import { useColumnVisibility } from '../../lib/useColumnVisibility';
import AutoRefreshSelect from './AutoRefreshSelect';
import Pagination from './Pagination';
import RefreshButton from './RefreshButton';
import { ColumnChooser, TableBody, TableHeader } from './DataTableParts';
import type { DataTableProps } from './DataTableTypes';

export { type DataTableColumn } from './DataTableTypes';

export default function DataTable<T>({
  tableKey, columns, rows, rowKey, onRowClick, selection, emptyMessage, placeholder,
  pagination, autoRefresh, onRefresh, refreshTitle, toolbarExtra,
}: DataTableProps<T>) {
  const { t } = useLocale();
  const visibility = useColumnVisibility(tableKey, columns);
  const shown = columns.filter(column => column.fixed || visibility.isVisible(column.key));

  return <>
    <div className="table-toolbar" style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 12, flexWrap: 'wrap' }}>
      {pagination ? <Pagination {...pagination} /> : <span>{t('{{count}} records', { count: rows.length.toLocaleString() })}</span>}
      {autoRefresh && <AutoRefreshSelect seconds={autoRefresh.seconds} onChange={autoRefresh.onChange} />}
      {onRefresh && <RefreshButton onRefresh={onRefresh} title={refreshTitle ?? 'Refresh'} />}
      {toolbarExtra}
      <ColumnChooser columns={columns} isVisible={visibility.isVisible} onToggle={visibility.toggle} onReset={visibility.reset} />
    </div>
    {placeholder || <div className="table-wrap"><table>
      <TableHeader columns={shown} selection={selection} />
      <TableBody rows={rows} columns={shown} rowKey={rowKey} onRowClick={onRowClick}
        selection={selection} emptyMessage={emptyMessage} />
    </table></div>}
  </>;
}
