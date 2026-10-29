import { useEffect, useState, useMemo, useCallback } from 'react';
import { useNavigate } from 'react-router-dom';
import { listVoyages, cancelVoyage, purgeVoyage, getVoyageMissionSummary } from '../api/client';
import type { Voyage } from '../types/models';
import DataTable, { type DataTableColumn } from '../components/shared/DataTable';
import ActionMenu from '../components/shared/ActionMenu';
import StatusBadge from '../components/shared/StatusBadge';
import ConfirmDialog from '../components/shared/ConfirmDialog';
import DeleteProgressDialog from '../components/shared/DeleteProgressDialog';
import { useDeleteProgress } from '../lib/useDeleteProgress';
import JsonViewer from '../components/shared/JsonViewer';
import RecordDetailModal from '../components/shared/RecordDetailModal';
import CopyButton from '../components/shared/CopyButton';
import { useAutoRefresh } from '../lib/useAutoRefresh';
import { useLoadError } from '../lib/useLoadError';
import { useLatestRequest } from '../lib/useLatestRequest';
import { useServerPaging } from '../lib/useServerPaging';
import PageHeader from '../components/shared/PageHeader';
import ErrorModal from '../components/shared/ErrorModal';
import { useLocale } from '../context/LocaleContext';
import { useNotifications } from '../context/NotificationContext';
import { useResourceTable } from '../lib/useResourceTable';

/** Voyage statuses the server filters on. */
const VOYAGE_STATUSES = ['Open', 'InProgress', 'Complete', 'Failed', 'Cancelled'] as const;

export default function Voyages() {
  const navigate = useNavigate();
  const { t } = useLocale();
  const { pushToast } = useNotifications();
  const [voyages, setVoyages] = useState<Voyage[]>([]);
  const [loading, setLoading] = useState(true);
  const { error, setError, loadFailed, loadSucceeded } = useLoadError();

  // Pagination and the status filter run on the server, so pages and totals cover every voyage.
  const { pageNumber, pageSize, totalPages, totalRecords, setPageNumber, setPageSize, acceptPage } = useServerPaging();
  const [statusFilter, setStatusFilter] = useState('');

  // JSON viewer
  const [jsonData, setJsonData] = useState<{ open: boolean; title: string; data: unknown }>({ open: false, title: '', data: null });

  // Row-click view modal
  const [viewRecord, setViewRecord] = useState<Record<string, unknown> | null>(null);

  // Confirm dialog
  const [confirm, setConfirm] = useState<{ open: boolean; title: string; message: string; onConfirm: () => void }>({ open: false, title: '', message: '', onConfirm: () => {} });
  const deleteProgress = useDeleteProgress();

  // Selection only: rows keep the server's order. Sorting or filtering one server page here would
  // present a partial set as the whole list.
  const table = useResourceTable({
    rows: voyages,
    getId: (v) => v.id,
    initialPageSize: 1000,
  });

  const requests = useLatestRequest();
  const load = useCallback(async () => {
    const request = requests.begin();
    try {
      setLoading(true);
      const filters: Record<string, string> = {};
      if (statusFilter) filters.status = statusFilter;
      const result = await listVoyages({ pageNumber, pageSize, filters });
      if (!request.isCurrent()) return;
      if (!acceptPage(result)) return;
      setVoyages(result.objects || []);
      loadSucceeded();
    } catch {
      if (request.isCurrent()) loadFailed(t('Failed to load voyages.'));
    } finally {
      if (request.isCurrent()) setLoading(false);
    }
  }, [acceptPage, requests, pageNumber, pageSize, statusFilter, t]);

  useEffect(() => { load(); }, [load]);

  const { seconds: refreshSeconds, setSeconds: setRefreshSeconds } = useAutoRefresh('voyages', load);

  const columns: DataTableColumn<Voyage>[] = [
    { key: 'title', label: t('Title'), required: true, render: v => <strong className="truncate-text" title={v.title}>{v.title}</strong>, cellClassName: 'truncate-cell' },
    { key: 'id', label: t('ID'), required: true, render: v => <span className="id-display"><span className="id-value" title={v.id}>{v.id}</span><CopyButton text={v.id} onClick={e => e.stopPropagation()} /></span>, cellClassName: 'mono text-dim table-id-cell' },
    { key: 'status', label: t('Status'), render: v => <StatusBadge status={v.status} /> },
    { key: 'autoPush', label: t('Auto Push'), defaultHidden: true, render: v => v.autoPush != null ? (v.autoPush ? t('Yes') : t('No')) : '-' },
    { key: 'autoCreatePullRequests', label: t('Auto Create PRs'), defaultHidden: true, render: v => v.autoCreatePullRequests != null ? (v.autoCreatePullRequests ? t('Yes') : t('No')) : '-' },
    { key: 'landingMode', label: t('Landing Mode'), render: v => v.landingMode || '-' },
    { key: 'actions', label: t('Actions'), fixed: true, className: 'text-right', render: v => <span onClick={e => e.stopPropagation()}><ActionMenu id={`voyage-${v.id}`} items={[
      { label: 'View Detail', onClick: () => navigate(`/voyages/${v.id}`) },
      { label: 'View Status', onClick: () => handleViewStatus(v.id) },
      { label: 'View JSON', onClick: () => setJsonData({ open: true, title: t('Voyage: {{title}}', { title: v.title }), data: v }) },
      { label: 'Cancel', danger: true, onClick: () => handleCancel(v.id, v.title) },
      { label: 'Purge', danger: true, onClick: () => handlePurge(v.id, v.title) },
    ]} /></span> },
  ];

  // Actions
  function handleCancel(id: string, title: string) {
    setConfirm({
      open: true,
      title: t('Cancel Voyage'),
      message: t('Cancel voyage "{{title}}"? All pending missions will be cancelled.', { title }),
      onConfirm: async () => {
        setConfirm(c => ({ ...c, open: false }));
        try {
          await cancelVoyage(id);
          pushToast('warning', t('Voyage "{{title}}" cancelled.', { title }));
          load();
        } catch { setError(t('Cancel failed.')); }
      },
    });
  }

  function handlePurge(id: string, title: string) {
    setConfirm({
      open: true,
      title: t('Purge Voyage'),
      message: t('Purge voyage "{{title}}"? This will permanently remove the voyage and all associated missions. This cannot be undone.', { title }),
      onConfirm: async () => {
        setConfirm(c => ({ ...c, open: false }));
        await deleteProgress.run(t('Purging voyage'), async () => {
          await purgeVoyage(id);
          pushToast('warning', t('Voyage "{{title}}" purged.', { title }));
          load();
        });
      },
    });
  }

  function handleBulkCancel() {
    setConfirm({
      open: true,
      title: t('Cancel Selected Voyages'),
      message: t('Cancel {{count}} selected voyage(s)?', { count: table.selected.length }),
      onConfirm: async () => {
        setConfirm(c => ({ ...c, open: false }));
        const ids = [...table.selected];
        table.setSelected([]);
        let failed = 0;
        for (const id of ids) {
          try { await cancelVoyage(id); } catch { failed++; }
        }
        const success = ids.length - failed;
        if (success > 0) {
          pushToast(failed > 0 ? 'warning' : 'success', failed > 0
            ? t('Cancelled {{success}} voyages. {{failed}} failed.', { success, failed })
            : t('Cancelled {{success}} voyages.', { success }));
        }
        if (failed > 0) setError(t('Cancelled {{success}} voyages, {{failed}} failed.', { success: ids.length - failed, failed }));
        load();
      },
    });
  }

  async function handleViewStatus(id: string) {
    try {
      const status = await getVoyageMissionSummary(id);
      setJsonData({ open: true, title: t('Voyage Status'), data: status });
    } catch { setError(t('Failed to load voyage status.')); }
  }

  return (
    <div>
      <PageHeader
        title={t('Voyages')}
        subtitle={t('Batches of related missions dispatched together')}
        actions={(
          <>
            {table.selected.length > 0 && (
              <button className="btn btn-sm btn-danger" onClick={handleBulkCancel}>
                {t('Cancel Selected')} ({table.selected.length})
              </button>
            )}
            <button className="btn btn-primary btn-sm" onClick={() => navigate('/voyages/create')}>+ {t('Voyage')}</button>
          </>
        )}
      />

      <ErrorModal error={error} onClose={() => setError('')} />

      <JsonViewer open={jsonData.open} title={jsonData.title} data={jsonData.data} onClose={() => setJsonData({ open: false, title: '', data: null })} />
      <RecordDetailModal
        open={!!viewRecord}
        title={typeof viewRecord?.title === 'string' ? viewRecord.title : t('Voyage')}
        subtitle={t('Voyage')}
        record={viewRecord}
        onClose={() => setViewRecord(null)}
        onEdit={() => { const r = viewRecord; setViewRecord(null); navigate(`/voyages/${(r as { id: string }).id}`); }}
        editLabel={t('Open Details')}
      />
      <DeleteProgressDialog state={deleteProgress.state} onRetry={deleteProgress.retry} onDismiss={deleteProgress.dismiss} />
      <ConfirmDialog open={confirm.open} title={confirm.title} message={confirm.message}
        onConfirm={confirm.onConfirm} onCancel={() => setConfirm(c => ({ ...c, open: false }))} />

      <div style={{ display: 'flex', gap: 8, marginBottom: 12, alignItems: 'center' }}>
        <select className="filter-select" title={t('Filter by status')} value={statusFilter}
          onChange={e => { setStatusFilter(e.target.value); setPageNumber(1); }}>
          <option value="">{t('All Statuses')}</option>
          {VOYAGE_STATUSES.map(status => <option key={status} value={status}>{t(status)}</option>)}
        </select>
      </div>

      <DataTable tableKey="voyages" columns={columns} rows={voyages} rowKey={v => v.id}
        onRowClick={v => setViewRecord(v as unknown as Record<string, unknown>)}
        pagination={{ pageNumber, pageSize, totalPages, totalRecords, onPageChange: setPageNumber,
          onPageSizeChange: size => { setPageSize(size); setPageNumber(1); } }}
        autoRefresh={{ seconds: refreshSeconds, onChange: setRefreshSeconds }} onRefresh={load} refreshTitle="Refresh voyage data"
        selection={{ isSelected: v => table.selected.includes(v.id), onToggle: v => table.toggleSelect(v.id),
          allSelected: table.allSelected, onToggleAll: checked => checked ? table.selectAll() : table.clearSelection(),
          selectAllLabel: t('Select all voyages'), rowLabel: () => t('Select this voyage') }}
        emptyMessage={t('No voyages match the current filters.')}
        placeholder={voyages.length === 0 ? <p className="text-dim">{loading ? t('Loading...') : statusFilter ? t('No voyages match the current filters.') : t('No voyages found.')}</p> : undefined} />
    </div>
  );
}
