import { useCallback, useEffect, useState } from 'react';
import { listMemories, deleteMemory } from '../api/client';
import type { Memory, MemoryType } from '../types/models';
import { useAuth } from '../context/AuthContext';
import { useLocale } from '../context/LocaleContext';
import { useNotifications } from '../context/NotificationContext';
import { canEditOwned, viewerFromAuth, OWNED_RECORD_WRITE_LEVEL } from '../lib/scoping';
import { useLatestRequest } from '../lib/useLatestRequest';
import PageHeader from '../components/shared/PageHeader';
import Pagination from '../components/shared/Pagination';
import ConfirmDialog from '../components/shared/ConfirmDialog';
import JsonViewer from '../components/shared/JsonViewer';
import ScopeBadge from '../components/shared/ScopeBadge';
import CopyButton from '../components/shared/CopyButton';
import RefreshButton from '../components/shared/RefreshButton';

const TYPES: MemoryType[] = ['Episodic', 'Semantic', 'Procedural'];

/**
 * Memories management surface. Lists the durable agent memories (episodic, semantic, procedural)
 * with type and text filters, a JSON detail view, and delete for pruning stale entries. Memories are
 * primarily written by agents over MCP; operators curate here. Delete is offered only where the
 * ownership rule lets the viewer change the memory.
 */
export default function Memories() {
  const { t, formatRelativeTime } = useLocale();
  const { pushToast } = useNotifications();
  const viewer = viewerFromAuth(useAuth());

  const [memories, setMemories] = useState<Memory[]>([]);
  const [loading, setLoading] = useState(false);
  const [pageNumber, setPageNumber] = useState(1);
  const [pageSize, setPageSize] = useState(25);
  const [totalPages, setTotalPages] = useState(1);
  const [totalRecords, setTotalRecords] = useState(0);
  const [typeFilter, setTypeFilter] = useState<'' | MemoryType>('');
  const [search, setSearch] = useState('');
  const [jsonData, setJsonData] = useState<{ open: boolean; title: string; data: unknown }>({ open: false, title: '', data: null });
  const [confirm, setConfirm] = useState<{ open: boolean; id: string }>({ open: false, id: '' });

  // Each keystroke, filter or page change starts a load; only the newest one writes the list.
  const requests = useLatestRequest();
  const load = useCallback(async () => {
    const request = requests.begin();
    setLoading(true);
    try {
      const filters: Record<string, string> = {};
      if (typeFilter) filters.type = typeFilter;
      if (search.trim()) filters.search = search.trim();
      const result = await listMemories({ pageNumber, pageSize, filters });
      if (!request.isCurrent()) return;
      setMemories(result.objects ?? []);
      setTotalPages(result.totalPages ?? 1);
      setTotalRecords(result.totalRecords ?? 0);
    } catch {
      if (request.isCurrent()) pushToast('error', t('Failed to load memories.'));
    } finally {
      if (request.isCurrent()) setLoading(false);
    }
  }, [requests, pageNumber, pageSize, typeFilter, search, pushToast, t]);

  useEffect(() => { void load(); }, [load]);

  const handleDelete = async () => {
    const id = confirm.id;
    setConfirm({ open: false, id: '' });
    try {
      await deleteMemory(id);
      pushToast('success', t('Memory deleted.'));
      void load();
    } catch {
      pushToast('error', t('Failed to delete memory.'));
    }
  };

  return (
    <div>
      <PageHeader
        title={t('Memory')}
        subtitle={t('Durable memories distilled from voyages: episodic, semantic, and procedural.')}
        actions={<RefreshButton onRefresh={load} />}
      />

      <div className="filter-bar" style={{ display: 'flex', gap: '0.5rem', alignItems: 'center', margin: '0.5rem 0' }}>
        <select
          aria-label={t('Filter by type')}
          value={typeFilter}
          onChange={e => { setTypeFilter(e.target.value as '' | MemoryType); setPageNumber(1); }}
        >
          <option value="">{t('All types')}</option>
          {TYPES.map(ty => <option key={ty} value={ty}>{t(ty)}</option>)}
        </select>
        <input
          type="text"
          placeholder={t('Search content, topic, tags...')}
          value={search}
          onChange={e => { setSearch(e.target.value); setPageNumber(1); }}
        />
      </div>

      <JsonViewer open={jsonData.open} title={jsonData.title} data={jsonData.data} onClose={() => setJsonData({ open: false, title: '', data: null })} />

      <ConfirmDialog
        open={confirm.open}
        title={t('Delete Memory')}
        message={t('Delete this memory? This cannot be undone.')}
        onConfirm={() => void handleDelete()}
        onCancel={() => setConfirm({ open: false, id: '' })}
      />

      {loading && memories.length === 0 && <p className="text-dim">{t('Loading...')}</p>}
      {!loading && memories.length === 0 && <p className="text-dim">{t('No memories recorded yet.')}</p>}

      {memories.length > 0 && (
        <>
          <Pagination
            pageNumber={pageNumber}
            pageSize={pageSize}
            totalPages={totalPages}
            totalRecords={totalRecords}
            onPageChange={setPageNumber}
            onPageSizeChange={s => { setPageSize(s); setPageNumber(1); }}
          />

          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>{t('Type')}</th>
                  <th>{t('Topic')}</th>
                  <th>{t('Summary')}</th>
                  <th>{t('Salience')}</th>
                  <th>{t('Vessel')}</th>
                  <th>{t('Visibility')}</th>
                  <th>{t('Updated')}</th>
                  <th className="text-right">{t('Actions')}</th>
                </tr>
              </thead>
              <tbody>
                {memories.map(m => {
                  const canDelete = canEditOwned(viewer, { ownershipScope: m.scope, tenantId: m.tenantId, userId: m.userId }, OWNED_RECORD_WRITE_LEVEL.memories);
                  return (
                    <tr key={m.id} className="clickable" onClick={() => setJsonData({ open: true, title: `Memory: ${m.id}`, data: m })}>
                      <td><span className={`tag tag-${m.type.toLowerCase()}`}>{t(m.type)}</span></td>
                      <td className="text-dim">{m.topic || '-'}</td>
                      <td>{m.summary || (m.content.length > 80 ? `${m.content.slice(0, 80)}...` : m.content)}</td>
                      <td className="text-dim">{m.salience.toFixed(2)}</td>
                      <td className="mono text-dim">{m.vesselId || m.sourceVesselId || '-'}</td>
                      <td><ScopeBadge scope={m.scope} /></td>
                      <td className="text-dim">{formatRelativeTime(m.lastUpdateUtc)}</td>
                      <td className="text-right" onClick={e => e.stopPropagation()}>
                        <span className="id-display">
                          <CopyButton text={m.id} />
                          {canDelete && <button className="btn-danger btn-sm" onClick={() => setConfirm({ open: true, id: m.id })}>{t('Delete')}</button>}
                        </span>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        </>
      )}
    </div>
  );
}
