import { useEffect, useState, useCallback, useRef } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import {
  createCaptain,
  getCaptain,
  getCaptainTools,
  getCaptainLog,
  listModelEndpoints,
  stopCaptain,
  quarantineCaptain,
  unquarantineCaptain,
  getMission,
  listMissionSummaries,
  updateCaptain,
  deleteCaptain,
} from '../api/client';
import type { Captain, CaptainQuarantineRequest, ModelEndpoint, Mission, MissionSummary, LogResult, FormattedLogEntry, CaptainToolAccessResult } from '../types/models';
import RuntimeLogEntries from '../components/shared/RuntimeLogEntries';
import CaptainQuarantineDialog from '../components/captains/CaptainQuarantineDialog';
import ActionMenu from '../components/shared/ActionMenu';
import MuxRuntimeFields from '../components/captains/MuxRuntimeFields';
import CaptainToolViewer from '../components/captains/CaptainToolViewer';
import ConfirmDialog from '../components/shared/ConfirmDialog';
import ErrorModal from '../components/shared/ErrorModal';
import JsonViewer from '../components/shared/JsonViewer';
import PageHeader from '../components/shared/PageHeader';
import StatusBadge from '../components/shared/StatusBadge';
import CaptainTierBadge from '../components/shared/CaptainTierBadge';
import AutoRefreshSelect from '../components/shared/AutoRefreshSelect';
import { useAutoRefresh } from '../lib/useAutoRefresh';
import CopyButton from '../components/shared/CopyButton';
import { useLocale } from '../context/LocaleContext';
import { useNotifications } from '../context/NotificationContext';
import { buildMuxRuntimeOptionsJson, EMPTY_MUX_CAPTAIN_FORM, isMuxRuntime, muxFormFromCaptain, parseMuxCaptainOptions, type MuxCaptainFormFields } from '../lib/mux';
import { EMPTY_CAPTAIN_CREDENTIAL_FORM, credentialFormFromCaptain, normalizeCredential, type CaptainCredentialFormFields } from '../lib/captainCredential';
import ProviderCredentialFields from '../components/captains/ProviderCredentialFields';
import { buildCaptainDuplicatePayload } from '../lib/duplicates';

const RUNTIMES = ['ClaudeCode', 'Codex', 'Gemini', 'Cursor', 'Mux', 'OpenCode', 'ApiEndpoint', 'Custom'];
type CaptainDetailFormState = {
  name: string;
  runtime: string;
  systemInstructions: string;
  model: string;
  modelEndpointId: string;
  tier: string;
  allowedPersonas: string;
  preferredPersona: string;
} & MuxCaptainFormFields & CaptainCredentialFormFields;

export default function CaptainDetail() {
  const { t, formatDateTime, formatRelativeTime } = useLocale();
  const { pushToast } = useNotifications();
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const [captain, setCaptain] = useState<Captain | null>(null);
  const [currentMission, setCurrentMission] = useState<Mission | null>(null);
  const [missions, setMissions] = useState<MissionSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [notFound, setNotFound] = useState(false);
  const captainLoadedRef = useRef(false);
  const [quarantineOpen, setQuarantineOpen] = useState(false);
  const [quarantining, setQuarantining] = useState(false);

  // Edit
  const [showForm, setShowForm] = useState(false);
  const [form, setForm] = useState<CaptainDetailFormState>({ name: '', runtime: 'ClaudeCode', systemInstructions: '', model: '', modelEndpointId: '', tier: '', allowedPersonas: '', preferredPersona: '', ...EMPTY_MUX_CAPTAIN_FORM, ...EMPTY_CAPTAIN_CREDENTIAL_FORM });
  const [saving, setSaving] = useState(false);
  const [inferenceEndpoints, setInferenceEndpoints] = useState<ModelEndpoint[]>([]);

  // Log viewer
  const [logText, setLogText] = useState<string | null>(null);
  const [logEntries, setLogEntries] = useState<FormattedLogEntry[] | null>(null);
  const [logEntriesTruncated, setLogEntriesTruncated] = useState(false);
  const [logReadable, setLogReadable] = useState(true);
  const [logLoading, setLogLoading] = useState(false);
  const [showLog, setShowLog] = useState(false);
  const [logInfo, setLogInfo] = useState('');

  // JSON viewer
  const [jsonData, setJsonData] = useState<{ open: boolean; title: string; data: unknown }>({ open: false, title: '', data: null });
  const [toolViewer, setToolViewer] = useState<{ open: boolean; loading: boolean; error: string; data: CaptainToolAccessResult | null }>({
    open: false,
    loading: false,
    error: '',
    data: null,
  });

  // Confirm
  const [confirm, setConfirm] = useState<{ open: boolean; title: string; message: string; onConfirm: () => void }>({ open: false, title: '', message: '', onConfirm: () => {} });

  // The spinner shows only on the first load, so an auto-refresh keeps the page on screen.
  const load = useCallback(async () => {
    if (!id) return;
    const isInitialLoad = !captainLoadedRef.current;
    if (isInitialLoad) setLoading(true);
    try {
      const cap = await getCaptain(id);
      setCaptain(cap);
      setNotFound(false);
      captainLoadedRef.current = true;
      // Load current mission if set
      if (cap.currentMissionId) {
        try {
          const m = await getMission(cap.currentMissionId);
          setCurrentMission(m);
        } catch { setCurrentMission(null); }
      } else {
        setCurrentMission(null);
      }
      // Load missions assigned to this captain
      try {
        const mResult = await listMissionSummaries({ pageSize: 100, filters: { captainId: id } });
        setMissions(mResult.objects || []);
      } catch { setMissions([]); }
      if (isInitialLoad) setError('');
    } catch (e: unknown) {
      if ((e as { status?: number } | null)?.status === 404) {
        setCaptain(null);
        setNotFound(true);
      } else if (isInitialLoad) {
        setError(t('Failed to load captain.'));
      }
    } finally {
      setLoading(false);
    }
  }, [id, t]);

  useEffect(() => { load(); }, [load]);
  const { seconds: refreshSeconds, setSeconds: setRefreshSeconds } = useAutoRefresh('captain-detail', load);

  // Load the configured inference endpoints so an API-endpoint captain can be pointed at one.
  useEffect(() => {
    listModelEndpoints()
      .then(result => setInferenceEndpoints((result ?? []).filter(e => e.kind === 'Inference')))
      .catch(() => setInferenceEndpoints([]));
  }, []);

  function openEdit() {
    if (!captain) return;
    setForm({
      name: captain.name,
      runtime: captain.runtime || 'ClaudeCode',
      systemInstructions: captain.systemInstructions ?? '',
      model: captain.model ?? '',
      modelEndpointId: captain.modelEndpointId ?? '',
      tier: captain.tier ?? '',
      allowedPersonas: captain.allowedPersonas ?? '',
      preferredPersona: captain.preferredPersona ?? '',
      ...muxFormFromCaptain(captain),
      ...credentialFormFromCaptain(captain),
    });
    setShowForm(true);
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!captain || saving) return;
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
      payload.modelEndpointId = form.runtime === 'ApiEndpoint' ? (form.modelEndpointId || null) : null;
      payload.tier = form.tier ? form.tier : null;
      payload.apiKey = normalizeCredential(form.apiKey);
      payload.apiBaseUrl = normalizeCredential(form.apiBaseUrl);
      if (!payload.allowedPersonas) delete payload.allowedPersonas;
      if (!payload.preferredPersona) delete payload.preferredPersona;
      payload.runtimeOptionsJson = buildMuxRuntimeOptionsJson(form.runtime, form);
      delete payload.muxConfigDirectory;
      delete payload.muxEndpoint;
      delete payload.muxBaseUrl;
      delete payload.muxAdapterType;
      delete payload.muxTemperature;
      delete payload.muxMaxTokens;
      delete payload.muxSystemPromptPath;
      delete payload.muxApprovalPolicy;
      await updateCaptain(captain.id, payload);
      setShowForm(false);
      pushToast('success', t('Captain "{{name}}" saved.', { name: form.name }));
      load();
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : t('Save failed.'));
    } finally {
      setSaving(false);
    }
  }

  // Readable mode asks the server for typed entries; raw mode keeps the redacted text view.
  async function handleViewLog(readable = logReadable) {
    if (!id) return;
    setLogLoading(true);
    setShowLog(true);
    try {
      const result: LogResult = await getCaptainLog(id, 500, readable);
      setLogText(result.log || t('(empty log)'));
      setLogEntries(readable && result.entries ? result.entries : null);
      setLogEntriesTruncated(readable && result.entriesTruncated === true);
      setLogInfo(t('({{lines}} of {{totalLines}} lines)', { lines: result.lines || 0, totalLines: result.totalLines || 0 }));
    } catch {
      setLogText(t('Failed to load log.'));
      setLogEntries(null);
      setLogEntriesTruncated(false);
      setLogInfo('');
    } finally {
      setLogLoading(false);
    }
  }

  function handleToggleLogMode() {
    const nextReadable = !logReadable;
    setLogReadable(nextReadable);
    void handleViewLog(nextReadable);
  }

  function handleStop() {
    if (!captain) return;
    setConfirm({
      open: true,
      title: t('Stop Captain'),
      message: t('Stop captain "{{name}}"? This will halt the current mission.', { name: captain.name }),
      onConfirm: async () => {
        setConfirm(c => ({ ...c, open: false }));
        try {
          await stopCaptain(captain.id);
          pushToast('warning', t('Captain "{{name}}" stopped.', { name: captain.name }));
          load();
        } catch { setError(t('Failed to stop captain.')); }
      },
    });
  }

  async function handleUnquarantine() {
    if (!captain) return;
    try {
      const result = await unquarantineCaptain(captain.id);
      if (result.outcome === 'Released') pushToast('success', t('Quarantine lifted for "{{name}}".', { name: captain.name }));
      else pushToast('warning', t('Captain "{{name}}" was not quarantined; nothing changed.', { name: captain.name }));
      load();
    } catch (e) { setError(e instanceof Error ? e.message : t('Failed to lift quarantine.')); }
  }

  async function handleQuarantineSubmit(request: CaptainQuarantineRequest) {
    if (!captain) return;
    setQuarantining(true);
    try {
      await quarantineCaptain(captain.id, request);
      pushToast('warning', t('Captain "{{name}}" quarantined.', { name: captain.name }));
      setQuarantineOpen(false);
      load();
    } catch (e) {
      setError(e instanceof Error ? e.message : t('Quarantine failed.'));
    } finally {
      setQuarantining(false);
    }
  }

  function handleRemove() {
    if (!captain) return;
    setConfirm({
      open: true,
      title: t('Remove Captain'),
      message: t('Remove captain "{{name}}"? This cannot be undone.', { name: captain.name }),
      onConfirm: async () => {
        setConfirm(c => ({ ...c, open: false }));
        try {
          await deleteCaptain(captain.id);
          pushToast('warning', t('Captain "{{name}}" removed.', { name: captain.name }));
          navigate('/captains');
        } catch { setError(t('Remove failed.')); }
      },
    });
  }

  async function handleDuplicate() {
    if (!captain) return;
    try {
      const created = await createCaptain(buildCaptainDuplicatePayload(captain));
      pushToast('success', t('Captain "{{name}}" duplicated.', { name: created.name }));
      navigate(`/captains/${created.id}`);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Duplicate failed.'));
    }
  }

  async function handleViewTools() {
    if (!captain) return;

    setToolViewer({
      open: true,
      loading: true,
      error: '',
      data: null,
    });

    try {
      const result = await getCaptainTools(captain.id);
      setToolViewer({
        open: true,
        loading: false,
        error: '',
        data: result,
      });
    } catch {
      setToolViewer({
        open: true,
        loading: false,
        error: t('Failed to load captain tools.'),
        data: null,
      });
    }
  }

  function getActionItems() {
    if (!captain) return [];
    const items: { label: string; danger?: boolean; onClick: () => void }[] = [
      { label: 'Edit', onClick: openEdit },
      { label: 'Duplicate', onClick: () => void handleDuplicate() },
      { label: 'View Tools', onClick: () => void handleViewTools() },
      { label: 'View Log', onClick: handleViewLog },
      { label: 'View JSON', onClick: () => setJsonData({ open: true, title: t('Captain: {{name}}', { name: captain.name }), data: captain }) },
    ];
    if (captain.state === 'Working' || captain.state === 'Stalled') {
      items.push({ label: 'Stop Captain', danger: true, onClick: handleStop });
    }
    if (captain.state === 'Planning') {
      items.push({ label: 'Stop Captain', danger: true, onClick: handleStop });
    }
    items.push({ label: 'Remove', danger: true, onClick: handleRemove });
    return items;
  }

  if (loading) return <p className="text-dim">{t('Loading...')}</p>;
  if (error && !captain) return <ErrorModal error={error} onClose={() => setError('')} />;
  if (notFound || !captain) return <p className="text-dim">{t('Captain not found.')}</p>;

  const muxOptions = parseMuxCaptainOptions(captain.runtimeOptionsJson);

  return (
    <div>
      <PageHeader
        breadcrumb={
          <>
            <Link to="/captains">{t('Captains')}</Link> <span className="breadcrumb-sep">&gt;</span> <span>{captain.name}</span>
          </>
        }
        title={captain.name}
        actions={
          <>
            <AutoRefreshSelect seconds={refreshSeconds} onChange={setRefreshSeconds} />
            <ActionMenu id={`captain-${captain.id}`} items={getActionItems()} />
          </>
        }
      />

      <ErrorModal error={error} onClose={() => setError('')} />

      {/* Edit Modal */}
      {showForm && (
        <div className="modal-overlay" onClick={() => setShowForm(false)}>
          <form className="modal" onClick={e => e.stopPropagation()} onSubmit={handleSubmit}>
            <h3>{t('Edit Captain')}</h3>
            <label>{t('Name')}<input value={form.name} onChange={e => setForm({ ...form, name: e.target.value })} required /></label>
            <label>{t('Runtime')}
              <select value={form.runtime} onChange={e => setForm({ ...form, runtime: e.target.value })} required>
                {RUNTIMES.map(r => <option key={r} value={r}>{r}</option>)}
              </select>
            </label>
            <label title={t('Optional instructions injected into every mission prompt for this captain. Use this to specialize behavior, add guardrails, or provide persistent context.')}>
              {t('System Instructions')}
              <textarea value={form.systemInstructions} onChange={e => setForm({ ...form, systemInstructions: e.target.value })} rows={4} placeholder={t('e.g., You are a testing specialist. Always run tests before committing...')} />
            </label>
            <label title={t('Optional AI model identifier. Leave blank to let the runtime choose its default model.')}>
              {t('Model')}
              <input value={form.model} onChange={e => setForm({ ...form, model: e.target.value })} placeholder={form.runtime === 'ApiEndpoint' ? t('Optional; overrides the endpoint model') : t('e.g., gpt-5.4-mini')} />
            </label>
            {form.runtime === 'ApiEndpoint' && (
              <label title={t('The configured inference endpoint this captain drives. Manage endpoints under Configuration > Endpoints.')}>
                {t('Inference Endpoint')}
                <select value={form.modelEndpointId} onChange={e => setForm({ ...form, modelEndpointId: e.target.value })} required>
                  <option value="">{t('Select an inference endpoint...')}</option>
                  {inferenceEndpoints.map(ep => (
                    <option key={ep.id} value={ep.id}>{ep.name} ({ep.provider}{ep.model ? ' / ' + ep.model : ''})</option>
                  ))}
                </select>
                {inferenceEndpoints.length === 0 && (
                  <small className="text-dim" style={{ display: 'block', marginTop: '0.25rem' }}>
                    {t('No inference endpoints configured. Add one under Configuration > Endpoints first.')}
                  </small>
                )}
              </label>
            )}
            <label>
              {t('Capability tier')}
              <select value={form.tier} onChange={e => setForm({ ...form, tier: e.target.value })}>
                <option value="">{t('Auto (classify from model)')}</option>
                <option value="Economy">{t('Economy')}</option>
                <option value="Standard">{t('Standard')}</option>
                <option value="Premium">{t('Premium')}</option>
              </select>
              <span className="text-dim" style={{ fontSize: '0.72rem' }}>
                {t('Missions requiring a tier route to captains at or above it. Leave on Auto to classify from the model name.')}
              </span>
            </label>
            <ProviderCredentialFields
              form={form}
              onChange={(patch) => setForm((current) => ({ ...current, ...patch }))}
              t={t}
            />
            <MuxRuntimeFields
              runtime={form.runtime}
              form={form}
              onChange={(patch) => setForm((current) => ({ ...current, ...patch }))}
              t={t}
            />
            <label>
              {t('Allowed Personas (JSON array)')}
              <textarea value={form.allowedPersonas} onChange={e => setForm({ ...form, allowedPersonas: e.target.value })} rows={2} placeholder={t('["Worker", "Judge"]')} />
            </label>
            <label>
              {t('Preferred Persona')}
              <input value={form.preferredPersona} onChange={e => setForm({ ...form, preferredPersona: e.target.value })} placeholder={t('e.g., Worker')} />
            </label>
            <div className="modal-actions">
              <button type="submit" className="btn btn-primary" disabled={saving}>{saving ? t('Saving...') : t('Save')}</button>
              <button type="button" className="btn" onClick={() => setShowForm(false)} disabled={saving}>{t('Cancel')}</button>
            </div>
          </form>
        </div>
      )}

      <JsonViewer open={jsonData.open} title={jsonData.title} data={jsonData.data} onClose={() => setJsonData({ open: false, title: '', data: null })} />
      <CaptainToolViewer
        open={toolViewer.open}
        captainName={captain.name}
        loading={toolViewer.loading}
        error={toolViewer.error}
        data={toolViewer.data}
        onClose={() => setToolViewer({ open: false, loading: false, error: '', data: null })}
      />
      <ConfirmDialog open={confirm.open} title={confirm.title} message={confirm.message}
        onConfirm={confirm.onConfirm} onCancel={() => setConfirm(c => ({ ...c, open: false }))} />
      <CaptainQuarantineDialog
        open={quarantineOpen}
        captainName={captain.name}
        t={t}
        submitting={quarantining}
        onSubmit={request => void handleQuarantineSubmit(request)}
        onCancel={() => setQuarantineOpen(false)}
      />

      {/* Captain Info */}
      <div className="detail-grid">
        <div className="detail-field">
          <span className="detail-label">{t('ID')}</span>
          <span className="id-display">
            <span className="mono">{captain.id}</span>
            <CopyButton text={captain.id} />
          </span>
        </div>
        <div className="detail-field"><span className="detail-label">{t('Name')}</span><span>{captain.name}</span></div>
        <div className="detail-field"><span className="detail-label">{t('Tenant ID')}</span><span className="mono">{captain.tenantId || '-'}</span></div>
        <div className="detail-field"><span className="detail-label">{t('Runtime')}</span><span>{captain.runtime || 'ClaudeCode'}</span></div>
        <div className="detail-field"><span className="detail-label">{t('Capability tier')}</span><span>{captain.tier ? <CaptainTierBadge tier={captain.tier} /> : <span className="text-dim">{t('Auto (classify from model)')}</span>}</span></div>
      </div>
      {isMuxRuntime(captain.runtime) && (
        <div className="detail-grid">
          <div className="detail-field">
            <span className="detail-label">{t('Mux Endpoint')}</span>
            <span>{muxOptions?.endpoint || <span className="text-dim">{t('Not configured')}</span>}</span>
          </div>
          <div className="detail-field">
            <span className="detail-label">{t('Mux Config Directory')}</span>
            <span className="mono">{muxOptions?.configDirectory || <span className="text-dim">{t('Mux default')}</span>}</span>
          </div>
          <div className="detail-field">
            <span className="detail-label">{t('Mux Adapter')}</span>
            <span>{muxOptions?.adapterType || <span className="text-dim">{t('Endpoint default')}</span>}</span>
          </div>
          <div className="detail-field">
            <span className="detail-label">{t('Mux Base URL')}</span>
            <span className="mono">{muxOptions?.baseUrl || <span className="text-dim">{t('Endpoint default')}</span>}</span>
          </div>
        </div>
      )}
      {captain.systemInstructions && (
        <div className="detail-context-section">
          <h4>{t('System Instructions')}</h4>
          <pre className="detail-context-block">{captain.systemInstructions}</pre>
        </div>
      )}
      <div className="detail-grid">
        <div className="detail-field">
          <span className="detail-label">{t('Allowed Personas')}</span>
          <span>{captain.allowedPersonas || <span className="text-dim">{t('Any (no restriction)')}</span>}</span>
        </div>
        <div className="detail-field">
          <span className="detail-label">{t('Model')}</span>
          <span>{captain.model || <span className="text-dim">{t('Runtime default')}</span>}</span>
        </div>
        <div className="detail-field">
          <span className="detail-label">{t('Preferred Persona')}</span>
          <span>{captain.preferredPersona || <span className="text-dim">{t('None')}</span>}</span>
        </div>
        <div className="detail-field">
          <span className="detail-label">{t('State')}</span>
          <span style={{ display: 'flex', alignItems: 'center', gap: '0.6rem', flexWrap: 'wrap' }}>
            <StatusBadge status={captain.state} />
            {captain.state !== 'Quarantined' && (
              <button type="button" className="btn btn-sm" onClick={() => setQuarantineOpen(true)}>{t('Quarantine')}</button>
            )}
          </span>
        </div>
        {captain.state === 'Quarantined' && (
          <div className="detail-field">
            <span className="detail-label">{t('Quarantine')}</span>
            <span style={{ display: 'flex', alignItems: 'center', gap: '0.6rem', flexWrap: 'wrap' }}>
              <span className="text-dim">
                {captain.quarantineReason || t('quarantined')}
                {captain.quarantineUntilUtc ? ` (${t('until')} ${formatDateTime(captain.quarantineUntilUtc)})` : ''}
              </span>
              <button type="button" className="btn btn-sm" onClick={handleUnquarantine}>{t('Lift Quarantine')}</button>
            </span>
          </div>
        )}
        <div className="detail-field">
          <span className="detail-label">{t('Current Mission')}</span>
          {captain.currentMissionId ? (
            <span className="id-display">
              <Link className="mono" to={`/missions/${captain.currentMissionId}`}>{captain.currentMissionId}</Link>
              <CopyButton text={captain.currentMissionId!} />
            </span>
          ) : <span>-</span>}
        </div>
        <div className="detail-field">
          <span className="detail-label">{t('Current Dock')}</span>
          {captain.currentDockId ? (
            <span className="id-display">
              <Link className="mono" to={`/docks/${captain.currentDockId}`}>{captain.currentDockId}</Link>
              <CopyButton text={captain.currentDockId!} />
            </span>
          ) : <span>-</span>}
        </div>
        <div className="detail-field"><span className="detail-label">{t('Process ID')}</span><span>{captain.processId || '-'}</span></div>
        <div className="detail-field"><span className="detail-label">{t('Recovery Attempts')}</span><span>{captain.recoveryAttempts ?? 0}</span></div>
        <div className="detail-field">
          <span className="detail-label">{t('Last Heartbeat')}</span>
          <span title={formatDateTime(captain.lastHeartbeatUtc)}>
            {formatRelativeTime(captain.lastHeartbeatUtc) || '-'}
            {captain.lastHeartbeatUtc && <span className="text-dim"> ({formatDateTime(captain.lastHeartbeatUtc)})</span>}
          </span>
        </div>
        <div className="detail-field">
          <span className="detail-label">{t('Created')}</span>
          <span title={captain.createdUtc}>
            {formatRelativeTime(captain.createdUtc)}
            <span className="text-dim"> ({formatDateTime(captain.createdUtc)})</span>
          </span>
        </div>
        <div className="detail-field">
          <span className="detail-label">{t('Last Updated')}</span>
          <span title={captain.lastUpdateUtc}>
            {formatRelativeTime(captain.lastUpdateUtc)}
            <span className="text-dim"> ({formatDateTime(captain.lastUpdateUtc)})</span>
          </span>
        </div>
      </div>

      {/* Current Mission Details */}
      {currentMission && (
        <div style={{ marginTop: '1rem' }}>
          <h3>{t('Current Mission')}</h3>
          <div className="card" style={{ padding: '1rem' }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 8 }}>
              <strong>{currentMission.title}</strong>
              <StatusBadge status={currentMission.status} />
            </div>
            {currentMission.description && <p className="text-dim" style={{ marginBottom: 8 }}>{currentMission.description}</p>}
            <div style={{ display: 'flex', gap: 16, fontSize: 13 }}>
              <span>{t('Branch')}: <span className="mono">{currentMission.branchName || '-'}</span></span>
              <span>{t('Priority')}: {currentMission.priority}</span>
            </div>
            <div style={{ marginTop: 8 }}>
              <Link to={`/missions/${currentMission.id}`} className="btn btn-sm">{t('View Mission')}</Link>
            </div>
          </div>
        </div>
      )}

      {/* Log Viewer */}
      {showLog && (
        <div style={{ marginTop: '1rem' }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
            <h3>{t('Captain Log')} {logInfo && <span className="text-dim">{logInfo}</span>}</h3>
            <div style={{ display: 'flex', gap: '0.5rem' }}>
              <button className="btn" onClick={handleToggleLogMode} aria-pressed={logReadable}>
                {logReadable ? t('Show Raw') : t('Show Readable')}
              </button>
              <button className="btn" onClick={() => setShowLog(false)}>{t('Hide Log')}</button>
            </div>
          </div>
          {logLoading ? (
            <p className="text-dim">{t('Loading log...')}</p>
          ) : logReadable && logEntries ? (
            <RuntimeLogEntries entries={logEntries} entriesTruncated={logEntriesTruncated} />
          ) : (
            <pre style={{
              background: '#1a1a2e',
              color: '#e0e0e0',
              padding: 16,
              borderRadius: 'var(--radius)',
              overflow: 'auto',
              fontSize: 12,
              fontFamily: 'var(--mono)',
              maxHeight: '60vh',
              whiteSpace: 'pre-wrap',
              wordBreak: 'break-word',
            }}>
              {logText}
            </pre>
          )}
        </div>
      )}

      {/* Recent Missions */}
      <div style={{ marginTop: '1rem' }}>
        <h3>{t('Recent Missions')}</h3>
        {missions.length > 0 ? (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th title={t('Mission name and unique identifier')}>{t('Mission')}</th>
                  <th title={t('Current mission lifecycle state')}>{t('Status')}</th>
                  <th title={t('Git branch for this mission\'s work')}>{t('Branch')}</th>
                  <th title={t('When the mission was completed or created')}>{t('Date')}</th>
                </tr>
              </thead>
              <tbody>
                {missions.map(m => (
                  <tr key={m.id} className="clickable" onClick={() => navigate(`/missions/${m.id}`)}>
                    <td>
                      <strong>{m.title}</strong>
                      <div className="text-dim id-display">
                        <span className="mono">{m.id}</span>
                        <CopyButton text={m.id} />
                      </div>
                    </td>
                    <td><StatusBadge status={m.status} /></td>
                    <td className="text-dim">{m.branchName || '-'}</td>
                    <td className="text-dim" title={formatDateTime(m.completedUtc || m.createdUtc)}>
                      {formatRelativeTime(m.completedUtc || m.createdUtc)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <p className="text-dim" style={{ marginTop: '0.5rem' }}>{t('No missions yet')}</p>
        )}
      </div>
    </div>
  );
}
