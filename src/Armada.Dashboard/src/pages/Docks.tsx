import { useEffect, useState, useMemo, useCallback } from 'react';
import { useNavigate } from 'react-router-dom';
import { listDocks, deleteDock, listCaptains, listVessels } from '../api/client';
import type { Dock, Captain, Vessel } from '../types/models';
import Pagination from '../components/shared/Pagination';
import ActionMenu from '../components/shared/ActionMenu';
import ConfirmDialog from '../components/shared/ConfirmDialog';
import JsonViewer from '../components/shared/JsonViewer';
import RecordDetailModal from '../components/shared/RecordDetailModal';
import CopyButton from '../components/shared/CopyButton';
import RefreshButton from '../components/shared/RefreshButton';
import AutoRefreshSelect from '../components/shared/AutoRefreshSelect';
import { useAutoRefresh } from '../lib/useAutoRefresh';
import { retainSelection } from '../lib/selection';
import PageHeader from '../components/shared/PageHeader';
import ErrorModal from '../components/shared/ErrorModal';
import { useLocale } from '../context/LocaleContext';
import { useNotifications } from '../context/NotificationContext';
import { useResourceTable } from '../lib/useResourceTable';

export default function Docks() {
  const navigate = useNavigate();
  const { t, formatRelativeTime, formatDateTime } = useLocale();
  const { pushToast } = useNotifications();
  const [docks, setDocks] = useState<Dock[]>([]);
  const [captains, setCaptains] = useState<Captain[]>([]);
  const [vessels, setVessels] = useState<Vessel[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  // JSON viewer
  const [jsonData, setJsonData] = useState<{ open: boolean; title: string; data: unknown }>({ open: false, title: '', data: null });

  // View detail modal
  const [viewRecord, setViewRecord] = useState<Record<string, unknown> | null>(null);

  // Confirm dialog
  const [confirm, setConfirm] = useState<{ open: boolean; title: string; message: string; onConfirm: () => void }>({ open: false, title: '', message: '', onConfirm: () => {} });

  const table = useResourceTable({
    rows: docks,
    getId: (d) => d.id,
    columnValues: {
      branchName: (d) => (d.branchName ?? '').toLowerCase(),
      worktreePath: (d) => (d.worktreePath ?? '').toLowerCase(),
      active: (d) => (d.active ? 1 : 0),
      createdUtc: (d) => d.createdUtc,
    },
    initialSortField: 'createdUtc',
    initialSortDir: 'desc',
    initialPageSize: 25,
  });

  const captainName = useCallback((id: string | null) => {
    if (!id) return '-';
    const c = captains.find(c => c.id === id);
    return c?.name || id.substring(0, 8);
  }, [captains]);

  const vesselName = useCallback((id: string | null) => {
    if (!id) return '-';
    const v = vessels.find(v => v.id === id);
    return v?.name || id.substring(0, 8);
  }, [vessels]);

  // Sorting, column filters and pagination run in the table over the whole set, so the page loads
  // every dock (the server caps a page at 1000) instead of one server page.
  const load = useCallback(async () => {
    try {
      setLoading(true);
      const result = await listDocks({ pageSize: 1000 });
      setDocks(result.objects || []);
      table.setSelected(prev => retainSelection(prev, (result.objects || []).map(d => d.id)));
      setError('');
    } catch {
      setError(t('Failed to load docks.'));
    } finally {
      setLoading(false);
    }
    // Captain and vessel names are refreshed with the docks so new records resolve by name.
    listCaptains({ pageSize: 1000 }).then(r => setCaptains(r.objects || [])).catch(() => {});
    listVessels({ pageSize: 1000 }).then(r => setVessels(r.objects || [])).catch(() => {});
  }, [t]);

  useEffect(() => { load(); }, [load]);
  const { seconds: refreshSeconds, setSeconds: setRefreshSeconds } = useAutoRefresh('docks', load);

  // Delete
  function handleDelete(id: string) {
    setConfirm({
      open: true,
      title: t('Delete Dock'),
      message: t('Delete dock {{id}}? This will clean up the git worktree and cannot be undone.', { id }),
      onConfirm: async () => {
        setConfirm(c => ({ ...c, open: false }));
        try {
          await deleteDock(id);
          pushToast('warning', t('Dock {{id}} deleted.', { id }));
          load();
        } catch { setError(t('Delete failed.')); }
      },
    });
  }

  function handleBulkDelete() {
    setConfirm({
      open: true,
      title: t('Delete Selected Docks'),
      message: t('Delete {{count}} selected dock(s)? This will clean up the git worktrees and cannot be undone.', { count: table.selected.length }),
      onConfirm: async () => {
        setConfirm(c => ({ ...c, open: false }));
        const ids = [...table.selected];
        table.setSelected([]);
        let failed = 0;
        for (const id of ids) {
          try { await deleteDock(id); } catch { failed++; }
        }
        const deleted = ids.length - failed;
        if (deleted > 0) {
          pushToast(failed > 0 ? 'warning' : 'success', failed > 0
            ? t('Deleted {{deleted}} docks. {{failed}} failed.', { deleted, failed })
            : t('Deleted {{deleted}} docks.', { deleted }));
        }
        if (failed > 0) setError(t('Deleted {{deleted}} docks, {{failed}} failed.', { deleted: ids.length - failed, failed }));
        load();
      },
    });
  }

  return (
    <div>
      <PageHeader
        title={t('Docks')}
        subtitle={t('Git worktrees provisioned for captains. Docks are system-managed and track branch activity.')}
        actions={(
          <>
            {table.selected.length > 0 && (
              <button className="btn btn-sm btn-danger" onClick={handleBulkDelete}>
                {t('Delete Selected')} ({table.selected.length})
              </button>
            )}
            <AutoRefreshSelect seconds={refreshSeconds} onChange={setRefreshSeconds} />
            <RefreshButton onRefresh={load} title={t('Refresh dock data')} />
          </>
        )}
      />

      <ErrorModal error={error} onClose={() => setError('')} />

      <JsonViewer open={jsonData.open} title={jsonData.title} data={jsonData.data} onClose={() => setJsonData({ open: false, title: '', data: null })} />
      <RecordDetailModal
        open={!!viewRecord}
        title={viewRecord ? `${t('Dock')}: ${String(viewRecord.branchName || viewRecord.id || '')}` : ''}
        subtitle={viewRecord ? String(viewRecord.worktreePath || '') : undefined}
        record={viewRecord}
        onClose={() => setViewRecord(null)}
      />
      <ConfirmDialog open={confirm.open} title={confirm.title} message={confirm.message}
        onConfirm={confirm.onConfirm} onCancel={() => setConfirm(c => ({ ...c, open: false }))} />

      {loading && docks.length === 0 && <p className="text-dim">{t('Loading...')}</p>}
      {!loading && docks.length === 0 && <p className="text-dim">{t('No docks found.')}</p>}

      {docks.length > 0 && (
        <>
          <Pagination pageNumber={table.currentPage} pageSize={table.pageSize} totalPages={table.totalPages}
            totalRecords={table.sorted.length}
            onPageChange={p => table.setPageNumber(p)} onPageSizeChange={s => { table.setPageSize(s); table.setPageNumber(1); }} />

          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th className="col-checkbox">
                    <input type="checkbox" checked={table.allSelected} onChange={e => e.target.checked ? table.selectAll() : table.clearSelection()} title={t('Select all docks')} />
                  </th>
                  <th>{t('ID')}</th>
                  <th>{t('Vessel')}</th>
                  <th>{t('Captain')}</th>
                  <th className="sortable" onClick={() => table.handleSort('branchName')} title={t('Branch name -- click to sort')}>
                    {t('Branch')}{table.sortIcon('branchName')}
                  </th>
                  <th>{t('Worktree Path')}</th>
                  <th className="sortable" onClick={() => table.handleSort('active')} title={t('Active status -- click to sort')}>
                    {t('Active')}{table.sortIcon('active')}
                  </th>
                  <th className="sortable" onClick={() => table.handleSort('createdUtc')} title={t('Created -- click to sort')}>
                    {t('Created')}{table.sortIcon('createdUtc')}
                  </th>
                  <th className="text-right">{t('Actions')}</th>
                </tr>
                <tr className="column-filter-row">
                  <td></td>
                  <td></td>
                  <td></td>
                  <td></td>
                  <td><input type="text" className="col-filter" value={table.colFilters.branchName ?? ''} onChange={e => table.setColFilter('branchName', e.target.value)} placeholder={t('Filter...')} /></td>
                  <td><input type="text" className="col-filter" value={table.colFilters.worktreePath ?? ''} onChange={e => table.setColFilter('worktreePath', e.target.value)} placeholder={t('Filter...')} /></td>
                  <td></td>
                  <td></td>
                  <td></td>
                </tr>
              </thead>
              <tbody>
                {table.paginated.map(d => (
                  <tr key={d.id} className="clickable" onClick={() => setViewRecord(d as unknown as Record<string, unknown>)}>
                    <td className="col-checkbox" onClick={e => e.stopPropagation()}>
                      <input type="checkbox" checked={table.selected.includes(d.id)} onChange={() => table.toggleSelect(d.id)} title={t('Select this dock')} />
                    </td>
                    <td className="mono text-dim table-id-cell">
                      <span className="id-display">
                        <span className="id-value" title={d.id}>{d.id}</span>
                        <CopyButton text={d.id} onClick={e => e.stopPropagation()} />
                      </span>
                    </td>
                    <td onClick={e => e.stopPropagation()}>
                      {d.vesselId ? (
                        <a href="#" onClick={e => { e.preventDefault(); navigate(`/vessels/${d.vesselId}`); }}>{vesselName(d.vesselId)}</a>
                      ) : '-'}
                    </td>
                    <td onClick={e => e.stopPropagation()}>
                      {d.captainId ? (
                        <a href="#" onClick={e => { e.preventDefault(); navigate(`/captains/${d.captainId}`); }}>{captainName(d.captainId)}</a>
                      ) : '-'}
                    </td>
                    <td className="mono text-dim table-url-cell">
                      {d.branchName ? (
                        <span className="id-display">
                          <span className="url-value" title={d.branchName}>{d.branchName}</span>
                          <CopyButton text={d.branchName} onClick={e => e.stopPropagation()} title={t('Copy branch')} />
                        </span>
                      ) : '-'}
                    </td>
                    <td className="mono text-dim" style={{ maxWidth: 200, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }} title={d.worktreePath || ''}>
                      {d.worktreePath || '-'}
                    </td>
                    <td>{d.active ? t('Yes') : t('No')}</td>
                    <td className="text-dim" title={formatDateTime(d.createdUtc)}>{formatRelativeTime(d.createdUtc)}</td>
                    <td className="text-right" onClick={e => e.stopPropagation()}>
                      <ActionMenu id={`dock-${d.id}`} items={[
                        { label: 'View Detail', onClick: () => navigate(`/docks/${d.id}`) },
                        { label: 'View JSON', onClick: () => setJsonData({ open: true, title: `${t('Dock')}: ${d.id}`, data: d }) },
                        { label: 'Delete', danger: true, onClick: () => handleDelete(d.id) },
                      ]} />
                    </td>
                  </tr>
                ))}
                {table.sorted.length === 0 && (
                  <tr><td colSpan={9} className="text-dim">{t('No docks match the current filters.')}</td></tr>
                )}
              </tbody>
            </table>
          </div>
        </>
      )}
    </div>
  );
}
