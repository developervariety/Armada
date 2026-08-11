import { useEffect, useState, useMemo, useCallback } from 'react';
import { useNavigate } from 'react-router-dom';
import { listCaptains, createCaptain, updateCaptain, deleteCaptain, stopCaptain, recallCaptain, stopAllCaptains, restartCaptain, getCaptainTools, getCaptainHealth } from '../api/client';
import type { Captain, CaptainToolAccessResult, CaptainHealthResponse } from '../types/models';
import CaptainHealthHistogram from '../components/captains/CaptainHealthHistogram';
import Pagination from '../components/shared/Pagination';
import ActionMenu from '../components/shared/ActionMenu';
import StatusBadge from '../components/shared/StatusBadge';
import ConfirmDialog from '../components/shared/ConfirmDialog';
import MuxRuntimeFields from '../components/captains/MuxRuntimeFields';
import CaptainToolViewer from '../components/captains/CaptainToolViewer';
import JsonViewer from '../components/shared/JsonViewer';
import CopyButton from '../components/shared/CopyButton';
import RefreshButton from '../components/shared/RefreshButton';
import PageHeader from '../components/shared/PageHeader';
import ErrorModal from '../components/shared/ErrorModal';
import { useLocale } from '../context/LocaleContext';
import { useNotifications } from '../context/NotificationContext';
import { canCaptainStartPlanning } from '../lib/captains';
import { buildMuxRuntimeOptionsJson, EMPTY_MUX_CAPTAIN_FORM, isMuxRuntime, muxFormFromCaptain, type MuxCaptainFormFields } from '../lib/mux';
import { buildCaptainDuplicatePayload } from '../lib/duplicates';

type SortDir = 'asc' | 'desc';
type SortField = 'name' | 'runtime' | 'state' | 'createdUtc';
type CaptainFormState = {
  name: string;
  runtime: string;
  systemInstructions: string;
  model: string;
  apiEndpointUrl: string;
} & MuxCaptainFormFields;

export default function Captains() {
  const navigate = useNavigate();
  const { t, formatRelativeTime, formatDateTime } = useLocale();
  const { pushToast } = useNotifications();
  const [captains, setCaptains] = useState<Captain[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  // Modal state
  const [showForm, setShowForm] = useState(false);
  const [editing, setEditing] = useState<Captain | null>(null);
  const [form, setForm] = useState<CaptainFormState>({ name: '', runtime: '', systemInstructions: '', model: '', apiEndpointUrl: '', ...EMPTY_MUX_CAPTAIN_FORM });
  const [saving, setSaving] = useState(false);

  // JSON viewer
  const [jsonData, setJsonData] = useState<{ open: boolean; title: string; data: unknown }>({ open: false, title: '', data: null });
  const [toolViewer, setToolViewer] = useState<{ open: boolean; captainName: string; loading: boolean; error: string; data: CaptainToolAccessResult | null }>({
    open: false,
    captainName: '',
    loading: false,
    error: '',
    data: null,
  });

  // Confirm dialog
  const [confirm, setConfirm] = useState<{ open: boolean; title: string; message: string; onConfirm: () => void }>({ open: false, title: '', message: '', onConfirm: () => {} });

  // Selection
  const [selected, setSelected] = useState<string[]>([]);

  // Sorting
  const [sortField, setSortField] = useState<SortField>('name');
  const [sortDir, setSortDir] = useState<SortDir>('asc');

  // Column filters
  const [colFilters, setColFilters] = useState({ name: '', runtime: '', state: '' });

  // Pagination
  const [pageNumber, setPageNumber] = useState(1);
  const [pageSize, setPageSize] = useState(25);

  // Endpoint health
  const [healthMap, setHealthMap] = useState<Record<string, CaptainHealthResponse>>({});
  const [healthView, setHealthView] = useState<{ open: boolean; captain: Captain | null; data: CaptainHealthResponse | null; loading: boolean }>({ open: false, captain: null, data: null, loading: false });

  const load = useCallback(async () => {
    try {
      setLoading(true);
      const result = await listCaptains({ pageSize: 9999 });
      setCaptains(result.objects);
      setError('');
    } catch {
      setError(t('Failed to load captains.'));
    } finally {
      setLoading(false);
    }
  }, [t]);

  useEffect(() => { load(); }, [load]);

  // Fetch recent endpoint health for captains that expose a direct API endpoint, for the row histogram.
  useEffect(() => {
    const withEndpoint = captains.filter((c) => !!c.apiEndpointUrl);
    if (withEndpoint.length === 0) return;
    let cancelled = false;
    void Promise.all(withEndpoint.map((c) =>
      getCaptainHealth(c.id).then((data) => ({ id: c.id, data })).catch(() => null),
    )).then((entries) => {
      if (cancelled) return;
      setHealthMap((current) => {
        const next = { ...current };
        for (const entry of entries) if (entry) next[entry.id] = entry.data;
        return next;
      });
    });
    return () => { cancelled = true; };
  }, [captains]);

  const openHealth = useCallback(async (c: Captain) => {
    setHealthView({ open: true, captain: c, data: healthMap[c.id] ?? null, loading: true });
    try {
      const data = await getCaptainHealth(c.id);
      setHealthMap((m) => ({ ...m, [c.id]: data }));
      setHealthView((v) => (v.captain?.id === c.id ? { ...v, data, loading: false } : v));
    } catch {
      setHealthView((v) => (v.captain?.id === c.id ? { ...v, loading: false } : v));
    }
  }, [healthMap]);

  // Filtered rows
  const filtered = useMemo(() => {
    return captains.filter(c =>
      (!colFilters.name || c.name.toLowerCase().includes(colFilters.name.toLowerCase())) &&
      (!colFilters.runtime || c.runtime.toLowerCase().includes(colFilters.runtime.toLowerCase())) &&
      (!colFilters.state || (c.state ?? '').toLowerCase().includes(colFilters.state.toLowerCase()))
    );
  }, [captains, colFilters]);

  // Sorted rows
  const sorted = useMemo(() => {
    const arr = [...filtered];
    arr.sort((a, b) => {
      let va: string = '';
      let vb: string = '';
      switch (sortField) {
        case 'runtime': va = a.runtime.toLowerCase(); vb = b.runtime.toLowerCase(); break;
        case 'state': va = (a.state ?? '').toLowerCase(); vb = (b.state ?? '').toLowerCase(); break;
        case 'createdUtc': va = a.createdUtc; vb = b.createdUtc; break;
        default: va = a.name.toLowerCase(); vb = b.name.toLowerCase();
      }
      if (va < vb) return sortDir === 'asc' ? -1 : 1;
      if (va > vb) return sortDir === 'asc' ? 1 : -1;
      return 0;
    });
    return arr;
  }, [filtered, sortField, sortDir]);

  // Paginated
  const totalPages = Math.max(1, Math.ceil(sorted.length / pageSize));
  const currentPage = Math.min(pageNumber, totalPages);
  const paginated = useMemo(() => {
    const start = (currentPage - 1) * pageSize;
    return sorted.slice(start, start + pageSize);
  }, [sorted, currentPage, pageSize]);

  function handleSort(field: SortField) {
    if (sortField === field) setSortDir(d => d === 'asc' ? 'desc' : 'asc');
    else { setSortField(field); setSortDir('asc'); }
  }

  function sortIcon(field: SortField) {
    if (sortField !== field) return '';
    return sortDir === 'asc' ? ' \u25B2' : ' \u25BC';
  }

  // Selection
  const allSelected = selected.length > 0 && selected.length === filtered.length;
  function toggleSelect(id: string) {
    setSelected(s => s.includes(id) ? s.filter(x => x !== id) : [...s, id]);
  }
  function selectAll() { setSelected(filtered.map(c => c.id)); }
  function clearSelection() { setSelected([]); }

  // CRUD
  function openCreate() {
    setForm({ name: '', runtime: '', systemInstructions: '', model: '', apiEndpointUrl: '', ...EMPTY_MUX_CAPTAIN_FORM });
    setEditing(null);
    setShowForm(true);
  }

  function openEdit(c: Captain) {
    setForm({
      name: c.name,
      runtime: c.runtime,
      systemInstructions: c.systemInstructions ?? '',
      model: c.model ?? '',
      apiEndpointUrl: c.apiEndpointUrl ?? '',
      ...muxFormFromCaptain(c),
    });
    setEditing(c);
    setShowForm(true);
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (saving) return;
    try {
      if (isMuxRuntime(form.runtime) && !form.muxEndpoint.trim()) {
        setError(t('Mux captains require a named Mux endpoint.'));
        return;
      }

      setSaving(true);
      const payload = { ...form } as Record<string, unknown>;
      if (!payload.systemInstructions) delete payload.systemInstructions;
      payload.model = form.model.trim() ? form.model.trim() : null;
      payload.apiEndpointUrl = form.apiEndpointUrl.trim() ? form.apiEndpointUrl.trim() : null;
      payload.runtimeOptionsJson = buildMuxRuntimeOptionsJson(form.runtime, form);
      delete payload.muxConfigDirectory;
      delete payload.muxEndpoint;
      delete payload.muxBaseUrl;
      delete payload.muxAdapterType;
      delete payload.muxTemperature;
      delete payload.muxMaxTokens;
      delete payload.muxSystemPromptPath;
      delete payload.muxApprovalPolicy;
      if (editing) await updateCaptain(editing.id, payload);
      else await createCaptain(payload);
      setShowForm(false);
      pushToast('success', editing
        ? t('Captain "{{name}}" saved.', { name: form.name })
        : t('Captain "{{name}}" created.', { name: form.name }));
      load();
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : t('Save failed.'));
    } finally {
      setSaving(false);
    }
  }

  function handleDelete(id: string, name: string) {
    setConfirm({
      open: true,
      title: t('Delete Captain'),
      message: t('Delete captain "{{name}}"? This cannot be undone.', { name }),
      onConfirm: async () => {
        setConfirm(c => ({ ...c, open: false }));
        try {
          await deleteCaptain(id);
          pushToast('warning', t('Captain "{{name}}" deleted.', { name }));
          load();
        } catch { setError(t('Delete failed.')); }
      },
    });
  }

  function handleBulkDelete() {
    setConfirm({
      open: true,
      title: t('Delete Selected Captains'),
      message: t('Delete {{count}} selected captain(s)? This cannot be undone.', { count: selected.length }),
      onConfirm: async () => {
        setConfirm(c => ({ ...c, open: false }));
        const ids = [...selected];
        setSelected([]);
        let failed = 0;
        for (const id of ids) {
          try { await deleteCaptain(id); } catch { failed++; }
        }
        const deleted = ids.length - failed;
        if (deleted > 0) {
          pushToast(failed > 0 ? 'warning' : 'success', failed > 0
            ? t('Deleted {{deleted}} captains. {{failed}} failed.', { deleted, failed })
            : t('Deleted {{deleted}} captains.', { deleted }));
        }
        if (failed > 0) setError(t('Deleted {{deleted}} captains, {{failed}} failed.', { deleted: ids.length - failed, failed }));
        load();
      },
    });
  }

  function handleStop(id: string, name: string) {
    setConfirm({
      open: true,
      title: t('Stop Captain'),
      message: t('Stop captain "{{name}}"? The captain process will be terminated.', { name }),
      onConfirm: async () => {
        setConfirm(c => ({ ...c, open: false }));
        try {
          await stopCaptain(id);
          pushToast('warning', t('Captain "{{name}}" stopped.', { name }));
          load();
        } catch { setError(t('Stop failed.')); }
      },
    });
  }

  function handleRecall(id: string, name: string) {
    setConfirm({
      open: true,
      title: t('Recall Captain'),
      message: t('Recall captain "{{name}}"? The captain will be recalled from its current mission.', { name }),
      onConfirm: async () => {
        setConfirm(c => ({ ...c, open: false }));
        try {
          await recallCaptain(id);
          pushToast('warning', t('Captain "{{name}}" recalled.', { name }));
          load();
        } catch { setError(t('Recall failed.')); }
      },
    });
  }

  function handleRestart(id: string, name: string) {
    setConfirm({
      open: true,
      title: t('Restart Captain'),
      message: t('Restart captain "{{name}}"? The captain will be deleted and recreated with the same saved configuration.', { name }),
      onConfirm: async () => {
        setConfirm(c => ({ ...c, open: false }));
        try {
          await restartCaptain(id);
          pushToast('success', t('Captain "{{name}}" restarted.', { name }));
          load();
        } catch { setError(t('Restart failed.')); }
      },
    });
  }

  function handleStopAll() {
    setConfirm({
      open: true,
      title: t('Stop All Captains'),
      message: t('Stop ALL captains? All captain processes will be terminated. This cannot be undone.'),
      onConfirm: async () => {
        setConfirm(c => ({ ...c, open: false }));
        try {
          await stopAllCaptains();
          pushToast('warning', t('All captains stopped.'));
          load();
        } catch { setError(t('Stop all failed.')); }
      },
    });
  }

  async function handleDuplicate(captain: Captain) {
    try {
      const created = await createCaptain(buildCaptainDuplicatePayload(captain));
      pushToast('success', t('Captain "{{name}}" duplicated.', { name: created.name }));
      navigate(`/captains/${created.id}`);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Duplicate failed.'));
    }
  }

  async function handleViewTools(captain: Captain) {
    setToolViewer({
      open: true,
      captainName: captain.name,
      loading: true,
      error: '',
      data: null,
    });

    try {
      const result = await getCaptainTools(captain.id);
      setToolViewer({
        open: true,
        captainName: captain.name,
        loading: false,
        error: '',
        data: result,
      });
    } catch {
      setToolViewer({
        open: true,
        captainName: captain.name,
        loading: false,
        error: t('Failed to load captain tools.'),
        data: null,
      });
    }
  }

  function handleStartPlanning(captain: Captain) {
    navigate('/planning', {
      state: {
        captainId: captain.id,
      },
    });
  }

  return (
    <div>
      <PageHeader
        title={t('Captains')}
        subtitle={t('AI agent processes that execute missions. Monitor heartbeat, state, and manage captain lifecycle.')}
        actions={(
          <>
            {selected.length > 0 && (
              <button className="btn btn-sm btn-danger" onClick={handleBulkDelete}>
                {t('Delete Selected')} ({selected.length})
              </button>
            )}
            <button className="btn btn-sm btn-danger" onClick={handleStopAll} title={t('Stop all captain processes')}>{t('Stop All')}</button>
            <button className="btn btn-primary btn-sm" onClick={openCreate}>+ {t('Captain')}</button>
            <RefreshButton onRefresh={load} title={t('Refresh captain data')} />
          </>
        )}
      />

      <ErrorModal error={error} onClose={() => setError('')} />

      {/* Create/Edit Modal */}
      {showForm && (
        <div className="modal-overlay" onClick={() => setShowForm(false)}>
          <form className={`modal modal-captain${isMuxRuntime(form.runtime) ? ' modal-mux' : ''}`} onClick={e => e.stopPropagation()} onSubmit={handleSubmit}>
            <h3>{editing ? t('Edit Captain') : t('Create Captain')}</h3>
            <label>{t('Name')}<input value={form.name} onChange={e => setForm({ ...form, name: e.target.value })} required /></label>
            <label title={t('The AI agent runtime this captain will use')}>{t('Runtime')}
              <select value={form.runtime} onChange={e => setForm({ ...form, runtime: e.target.value })} required>
                <option value="">{t('Select runtime...')}</option>
                <option value="ClaudeCode">Claude Code</option>
                <option value="Codex">Codex</option>
                <option value="Gemini">Gemini</option>
                <option value="Cursor">Cursor</option>
                <option value="Mux">Mux</option>
              </select>
            </label>
            <label title={t('Optional AI model identifier. Leave blank to let the runtime choose its default model.')}>
              {t('Model')}
              <input value={form.model} onChange={e => setForm({ ...form, model: e.target.value })} placeholder={t('e.g., gpt-5.4-mini')} />
            </label>
            <MuxRuntimeFields
              runtime={form.runtime}
              form={form}
              onChange={(patch) => setForm((current) => ({ ...current, ...patch }))}
              t={t}
            />
            <label title={t('Optional instructions injected into every mission prompt for this captain. Use this to specialize behavior, add guardrails, or provide persistent context.')}>
              {t('System Instructions')}
              <textarea value={form.systemInstructions} onChange={e => setForm({ ...form, systemInstructions: e.target.value })} rows={4} placeholder={t('e.g., You are a testing specialist. Always run tests before committing...')} />
            </label>
            <label title={t('Optional direct API endpoint URL for this captain. When set, the Admiral periodically health-checks it and shows a health histogram on the Captains table.')}>
              {t('API Endpoint URL')}
              <input type="url" value={form.apiEndpointUrl} onChange={e => setForm({ ...form, apiEndpointUrl: e.target.value })} placeholder="https://host:port/health" />
            </label>
            <div className="modal-actions">
              <button type="submit" className="btn btn-primary" disabled={saving}>{saving ? t('Saving...') : t('Save')}</button>
              <button type="button" className="btn" onClick={() => setShowForm(false)} disabled={saving}>{t('Cancel')}</button>
            </div>
          </form>
        </div>
      )}

      {/* JSON Viewer */}
      <JsonViewer open={jsonData.open} title={jsonData.title} data={jsonData.data} onClose={() => setJsonData({ open: false, title: '', data: null })} />
      <CaptainToolViewer
        open={toolViewer.open}
        captainName={toolViewer.captainName}
        loading={toolViewer.loading}
        error={toolViewer.error}
        data={toolViewer.data}
        onClose={() => setToolViewer({ open: false, captainName: '', loading: false, error: '', data: null })}
      />

      {/* View Health Modal */}
      {healthView.open && (
        <div className="modal-overlay" onClick={() => setHealthView({ open: false, captain: null, data: null, loading: false })}>
          <div className="modal modal-large" onClick={e => e.stopPropagation()}>
            <h3>{t('Endpoint Health')}: {healthView.captain?.name}</h3>
            <p className="text-dim mono" style={{ marginTop: 0, wordBreak: 'break-all' }}>{healthView.captain?.apiEndpointUrl}</p>
            {healthView.loading && !healthView.data ? (
              <p className="text-dim">{t('Loading...')}</p>
            ) : (
              <>
                <div className="captain-health-summary">
                  <div><span>{t('Recent checks')}</span><strong>{healthView.data?.totalChecks ?? 0}</strong></div>
                  <div><span>{t('Healthy')}</span><strong>{healthView.data?.healthyChecks ?? 0}</strong></div>
                  <div>
                    <span>{t('Success rate')}</span>
                    <strong>{healthView.data && healthView.data.totalChecks > 0 ? `${Math.round((healthView.data.healthyChecks / healthView.data.totalChecks) * 100)}%` : '-'}</strong>
                  </div>
                </div>
                <div style={{ margin: '0.75rem 0' }}>
                  <CaptainHealthHistogram data={healthView.data} bars={50} />
                </div>
                <div className="table-wrap" style={{ maxHeight: '320px', overflow: 'auto' }}>
                  <table>
                    <thead>
                      <tr>
                        <th>{t('When')}</th>
                        <th>{t('Result')}</th>
                        <th>{t('Status')}</th>
                        <th>{t('Latency')}</th>
                        <th>{t('Error')}</th>
                      </tr>
                    </thead>
                    <tbody>
                      {(healthView.data?.results ?? []).slice().reverse().map((r, i) => (
                        <tr key={`${r.checkedUtc}-${i}`}>
                          <td className="text-dim" title={formatDateTime(r.checkedUtc)}>{formatRelativeTime(r.checkedUtc)}</td>
                          <td><StatusBadge status={r.healthy ? 'Healthy' : 'Unhealthy'} /></td>
                          <td className="text-dim">{r.statusCode ?? '-'}</td>
                          <td className="text-dim">{r.latencyMs.toFixed(0)} ms</td>
                          <td className="text-dim">{r.error ?? '-'}</td>
                        </tr>
                      ))}
                      {(healthView.data?.results ?? []).length === 0 && (
                        <tr><td colSpan={5} className="text-dim">{t('No health checks recorded yet. The Admiral checks configured endpoints periodically.')}</td></tr>
                      )}
                    </tbody>
                  </table>
                </div>
              </>
            )}
            <div className="modal-actions">
              <button type="button" className="btn" onClick={() => setHealthView({ open: false, captain: null, data: null, loading: false })}>{t('Close')}</button>
            </div>
          </div>
        </div>
      )}

      {/* Confirm Dialog */}
      <ConfirmDialog open={confirm.open} title={confirm.title} message={confirm.message}
        onConfirm={confirm.onConfirm} onCancel={() => setConfirm(c => ({ ...c, open: false }))} />

      {loading && captains.length === 0 && <p className="text-dim">{t('Loading...')}</p>}
      {!loading && captains.length === 0 && <p className="text-dim">{t('No captains configured.')}</p>}

      {captains.length > 0 && (
        <>
          <Pagination pageNumber={currentPage} pageSize={pageSize} totalPages={totalPages}
            totalRecords={sorted.length}
            onPageChange={p => setPageNumber(p)} onPageSizeChange={s => { setPageSize(s); setPageNumber(1); }} />

          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th className="col-checkbox">
                    <input type="checkbox" checked={allSelected} onChange={e => e.target.checked ? selectAll() : clearSelection()} title={t('Select all captains')} />
                  </th>
                  <th className="sortable" onClick={() => handleSort('name')} title={t('Captain name -- click to sort')}>
                    {t('Name')}{sortIcon('name')}
                  </th>
                  <th>{t('ID')}</th>
                  <th className="sortable" onClick={() => handleSort('runtime')} title={t('Runtime -- click to sort')}>
                    {t('Runtime')}{sortIcon('runtime')}
                  </th>
                  <th className="sortable" onClick={() => handleSort('state')} title={t('State -- click to sort')}>
                    {t('State')}{sortIcon('state')}
                  </th>
                  <th>{t('Health')}</th>
                  <th>{t('Current Mission')}</th>
                  <th>{t('Heartbeat')}</th>
                  <th className="sortable" onClick={() => handleSort('createdUtc')} title={t('Created date -- click to sort')}>
                    {t('Created')}{sortIcon('createdUtc')}
                  </th>
                  <th className="text-right">{t('Actions')}</th>
                </tr>
                <tr className="column-filter-row">
                  <td></td>
                  <td><input type="text" className="col-filter" value={colFilters.name} onChange={e => { setColFilters(f => ({ ...f, name: e.target.value })); setPageNumber(1); }} placeholder={t('Filter...')} /></td>
                  <td></td>
                  <td><input type="text" className="col-filter" value={colFilters.runtime} onChange={e => { setColFilters(f => ({ ...f, runtime: e.target.value })); setPageNumber(1); }} placeholder={t('Filter...')} /></td>
                  <td><input type="text" className="col-filter" value={colFilters.state} onChange={e => { setColFilters(f => ({ ...f, state: e.target.value })); setPageNumber(1); }} placeholder={t('Filter...')} /></td>
                  <td></td>
                  <td></td>
                  <td></td>
                  <td></td>
                  <td></td>
                </tr>
              </thead>
              <tbody>
                {paginated.map(c => (
                  <tr key={c.id} className="clickable" onClick={() => openEdit(c)}>
                    <td className="col-checkbox" onClick={e => e.stopPropagation()}>
                      <input type="checkbox" checked={selected.includes(c.id)} onChange={() => toggleSelect(c.id)} title={t('Select this captain')} />
                    </td>
                    <td><strong>{c.name}</strong></td>
                    <td className="mono text-dim table-id-cell">
                      <span className="id-display">
                        <span className="id-value" title={c.id}>{c.id}</span>
                        <CopyButton text={c.id} onClick={e => e.stopPropagation()} />
                      </span>
                    </td>
                    <td className="text-dim">{c.runtime}</td>
                    <td><StatusBadge status={c.state} /></td>
                    <td onClick={e => e.stopPropagation()}>
                      {c.apiEndpointUrl
                        ? <CaptainHealthHistogram data={healthMap[c.id]} onClick={() => void openHealth(c)} />
                        : <span className="text-dim">-</span>}
                    </td>
                    <td className="mono text-dim" onClick={e => e.stopPropagation()}>
                      {c.currentMissionId ? (
                        <a href="#" onClick={e => { e.preventDefault(); navigate(`/missions/${c.currentMissionId}`); }}>
                          {c.currentMissionId.substring(0, 8)}...
                        </a>
                      ) : '-'}
                    </td>
                    <td className="text-dim" title={formatDateTime(c.lastHeartbeatUtc)}>{formatRelativeTime(c.lastHeartbeatUtc)}</td>
                    <td className="text-dim" title={formatDateTime(c.createdUtc)}>{formatRelativeTime(c.createdUtc)}</td>
                    <td className="text-right" onClick={e => e.stopPropagation()}>
                      <ActionMenu id={`captain-${c.id}`} items={[
                        { label: 'View Detail', onClick: () => navigate(`/captains/${c.id}`) },
                        ...(canCaptainStartPlanning(c) ? [{ label: 'Start Planning', onClick: () => handleStartPlanning(c) }] : []),
                        { label: 'Edit', onClick: () => openEdit(c) },
                        { label: 'Duplicate', onClick: () => void handleDuplicate(c) },
                        { label: 'View Tools', onClick: () => void handleViewTools(c) },
                        ...(c.apiEndpointUrl ? [{ label: 'View Health', onClick: () => void openHealth(c) }] : []),
                        { label: 'View JSON', onClick: () => setJsonData({ open: true, title: `${t('Captain')}: ${c.name}`, data: c }) },
                        { label: 'Stop', onClick: () => handleStop(c.id, c.name) },
                        { label: 'Recall', onClick: () => handleRecall(c.id, c.name) },
                        { label: 'Restart', onClick: () => handleRestart(c.id, c.name) },
                        { label: 'Delete', danger: true, onClick: () => handleDelete(c.id, c.name) },
                      ]} />
                    </td>
                  </tr>
                ))}
                {paginated.length === 0 && (
                  <tr><td colSpan={10} className="text-dim">{t('No captains match the current filters.')}</td></tr>
                )}
              </tbody>
            </table>
          </div>
        </>
      )}
    </div>
  );
}
