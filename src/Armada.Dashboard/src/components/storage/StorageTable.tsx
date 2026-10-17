import { useMemo, useState } from 'react';
import DataTable, { type DataTableColumn } from '../shared/DataTable';
import { usePersistedPageSize } from '../../lib/usePersistedPageSize';
import { useLocale } from '../../context/LocaleContext';

/** Local paging and sorting apply to the complete bounded cached inventory, never to one server page. */
export default function StorageTable<T extends object>({ name, rows, columns }: { name: string; rows: T[]; columns: DataTableColumn<T>[] }) {
  const { t } = useLocale();
  const [filter, setFilter] = useState('');
  const [page, setPage] = useState(1);
  const [size, setSize] = usePersistedPageSize(`storage_${name}`, 10);
  const [sort, setSort] = useState({ field: name === 'files' ? 'bytes' : columns[0]?.key ?? '', dir: (name === 'files' ? 'desc' : 'asc') as 'asc' | 'desc' });
  const filtered = useMemo(() => {
    const search = filter.toLocaleLowerCase();
    const result = rows.filter(row => Object.values(row).some(value => String(value ?? '').toLocaleLowerCase().includes(search)));
    return result.sort((a, b) => {
      const av = (a as Record<string, unknown>)[sort.field];
      const bv = (b as Record<string, unknown>)[sort.field];
      const delta = typeof av === 'number' && typeof bv === 'number' ? av - bv : String(av ?? '').localeCompare(String(bv ?? ''));
      return sort.dir === 'asc' ? delta : -delta;
    });
  }, [rows, filter, sort]);
  const pages = Math.max(1, Math.ceil(filtered.length / size));
  const current = Math.min(page, pages);
  return <DataTable tableKey={`storage_${name}`} ariaLabel={t(`storage.${name}`)} rows={filtered.slice((current - 1) * size, current * size)}
    rowKey={row => String(filtered.indexOf(row))} columns={columns.map(c => ({ ...c, sortKey: c.key }))}
    sort={{ ...sort, onSort: field => setSort(s => ({ field, dir: s.field === field && s.dir === 'asc' ? 'desc' : 'asc' })) }}
    emptyMessage={t('storage.empty')} pagination={{ pageNumber: current, pageSize: size, totalPages: pages, totalRecords: filtered.length,
      onPageChange: setPage, onPageSizeChange: n => { setSize(n); setPage(1); } }}
    toolbarExtra={<input aria-label={t('storage.filter')} title={t('storage.filterHelp')} placeholder={t('storage.filter')} value={filter} onChange={e => { setFilter(e.target.value); setPage(1); }} />} />;
}
