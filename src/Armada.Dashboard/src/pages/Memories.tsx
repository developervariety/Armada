import ActionMenu from '../components/shared/ActionMenu';
import DialogShell from '../components/shared/DialogShell';
import ScopeSelect from '../components/shared/ScopeSelect';
import { useAuth } from '../context/AuthContext';
import { resolveCreateScope } from '../lib/scoping';
import { useCallback, useEffect, useState } from 'react';
import { listMemories, deleteMemory, createMemory } from '../api/client';
import type { Memory, MemoryType } from '../types/models';
import { useLocale } from '../context/LocaleContext';
import { useNotifications } from '../context/NotificationContext';
import PageHeader from '../components/shared/PageHeader';
import DataTable, { type DataTableColumn } from '../components/shared/DataTable';
import ConfirmDialog from '../components/shared/ConfirmDialog';
import JsonViewer from '../components/shared/JsonViewer';
import ScopeBadge from '../components/shared/ScopeBadge';
import { MEMORY_TYPES } from '../lib/configuration';
import { reconcileServerPage, useListRequestGuard } from '../lib/serverPage';
import { usePersistedPageSize } from '../lib/usePersistedPageSize';

const TYPES = MEMORY_TYPES;

const MEMORY_TYPE_TIPS: Record<string, string> = {
  Episodic: 'Episodic: what happened and when, such as events, actions taken and key decisions.',
  Semantic: 'Semantic: a fact on its own, such as a preference or a rule about the code.',
  Procedural: 'Procedural: how to do something, such as a workflow or checklist.',
};

/**
 * Memories management surface. Lists the durable agent memories the Recorder distills from voyages
 * (episodic / semantic / procedural), with type and text filters, a JSON detail view, and delete for
 * pruning stale entries. Memories are primarily written by agents over MCP; operators curate here.
 */
export default function Memories() {
  const { t, formatRelativeTime, formatDateTime } = useLocale();
  const { pushToast } = useNotifications();
  const { isAdmin, isTenantAdmin, user } = useAuth();
  const viewer = { isAdmin, isTenantAdmin, tenantId: user?.user?.tenantId, userId: user?.user?.id };
  const [draft, setDraft] = useState<Partial<Memory> | null>(null);
  const [tagsText, setTagsText] = useState('');
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState('');

  function openDuplicate(memory: Memory) {
    setSaveError('');
    setTagsText(memory.tags.join(', '));
    // No key: POST upserts by key, so reusing the source key would replace the original.
    setDraft({ type: memory.type, topic: memory.topic, summary: memory.summary, content: memory.content,
      salience: memory.salience, vesselId: memory.vesselId || memory.sourceVesselId, tags: [...memory.tags], scope: resolveCreateScope(viewer), sourceKind: 'Manual' });
  }

  async function saveDuplicate(event: React.FormEvent) {
    event.preventDefault();
    if (!draft || saving) return;
    setSaving(true);
    try {
      await createMemory({ ...draft, tags: tagsText.split(',').map(tag => tag.trim()).filter(Boolean) });
      setDraft(null);
      pushToast('success', t('Memory duplicated.'));
      await load();
    } catch (error) { setSaveError(error instanceof Error ? error.message : t('Save failed.')); }
    finally { setSaving(false); }
  }

  const [memories, setMemories] = useState<Memory[]>([]);
  const [loading, setLoading] = useState(false);
  const [pageNumber, setPageNumber] = useState(1);
  const [pageSize, setPageSize] = usePersistedPageSize('memories', 25);
  const [totalPages, setTotalPages] = useState(1);
  const [totalRecords, setTotalRecords] = useState(0);
  const [typeFilter, setTypeFilter] = useState<'' | MemoryType>('');
  const [search, setSearch] = useState('');
  const [jsonData, setJsonData] = useState<{ open: boolean; title: string; data: unknown }>({ open: false, title: '', data: null });
  const [confirm, setConfirm] = useState<{ open: boolean; id: string }>({ open: false, id: '' });

  const beginListRequest = useListRequestGuard();

  const load = useCallback(async () => {
    const request = beginListRequest();
    setLoading(true);
    try {
      const filters: Record<string, string> = {};
      if (typeFilter) filters.type = typeFilter;
      if (search.trim()) filters.search = search.trim();
      const result = await listMemories({ pageNumber, pageSize, filters });
      if (!request.isCurrent()) return;
      const decision = reconcileServerPage(pageNumber, result.totalPages);
      if (!decision.accept) {
        setPageNumber(decision.pageNumber);
        return;
      }
      setMemories(result.objects ?? []);
      setTotalPages(decision.totalPages);
      setTotalRecords(result.totalRecords ?? 0);
    } catch {
      if (request.isCurrent()) pushToast('error', t('Failed to load memories.'));
    } finally {
      if (request.isCurrent()) setLoading(false);
    }
  }, [beginListRequest, pageNumber, pageSize, typeFilter, search, pushToast, t]);

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

  const memorySummary = (m: Memory) => m.summary || m.content;

  const columns: DataTableColumn<Memory>[] = [
    {
      key: 'type', label: t('Type'), headerTitle: t('Episodic memories record what happened, semantic ones record facts, procedural ones record how to do something.'), cellClassName: 'cell-nowrap',
      render: (m) => <span className={`tag tag-${m.type.toLowerCase()}`} title={t(MEMORY_TYPE_TIPS[m.type] ?? m.type)}>{t(m.type)}</span>,
    },
    { key: 'topic', label: t('Topic'), headerTitle: t('Group within the type that related memories share, for example code-style or deployment. A dash means none.'), cellClassName: 'text-dim', render: (m) => m.topic || '-' },
    {
      // One line; the full summary (or content) is in the tooltip.
      key: 'summary', label: t('Summary'), required: true, headerTitle: t('One-line summary of the memory (or its content when there is no summary). Hover for the full text; click a row for every field.'), cellClassName: 'truncate-cell', cellTitle: memorySummary,
      render: (m) => <span className="truncate-text">{memorySummary(m)}</span>,
    },
    { key: 'salience', label: t('Salience'), headerTitle: t('Importance from 0.00 to 1.00 (default 0.50). Higher memories are recalled first when agents search memory.'), cellClassName: 'text-dim', render: (m) => m.salience.toFixed(2) },
    {
      key: 'vessel', label: t('Vessel'), headerTitle: t('ID of the repository this memory is about, or that the work came from. A dash means it applies to no single vessel.'), cellClassName: 'mono text-dim',
      render: (m) => {
        const vesselId = m.vesselId || m.sourceVesselId;
        return vesselId ? <span className="cell-clip" title={vesselId}><span>{vesselId}</span></span> : '-';
      },
    },
    { key: 'visibility', label: t('Visibility'), headerTitle: t('Tenant-wide memories are recalled for everyone in the tenant; personal ones only for their owner.'), render: (m) => <ScopeBadge scope={m.scope} /> },
    {
      key: 'updated', label: t('Updated'), headerTitle: t('When this memory was last written or revised. Hover a value for the exact time.'), cellClassName: 'text-dim cell-nowrap', cellTitle: (m) => formatDateTime(m.lastUpdateUtc),
      render: (m) => formatRelativeTime(m.lastUpdateUtc),
    },
    {
      key: 'actions', label: t('Actions'), headerTitle: t('Copy the memory ID, or delete the memory.'), fixed: true, interactive: true, className: 'text-right',
      render: (m) => (
        <ActionMenu id={`memory-${m.id}`} items={[
          { label: 'Duplicate', title: 'Edit a new memory before saving. Its ID, key and agent history are not reused.', onClick: () => openDuplicate(m) },
          { label: 'View JSON', onClick: () => setJsonData({ open: true, title: `${t('Memory')}: ${m.id}`, data: m }) },
          { label: 'Delete', danger: true, onClick: () => setConfirm({ open: true, id: m.id }) },
        ]} />
      ),
    },
  ];

  return (
    <div>
      <PageHeader
        title={t('Memory')}
        subtitle={t('Durable memories distilled from voyages: episodic, semantic, and procedural.')}
      />

      <div className="filter-bar" style={{ display: 'flex', gap: '0.5rem', alignItems: 'center', margin: '0.5rem 0' }}>
        <select
          aria-label={t('Filter by type')}
          title={t('Show memories of every type, or only episodic, semantic or procedural ones.')}
          value={typeFilter}
          onChange={e => { setTypeFilter(e.target.value as '' | MemoryType); setPageNumber(1); }}
        >
          <option value="">{t('All types')}</option>
          {TYPES.map(ty => <option key={ty} value={ty}>{t(ty)}</option>)}
        </select>
        <input
          type="text"
          placeholder={t('Search content, topic, tags...')}
          title={t('Show only memories whose content, topic or tags contain this text.')}
          value={search}
          onChange={e => { setSearch(e.target.value); setPageNumber(1); }}
        />
      </div>

      {draft && <DialogShell open title={t('Duplicate Memory')} onClose={() => setDraft(null)} dismissible={!saving}>
        <form onSubmit={saveDuplicate}>
          {saveError && <p role="alert" className="text-danger">{saveError}</p>}
          <label>{t('Type')}<select value={draft.type} onChange={e => setDraft({ ...draft, type: e.target.value as MemoryType })}>{TYPES.map(type => <option key={type}>{type}</option>)}</select></label>
          <label>{t('Topic')}<input value={draft.topic ?? ''} onChange={e => setDraft({ ...draft, topic: e.target.value })} /></label>
          <label>{t('Summary')}<input value={draft.summary ?? ''} onChange={e => setDraft({ ...draft, summary: e.target.value })} /></label>
          <label>{t('Content')}<textarea required value={draft.content ?? ''} onChange={e => setDraft({ ...draft, content: e.target.value })} /></label>
          <label>{t('Salience')}<input type="number" min={0} max={1} step={0.01} required value={draft.salience} onChange={e => setDraft({ ...draft, salience: Number(e.target.value) })} /></label>
          <label>{t('Tags')}<input value={tagsText} onChange={e => setTagsText(e.target.value)} /></label>
          <ScopeSelect viewer={viewer} value={draft.scope ?? 'UserSpecific'} onChange={scope => setDraft({ ...draft, scope })} />
          <div className="modal-actions"><button type="submit" className="btn btn-primary" disabled={saving}>{t('Save Copy')}</button><button type="button" className="btn" disabled={saving} onClick={() => setDraft(null)}>{t('Cancel')}</button></div>
        </form>
      </DialogShell>}

      <JsonViewer open={jsonData.open} title={jsonData.title} data={jsonData.data} onClose={() => setJsonData({ open: false, title: '', data: null })} />

      <ConfirmDialog
        open={confirm.open}
        title={t('Delete Memory')}
        message={t('Delete this memory? This cannot be undone.')}
        onConfirm={() => void handleDelete()}
        onCancel={() => setConfirm({ open: false, id: '' })}
      />

      <DataTable
        tableKey="memories"
        columns={columns}
        rows={memories}
        rowKey={(m) => m.id}
        onRowClick={(m) => setJsonData({ open: true, title: `${t('Memory')}: ${m.id}`, data: m })}
        pagination={{
          pageNumber, pageSize, totalPages, totalRecords,
          onPageChange: setPageNumber,
          onPageSizeChange: (s) => { setPageSize(s); setPageNumber(1); },
        }}
        onRefresh={load}
        placeholder={memories.length > 0 ? undefined : loading
          ? <p className="text-dim">{t('Loading...')}</p>
          : <p className="text-dim">{t('No memories recorded yet.')}</p>}
      />
    </div>
  );
}
