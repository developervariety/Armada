import { useEffect, useState, useCallback } from 'react';
import { useNavigate } from 'react-router-dom';
import { createCaptain, updateCaptain, deleteCaptain, stopCaptain, stopAllCaptains, restartCaptain, getCaptainTools, listModelEndpoints, quarantineCaptain, unquarantineCaptain, listAllCaptains } from '../api/client';
import type { Captain, CaptainQuarantineRequest, CaptainToolAccessResult, ModelEndpoint } from '../types/models';
import CaptainQuarantineDialog from '../components/captains/CaptainQuarantineDialog';
import Pagination from '../components/shared/Pagination';
import ActionMenu from '../components/shared/ActionMenu';
import StatusBadge from '../components/shared/StatusBadge';
import ConfirmDialog from '../components/shared/ConfirmDialog';
import MuxRuntimeFields from '../components/captains/MuxRuntimeFields';
import CaptainTierBadge from '../components/shared/CaptainTierBadge';
import { parsePreferenceRank } from '../lib/captainTier';
import CaptainToolViewer from '../components/captains/CaptainToolViewer';
import JsonViewer from '../components/shared/JsonViewer';
import CopyButton from '../components/shared/CopyButton';
import RefreshButton from '../components/shared/RefreshButton';
import AutoRefreshSelect from '../components/shared/AutoRefreshSelect';
import { useAutoRefresh } from '../lib/useAutoRefresh';
import { useLoadError } from '../lib/useLoadError';
import { useResourceTable } from '../lib/useResourceTable';
import PageHeader from '../components/shared/PageHeader';
import ErrorModal from '../components/shared/ErrorModal';
import { useAuth } from '../context/AuthContext';
import { useLocale } from '../context/LocaleContext';
import { useNotifications } from '../context/NotificationContext';
import { canCaptainStartPlanning } from '../lib/captains';
import { buildMuxRuntimeOptionsJson, EMPTY_MUX_CAPTAIN_FORM, isMuxRuntime, muxFormFromCaptain, type MuxCaptainFormFields } from '../lib/mux';
import { buildCaptainDuplicatePayload } from '../lib/duplicates';

// Column values for filtering and sorting; text compares without case.
const CAPTAIN_COLUMNS: Record<string, (c: Captain) => string> = {
  name: c => c.name.toLowerCase(),
  runtime: c => c.runtime.toLowerCase(),
  state: c => (c.state ?? '').toLowerCase(),
  createdUtc: c => c.createdUtc,
};
type CaptainFormState = {
  name: string;
  runtime: string;
  systemInstructions: string;
  model: string;
  modelEndpointId: string;
  tier: string;
  preferenceRank: string;
  allowedPersonas: string;
  preferredPersona: string;
} & MuxCaptainFormFields;

const EMPTY_CAPTAIN_FORM: CaptainFormState = {
  name: '', runtime: '', systemInstructions: '', model: '', modelEndpointId: '', tier: '', preferenceRank: '0', allowedPersonas: '', preferredPersona: '',
  ...EMPTY_MUX_CAPTAIN_FORM,
};

export default function Captains() {
  const navigate = useNavigate();
  const { t, formatRelativeTime, formatDateTime } = useLocale();
  const { pushToast } = useNotifications();
  const { isAdmin } = useAuth();
  const [captains, setCaptains] = useState<Captain[]>([]);
  const [loading, setLoading] = useState(true);
  const { error, setError, loadFailed, loadSucceeded } = useLoadError();

  // Modal state
  const [showForm, setShowForm] = useState(false);
  const [editing, setEditing] = useState<Captain | null>(null);
  const [form, setForm] = useState<CaptainFormState>(EMPTY_CAPTAIN_FORM);
  const [saving, setSaving] = useState(false);
  const [inferenceEndpoints, setInferenceEndpoints] = useState<ModelEndpoint[]>([]);

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
  const [quarantineTarget, setQuarantineTarget] = useState<Captain | null>(null);
  const [quarantining, setQuarantining] = useState(false);

  const load = useCallback(async () => {
    try {
      setLoading(true);
      const result = await listAllCaptains();
      setCaptains(result);
      loadSucceeded();
    } catch {
      loadFailed(t('Failed to load captains.'));
    } finally {
      setLoading(false);
    }
  }, [t]);

  useEffect(() => { load(); }, [load]);
  const { seconds: refreshSeconds, setSeconds: setRefreshSeconds } = useAutoRefresh('captains', load);

  const {
    colFilters, setColFilter, handleSort, sortIcon, pageSize, setPageNumber, setPageSize, totalPages, currentPage,
    sorted, paginated, selected, setSelected, toggleSelect, allSelected, selectAll, clearSelection,
  } = useResourceTable<Captain>({ rows: captains, getId: c => c.id, columnValues: CAPTAIN_COLUMNS, initialSortField: 'name' });

  // Load the configured inference endpoints so an API-endpoint captain can be pointed at one.
  useEffect(() => {
    listModelEndpoints()
      .then(result => setInferenceEndpoints((result ?? []).filter(e => e.kind === 'Inference')))
      .catch(() => setInferenceEndpoints([]));
  }, []);

  // CRUD
  function openCreate() {
    setForm(EMPTY_CAPTAIN_FORM);
    setEditing(null);
    setShowForm(true);
  }

  function openEdit(c: Captain) {
    setForm({
      name: c.name,
      runtime: c.runtime,
      systemInstructions: c.systemInstructions ?? '',
      model: c.model ?? '',
      modelEndpointId: c.modelEndpointId ?? '',
      tier: c.tier ?? '',
      preferenceRank: String(c.preferenceRank ?? 0),
      allowedPersonas: c.allowedPersonas ?? '',
      preferredPersona: c.preferredPersona ?? '',
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

      if (form.runtime === 'ApiEndpoint' && !form.modelEndpointId) {
        setError(t('API-endpoint captains require an inference endpoint. Select one, or add it under Configuration > Endpoints.'));
        return;
      }

      setSaving(true);
      const payload = { ...form } as Record<string, unknown>;
      if (!payload.systemInstructions) delete payload.systemInstructions;
      payload.model = form.model.trim() ? form.model.trim() : null;
      // Every captain draws external credentials from a referenced inference endpoint, never inline
      // fields: an API-endpoint captain requires one; a native runtime may optionally reference one to
      // override the host default; a Mux captain configures its endpoint through its own fields.
      payload.modelEndpointId = isMuxRuntime(form.runtime) ? null : (form.modelEndpointId || null);
      payload.tier = form.tier ? form.tier : null;
      payload.preferenceRank = parsePreferenceRank(form.preferenceRank);
      payload.allowedPersonas = form.allowedPersonas.trim() ? form.allowedPersonas.trim() : null;
      payload.preferredPersona = form.preferredPersona.trim() ? form.preferredPersona.trim() : null;
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

  async function handleQuarantineSubmit(request: CaptainQuarantineRequest) {
    if (!quarantineTarget) return;
    const target = quarantineTarget;
    setQuarantining(true);
    try {
      await quarantineCaptain(target.id, request);
      pushToast('warning', t('Captain "{{name}}" quarantined.', { name: target.name }));
      setQuarantineTarget(null);
      load();
    } catch (e) {
      setError(e instanceof Error ? e.message : t('Quarantine failed.'));
    } finally {
      setQuarantining(false);
    }
  }

  async function handleLiftQuarantine(id: string, name: string) {
    try {
      const result = await unquarantineCaptain(id);
      if (result.outcome === 'Released') pushToast('success', t('Quarantine lifted for "{{name}}".', { name }));
      else pushToast('warning', t('Captain "{{name}}" was not quarantined; nothing changed.', { name }));
      load();
    } catch (e) {
      setError(e instanceof Error ? e.message : t('Failed to lift quarantine.'));
    }
  }

  function handleRestart(id: string, name: string) {
    setConfirm({
      open: true,
      title: t('Restart Captain'),
      message: t('Restart captain "{{name}}"? Its process and assignment are reset; its configuration, identity and any quarantine are kept.', { name }),
      onConfirm: async () => {
        setConfirm(c => ({ ...c, open: false }));
        try {
          await restartCaptain(id);
          pushToast('success', t('Captain "{{name}}" restarted.', { name }));
          load();
        } catch (e) { setError(e instanceof Error ? e.message : t('Restart failed.')); }
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
          const result = await stopAllCaptains();
          // Reload first: a successful load clears the error banner.
          await load();
          if (result && result.failed > 0) {
            setError(t('Stop all stopped {{stopped}} and could not stop {{failed}}: {{failures}}', {
              stopped: result.stopped,
              failed: result.failed,
              failures: result.failures.map(f => `${f.kind} ${f.id}: ${f.message}`).join('; '),
            }));
          } else {
            pushToast('warning', t('All captains stopped.'));
          }
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
        subtitle={t('AI agent harness processes that execute missions. Monitor state, current mission, and captain lifecycle.')}
        actions={(
          <>
            <AutoRefreshSelect seconds={refreshSeconds} onChange={setRefreshSeconds} />
            <RefreshButton onRefresh={load} title={t('Refresh captain data')} />
            {selected.length > 0 && (
              <button className="btn btn-sm btn-danger" onClick={handleBulkDelete}>
                {t('Delete Selected')} ({selected.length})
              </button>
            )}
            {/* Stopping every captain acts on every tenant, so the route needs a global administrator. */}
            {isAdmin && <button className="btn btn-sm btn-danger" onClick={handleStopAll} title={t('Stop all captain processes')}>{t('Stop All')}</button>}
            <button className="btn btn-primary btn-sm" onClick={openCreate}>+ {t('Captain')}</button>
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
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '0 1rem' }}>
              <label title={t('The AI agent runtime this captain will use')}>{t('Runtime')}
                <select value={form.runtime} onChange={e => setForm({ ...form, runtime: e.target.value })} required>
                  <option value="">{t('Select runtime...')}</option>
                  <option value="ClaudeCode">Claude Code</option>
                  <option value="Codex">Codex</option>
                  <option value="Gemini">Gemini</option>
                  <option value="Cursor">Cursor</option>
                  <option value="Mux">Mux</option>
                  <option value="OpenCode">OpenCode</option>
                  <option value="ApiEndpoint">API Endpoint</option>
                </select>
              </label>
              <label title={t('Optional AI model identifier. Leave blank to let the runtime choose its default model.')}>
                {t('Model')}
                <input value={form.model} onChange={e => setForm({ ...form, model: e.target.value })} placeholder={form.runtime === 'ApiEndpoint' ? t('Optional; overrides the endpoint model') : t('e.g., gpt-5.4-mini')} />
              </label>
            </div>
            {form.runtime !== '' && !isMuxRuntime(form.runtime) && (
              <label title={form.runtime === 'ApiEndpoint'
                ? t('The configured inference endpoint this captain drives. Manage endpoints under Configuration > Endpoints.')
                : t('Optional. Resolve this captain\'s base URL and key from an inference endpoint instead of the host provider default. Manage endpoints under Configuration > Endpoints.')}>
                {form.runtime === 'ApiEndpoint' ? t('Inference Endpoint') : t('Inference Endpoint (optional)')}
                <select value={form.modelEndpointId} onChange={e => setForm({ ...form, modelEndpointId: e.target.value })} required={form.runtime === 'ApiEndpoint'}>
                  <option value="">{form.runtime === 'ApiEndpoint' ? t('Select an inference endpoint...') : t('Host provider default (no endpoint)')}</option>
                  {inferenceEndpoints.map(ep => (
                    <option key={ep.id} value={ep.id}>{ep.name} ({ep.provider}{ep.model ? ' / ' + ep.model : ''})</option>
                  ))}
                </select>
                {form.runtime === 'ApiEndpoint' && inferenceEndpoints.length === 0 && (
                  <small className="text-dim" style={{ display: 'block', marginTop: '0.25rem' }}>
                    {t('No inference endpoints configured. Add one under Configuration > Endpoints first.')}
                  </small>
                )}
              </label>
            )}
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '0 1rem' }}>
              <label title={t('Persona preferred for dispatch routing priority.')}>
                {t('Preferred Persona')}
                <input value={form.preferredPersona} onChange={e => setForm({ ...form, preferredPersona: e.target.value })} placeholder={t('e.g., Worker')} />
              </label>
              <label title={t('Missions requiring a tier route to captains at or above it. Leave on Auto to classify from the model name.')}>
                {t('Capability tier')}
                <select value={form.tier} onChange={e => setForm({ ...form, tier: e.target.value })}>
                  <option value="">{t('Auto (classify from model)')}</option>
                  <option value="Economy">{t('Economy')}</option>
                  <option value="Standard">{t('Standard')}</option>
                  <option value="Premium">{t('Premium')}</option>
                </select>
              </label>
              <label title={t('Among captains of the same tier, a higher rank is tried first. Equal ranks are equal peers. Range -1000 to 1000.')}>
                {t('Preference rank')}
                <input type="number" min={-1000} max={1000} step={1} value={form.preferenceRank} onChange={e => setForm({ ...form, preferenceRank: e.target.value })} />
              </label>
            </div>
            <label title={t('JSON array of persona names this captain may fill. Null means any persona.')}>
              {t('Allowed Personas (JSON array)')}
              <textarea value={form.allowedPersonas} onChange={e => setForm({ ...form, allowedPersonas: e.target.value })} rows={2} placeholder={t('["Worker", "Judge"]')} />
            </label>
            <MuxRuntimeFields
              runtime={form.runtime}
              form={form}
              onChange={(patch) => setForm((current) => ({ ...current, ...patch }))}
              t={t}
            />
            <label className="captain-instructions-field" title={t('Optional instructions injected into every mission prompt for this captain. Use this to specialize behavior, add guardrails, or provide persistent context.')}>
              {t('System Instructions')}
              <textarea value={form.systemInstructions} onChange={e => setForm({ ...form, systemInstructions: e.target.value })} rows={4} placeholder={t('e.g., You are a testing specialist. Always run tests before committing...')} />
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

      {/* Confirm Dialog */}
      <CaptainQuarantineDialog
        open={quarantineTarget !== null}
        captainName={quarantineTarget?.name ?? ''}
        t={t}
        submitting={quarantining}
        onSubmit={request => void handleQuarantineSubmit(request)}
        onCancel={() => setQuarantineTarget(null)}
      />
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
                  <th>{t('Current Mission')}</th>
                  <th>{t('Heartbeat')}</th>
                  <th className="sortable" onClick={() => handleSort('createdUtc')} title={t('Created date -- click to sort')}>
                    {t('Created')}{sortIcon('createdUtc')}
                  </th>
                  <th className="text-right">{t('Actions')}</th>
                </tr>
                <tr className="column-filter-row">
                  <td></td>
                  <td><input type="text" className="col-filter" value={colFilters.name ?? ''} onChange={e => setColFilter('name', e.target.value)} placeholder={t('Filter...')} /></td>
                  <td></td>
                  <td><input type="text" className="col-filter" value={colFilters.runtime ?? ''} onChange={e => setColFilter('runtime', e.target.value)} placeholder={t('Filter...')} /></td>
                  <td><input type="text" className="col-filter" value={colFilters.state ?? ''} onChange={e => setColFilter('state', e.target.value)} placeholder={t('Filter...')} /></td>
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
                    <td><strong>{c.name}</strong>{c.tier ? <> <CaptainTierBadge tier={c.tier} /></> : null}</td>
                    <td className="mono text-dim table-id-cell">
                      <span className="id-display">
                        <span className="id-value" title={c.id}>{c.id}</span>
                        <CopyButton text={c.id} onClick={e => e.stopPropagation()} />
                      </span>
                    </td>
                    <td className="text-dim">{c.runtime}</td>
                    <td>
                      <StatusBadge status={c.state} />
                      {c.state === 'Quarantined' && (
                        <span className="tag stalled" title={c.quarantineReason || undefined} style={{ marginLeft: '0.35rem' }}>
                          {c.quarantineUntilUtc ? t('until {{time}}', { time: formatRelativeTime(c.quarantineUntilUtc) }) : t('quarantined')}
                        </span>
                      )}
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
                        { label: 'View JSON', onClick: () => setJsonData({ open: true, title: `${t('Captain')}: ${c.name}`, data: c }) },
                        { label: 'View Notifications', onClick: () => navigate('/inbox') },
                        ...(c.state === 'Quarantined'
                          ? [{ label: 'Lift Quarantine', onClick: () => void handleLiftQuarantine(c.id, c.name) }]
                          : [{ label: 'Quarantine', onClick: () => setQuarantineTarget(c) }]),
                        { label: 'Stop', onClick: () => handleStop(c.id, c.name) },
                        { label: 'Restart', onClick: () => handleRestart(c.id, c.name) },
                        { label: 'Delete', danger: true, onClick: () => handleDelete(c.id, c.name) },
                      ]} />
                    </td>
                  </tr>
                ))}
                {paginated.length === 0 && (
                  <tr><td colSpan={9} className="text-dim">{t('No captains match the current filters.')}</td></tr>
                )}
              </tbody>
            </table>
          </div>
        </>
      )}
    </div>
  );
}
