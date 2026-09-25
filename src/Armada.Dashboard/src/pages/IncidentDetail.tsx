import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link, useLocation, useNavigate, useParams } from 'react-router-dom';
import {
  createIncident,
  deleteIncident,
  getIncident,
  getMissionRecovery,
  rollbackDeployment,
  updateIncident,
  listAllDeployments,
  listAllEnvironments,
  listAllReleases,
  listAllRunbookExecutions,
  listAllVessels,
} from '../api/client';
import type {
  Deployment,
  DeploymentEnvironment,
  Incident,
  IncidentSeverity,
  IncidentStatus,
  IncidentUpsertRequest,
  MissionRecoveryReport,
  Release,
  RunbookExecution,
  Vessel,
} from '../types/models';
import { useAuth } from '../context/AuthContext';
import { useLocale } from '../context/LocaleContext';
import { useLatestRequest } from '../lib/useLatestRequest';
import { useNotifications } from '../context/NotificationContext';
import { RESYNC_MESSAGE_TYPE, useWebSocket } from '../context/WebSocketContext';
import ConfirmDialog from '../components/shared/ConfirmDialog';
import CopyButton from '../components/shared/CopyButton';
import IncidentCloseDialog from '../components/incidents/IncidentCloseDialog';
import MissionRecoveryPanel from '../components/shared/MissionRecoveryPanel';
import ErrorModal from '../components/shared/ErrorModal';
import JsonViewer from '../components/shared/JsonViewer';
import PageHeader from '../components/shared/PageHeader';
import StatusBadge from '../components/shared/StatusBadge';

const INCIDENT_STATUSES: IncidentStatus[] = ['Open', 'Monitoring', 'Mitigated', 'RolledBack', 'Closed'];
const INCIDENT_SEVERITIES: IncidentSeverity[] = ['Critical', 'High', 'Medium', 'Low'];

interface IncidentPrefillState {
  prefill?: IncidentUpsertRequest;
}

function toInputDateTime(value: string | null | undefined) {
  if (!value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  const pad = (input: number) => String(input).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

function toUtcValue(value: string) {
  if (!value.trim()) return null;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date.toISOString();
}

/** The editable fields of the form, as strings, so the form can be compared with the stored record. */
interface IncidentFormState {
  vesselId: string;
  environmentId: string;
  environmentName: string;
  deploymentId: string;
  releaseId: string;
  missionId: string;
  voyageId: string;
  rollbackDeploymentId: string;
  title: string;
  summary: string;
  status: IncidentStatus;
  severity: IncidentSeverity;
  impact: string;
  rootCause: string;
  recoveryNotes: string;
  postmortem: string;
  detectedUtc: string;
  mitigatedUtc: string;
  closedUtc: string;
}

function formStateFromIncident(result: Incident): IncidentFormState {
  return {
    vesselId: result.vesselId || '',
    environmentId: result.environmentId || '',
    environmentName: result.environmentName || '',
    deploymentId: result.deploymentId || '',
    releaseId: result.releaseId || '',
    missionId: result.missionId || '',
    voyageId: result.voyageId || '',
    rollbackDeploymentId: result.rollbackDeploymentId || '',
    title: result.title,
    summary: result.summary || '',
    status: result.status,
    severity: result.severity,
    impact: result.impact || '',
    rootCause: result.rootCause || '',
    recoveryNotes: result.recoveryNotes || '',
    postmortem: result.postmortem || '',
    detectedUtc: toInputDateTime(result.detectedUtc),
    mitigatedUtc: toInputDateTime(result.mitigatedUtc),
    closedUtc: toInputDateTime(result.closedUtc),
  };
}

function buildIncidentPrompt(incidentTitle: string, summary: string, impact: string, environmentName: string, deploymentId: string, releaseId: string) {
  const lines: string[] = [
    `Investigate and mitigate the incident "${incidentTitle}".`,
    '',
  ];

  if (summary) {
    lines.push(`Summary: ${summary}`);
  }
  if (impact) {
    lines.push(`Impact: ${impact}`);
  }
  if (environmentName) {
    lines.push(`Environment: ${environmentName}`);
  }
  if (deploymentId) {
    lines.push(`Deployment: ${deploymentId}`);
  }
  if (releaseId) {
    lines.push(`Release: ${releaseId}`);
  }

  lines.push('');
  lines.push('Produce a concrete hotfix plan and, if appropriate, implementation scope for the linked vessel.');
  return lines.join('\n');
}

export default function IncidentDetail() {
  const { id } = useParams<{ id: string }>();
  const location = useLocation();
  const navigate = useNavigate();
  const { isAdmin, isTenantAdmin } = useAuth();
  const { t, formatDateTime, formatRelativeTime } = useLocale();
  const { pushToast } = useNotifications();

  const createMode = id === 'new';
  const canManage = isAdmin || isTenantAdmin;

  const [incident, setIncident] = useState<Incident | null>(null);
  const [vessels, setVessels] = useState<Vessel[]>([]);
  const [environments, setEnvironments] = useState<DeploymentEnvironment[]>([]);
  const [deployments, setDeployments] = useState<Deployment[]>([]);
  const [releases, setReleases] = useState<Release[]>([]);
  const [executions, setExecutions] = useState<RunbookExecution[]>([]);
  const [vesselId, setVesselId] = useState('');
  const [environmentId, setEnvironmentId] = useState('');
  const [environmentName, setEnvironmentName] = useState('');
  const [deploymentId, setDeploymentId] = useState('');
  const [releaseId, setReleaseId] = useState('');
  const [missionId, setMissionId] = useState('');
  const [voyageId, setVoyageId] = useState('');
  const [rollbackDeploymentId, setRollbackDeploymentId] = useState('');
  const [title, setTitle] = useState('Incident');
  const [summary, setSummary] = useState('');
  const [status, setStatus] = useState<IncidentStatus>('Open');
  const [severity, setSeverity] = useState<IncidentSeverity>('High');
  const [impact, setImpact] = useState('');
  const [rootCause, setRootCause] = useState('');
  const [recoveryNotes, setRecoveryNotes] = useState('');
  const [postmortem, setPostmortem] = useState('');
  const [detectedUtc, setDetectedUtc] = useState('');
  const [mitigatedUtc, setMitigatedUtc] = useState('');
  const [closedUtc, setClosedUtc] = useState('');
  const [loading, setLoading] = useState(!createMode);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [jsonData, setJsonData] = useState<{ open: boolean; title: string; data: unknown }>({ open: false, title: '', data: null });
  const [closeDialog, setCloseDialog] = useState<{ open: boolean; submitting: boolean; refusal: string | null }>({
    open: false,
    submitting: false,
    refusal: null,
  });
  const [confirm, setConfirm] = useState<{ open: boolean; title: string; message: string; onConfirm: () => void }>({
    open: false,
    title: '',
    message: '',
    onConfirm: () => {},
  });

  useEffect(() => {
    let cancelled = false;

    Promise.all([
      listAllVessels(),
      listAllEnvironments(),
      listAllDeployments(),
      listAllReleases(),
    ]).then(([vesselResult, environmentResult, deploymentResult, releaseResult]) => {
      if (cancelled) return;
      setVessels(vesselResult);
      setEnvironments(environmentResult);
      setDeployments(deploymentResult);
      setReleases(releaseResult);
    }).catch((err: unknown) => {
      if (!cancelled) setError(err instanceof Error ? err.message : t('Failed to load incident reference data.'));
    });

    return () => { cancelled = true; };
  }, [t]);

  useEffect(() => {
    if (!createMode || !location.state) return;
    const state = location.state as IncidentPrefillState;
    if (!state.prefill) return;

    setVesselId(state.prefill.vesselId || '');
    setEnvironmentId(state.prefill.environmentId || '');
    setEnvironmentName(state.prefill.environmentName || '');
    setDeploymentId(state.prefill.deploymentId || '');
    setReleaseId(state.prefill.releaseId || '');
    setMissionId(state.prefill.missionId || '');
    setVoyageId(state.prefill.voyageId || '');
    setRollbackDeploymentId(state.prefill.rollbackDeploymentId || '');
    setTitle(state.prefill.title || 'Incident');
    setSummary(state.prefill.summary || '');
    setStatus(state.prefill.status || 'Open');
    setSeverity(state.prefill.severity || 'High');
    setImpact(state.prefill.impact || '');
    setRootCause(state.prefill.rootCause || '');
    setRecoveryNotes(state.prefill.recoveryNotes || '');
    setPostmortem(state.prefill.postmortem || '');
    setDetectedUtc(toInputDateTime(state.prefill.detectedUtc || null));
    setMitigatedUtc(toInputDateTime(state.prefill.mitigatedUtc || null));
    setClosedUtc(toInputDateTime(state.prefill.closedUtc || null));
  }, [createMode, location.state]);

  // Recovery evidence comes from the named mission's recovery report, read in the caller's scope.
  const [recoveryReport, setRecoveryReport] = useState<MissionRecoveryReport | null>(null);
  const [recoveryError, setRecoveryError] = useState<string | null>(null);
  const [recoveryLoading, setRecoveryLoading] = useState(false);
  const recoveryMissionId = incident?.missionId || null;

  useEffect(() => {
    if (!recoveryMissionId) {
      setRecoveryReport(null);
      setRecoveryError(null);
      setRecoveryLoading(false);
      return;
    }
    let active = true;
    setRecoveryLoading(true);
    setRecoveryError(null);
    getMissionRecovery(recoveryMissionId)
      .then((result) => { if (active) setRecoveryReport(result); })
      .catch((err: unknown) => {
        if (!active) return;
        setRecoveryReport(null);
        setRecoveryError(t('Recovery report unavailable: {{message}}', { message: err instanceof Error ? err.message : String(err) }));
      })
      .finally(() => { if (active) setRecoveryLoading(false); });
    return () => { active = false; };
  }, [recoveryMissionId, t]);

  const applyIncident = useCallback((result: Incident) => {
    const form = formStateFromIncident(result);
    setIncident(result);
    setVesselId(form.vesselId);
    setEnvironmentId(form.environmentId);
    setEnvironmentName(form.environmentName);
    setDeploymentId(form.deploymentId);
    setReleaseId(form.releaseId);
    setMissionId(form.missionId);
    setVoyageId(form.voyageId);
    setRollbackDeploymentId(form.rollbackDeploymentId);
    setTitle(form.title);
    setSummary(form.summary);
    setStatus(form.status);
    setSeverity(form.severity);
    setImpact(form.impact);
    setRootCause(form.rootCause);
    setRecoveryNotes(form.recoveryNotes);
    setPostmortem(form.postmortem);
    setDetectedUtc(form.detectedUtc);
    setMitigatedUtc(form.mitigatedUtc);
    setClosedUtc(form.closedUtc);
    setServerChange(null);
  }, []);

  // A newer copy of the incident that arrived while the form held unsaved edits.
  const [serverChange, setServerChange] = useState<Incident | null>(null);

  const currentForm: IncidentFormState = {
    vesselId, environmentId, environmentName, deploymentId, releaseId, missionId, voyageId, rollbackDeploymentId,
    title, summary, status, severity, impact, rootCause, recoveryNotes, postmortem, detectedUtc, mitigatedUtc, closedUtc,
  };
  const dirty = !createMode && incident !== null
    && JSON.stringify(currentForm) !== JSON.stringify(formStateFromIncident(incident));
  const dirtyRef = useRef(dirty);
  dirtyRef.current = dirty;

  // A read superseded by a later one (another id or a reload after missed events) writes nothing.
  const requests = useLatestRequest();
  const loadIncident = useCallback(async (showLoading: boolean) => {
    if (createMode || !id) return;
    const request = requests.begin(id);
    try {
      if (showLoading) setLoading(true);
      const result = await getIncident(id);
      if (!request.isCurrent()) return;
      if (dirtyRef.current) setServerChange(result);
      else applyIncident(result);
      setError('');
    } catch (err: unknown) {
      if (request.isCurrent()) setError(err instanceof Error ? err.message : t('Failed to load incident.'));
    } finally {
      // The newest load ends the loading state, even when an older load that showed it was superseded.
      if (request.isCurrent()) setLoading(false);
    }
  }, [requests, applyIncident, createMode, id, t]);

  useEffect(() => {
    void loadIncident(true);
  }, [loadIncident]);

  // Live updates: another session or the server may change this incident while it is open. A change
  // replaces the form only when it holds no unsaved edits; otherwise the page says so and waits.
  const { subscribe } = useWebSocket();
  useEffect(() => {
    if (createMode || !id) return undefined;
    return subscribe((msg) => {
      if (msg.type === RESYNC_MESSAGE_TYPE) {
        void loadIncident(false);
        return;
      }
      if (msg.type !== 'incident.changed') return;
      const data = msg.data as Incident | undefined;
      if (!data || data.id !== id) return;
      if (dirtyRef.current) setServerChange(data);
      else applyIncident(data);
    });
  }, [applyIncident, createMode, id, loadIncident, subscribe]);

  useEffect(() => {
    if (createMode || !id) {
      setExecutions([]);
      return;
    }

    let cancelled = false;
    listAllRunbookExecutions({ incidentId: id }).then((result) => {
      if (!cancelled) setExecutions(result);
    }).catch(() => {
      if (!cancelled) setExecutions([]);
    });

    return () => { cancelled = true; };
  }, [createMode, id]);

  const selectedVessel = useMemo(() => vessels.find((candidate) => candidate.id === vesselId) || null, [vesselId, vessels]);
  const selectedEnvironment = useMemo(() => environments.find((candidate) => candidate.id === environmentId) || null, [environmentId, environments]);
  const selectedDeployment = useMemo(() => deployments.find((candidate) => candidate.id === deploymentId) || null, [deploymentId, deployments]);

  useEffect(() => {
    if (!environmentId) return;
    const selected = environments.find((candidate) => candidate.id === environmentId);
    if (selected) {
      setEnvironmentName(selected.name);
      if (!vesselId && selected.vesselId) setVesselId(selected.vesselId);
    }
  }, [environmentId, environments, vesselId]);

  useEffect(() => {
    if (!deploymentId) return;
    const selected = deployments.find((candidate) => candidate.id === deploymentId);
    if (selected) {
      if (!environmentId && selected.environmentId) setEnvironmentId(selected.environmentId);
      if (!environmentName && selected.environmentName) setEnvironmentName(selected.environmentName);
      if (!vesselId && selected.vesselId) setVesselId(selected.vesselId);
      if (!releaseId && selected.releaseId) setReleaseId(selected.releaseId);
      if (!missionId && selected.missionId) setMissionId(selected.missionId);
      if (!voyageId && selected.voyageId) setVoyageId(selected.voyageId);
    }
  }, [deploymentId, deployments, environmentId, environmentName, missionId, releaseId, vesselId, voyageId]);

  function buildPayload(): IncidentUpsertRequest {
    return {
      title: title.trim() || null,
      summary: summary.trim() || null,
      status,
      severity,
      vesselId: vesselId || null,
      environmentId: environmentId || null,
      environmentName: environmentName.trim() || null,
      deploymentId: deploymentId.trim() || null,
      releaseId: releaseId.trim() || null,
      missionId: missionId.trim() || null,
      voyageId: voyageId.trim() || null,
      rollbackDeploymentId: rollbackDeploymentId.trim() || null,
      impact: impact.trim() || null,
      rootCause: rootCause.trim() || null,
      recoveryNotes: recoveryNotes.trim() || null,
      postmortem: postmortem.trim() || null,
      detectedUtc: toUtcValue(detectedUtc),
      mitigatedUtc: toUtcValue(mitigatedUtc),
      closedUtc: toUtcValue(closedUtc),
    };
  }

  async function handleSave() {
    if (!canManage) return;
    try {
      setSaving(true);
      const payload = buildPayload();
      if (createMode) {
        const created = await createIncident(payload);
        pushToast('success', t('Incident "{{title}}" created.', { title: created.title }));
        navigate(`/incidents/${created.id}`);
        return;
      }

      if (!id) return;
      const updated = await updateIncident(id, payload);
      applyIncident(updated);
      pushToast('success', t('Incident "{{title}}" saved.', { title: updated.title }));
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Save failed.'));
    } finally {
      setSaving(false);
    }
  }

  async function handleCloseIncident(writtenRootCause: string) {
    if (!incident || !canManage) return;
    setCloseDialog((current) => ({ ...current, submitting: true, refusal: null }));
    try {
      const updated = await updateIncident(incident.id, { status: 'Closed', rootCause: writtenRootCause });
      applyIncident(updated);
      setCloseDialog({ open: false, submitting: false, refusal: null });
      pushToast('success', t('Incident "{{title}}" closed.', { title: updated.title }));
    } catch (err: unknown) {
      setCloseDialog((current) => ({
        ...current,
        submitting: false,
        refusal: err instanceof Error ? err.message : t('Close failed.'),
      }));
    }
  }

  function handleDelete() {
    if (!incident || !canManage) return;
    setConfirm({
      open: true,
      title: t('Delete Incident'),
      message: t('Delete "{{title}}"? This removes only the incident record.', { title: incident.title }),
      onConfirm: async () => {
        setConfirm((current) => ({ ...current, open: false }));
        try {
          await deleteIncident(incident.id);
          pushToast('warning', t('Incident "{{title}}" deleted.', { title: incident.title }));
          navigate('/incidents');
        } catch (err: unknown) {
          setError(err instanceof Error ? err.message : t('Delete failed.'));
        }
      },
    });
  }

  function handlePlanHotfix() {
    const prompt = buildIncidentPrompt(title, summary, impact, environmentName, deploymentId, releaseId);
    navigate('/planning', {
      state: {
        fromIncident: true,
        vesselId: vesselId || selectedEnvironment?.vesselId || null,
        title: `Hotfix: ${title}`,
        initialPrompt: prompt,
      },
    });
  }

  function handleDispatchHotfix() {
    const prompt = buildIncidentPrompt(title, summary, impact, environmentName, deploymentId, releaseId);
    navigate('/dispatch', {
      state: {
        fromIncident: true,
        vesselId: vesselId || selectedEnvironment?.vesselId || null,
        voyageTitle: `Hotfix: ${title}`,
        prompt,
      },
    });
  }

  function handleRollback() {
    if (!incident?.deploymentId) return;
    setConfirm({
      open: true,
      title: t('Rollback Deployment'),
      message: t('Rollback deployment "{{deploymentId}}" and attach the result to this incident?', { deploymentId: incident.deploymentId }),
      onConfirm: async () => {
        setConfirm((current) => ({ ...current, open: false }));
        try {
          await rollbackDeployment(incident.deploymentId!);
          const updated = await updateIncident(incident.id, {
            rollbackDeploymentId: incident.deploymentId,
            status: 'RolledBack',
          });
          setIncident(updated);
          setRollbackDeploymentId(updated.rollbackDeploymentId || '');
          setStatus(updated.status);
          pushToast('warning', t('Rollback started for "{{deploymentId}}".', { deploymentId: incident.deploymentId! }));
        } catch (err: unknown) {
          setError(err instanceof Error ? err.message : t('Rollback failed.'));
        }
      },
    });
  }

  function handleRunbook() {
    navigate('/runbooks', {
      state: {
        prefillExecution: {
          title: `${title} Response`,
          workflowProfileId: selectedDeployment?.workflowProfileId || null,
          environmentId: environmentId || selectedDeployment?.environmentId || null,
          environmentName: environmentName || selectedDeployment?.environmentName || null,
          deploymentId: deploymentId || null,
          incidentId: incident?.id || null,
          checkType: deploymentId ? 'DeploymentVerification' : 'Custom',
          notes: buildIncidentPrompt(title, summary, impact, environmentName, deploymentId, releaseId),
        },
      },
    });
  }

  if (loading) return <p className="text-dim">{t('Loading...')}</p>;

  return (
    <div>
      <PageHeader
        breadcrumb={
          <>
            <Link to="/incidents">{t('Incidents')}</Link> <span className="breadcrumb-sep">&gt;</span> <span>{createMode ? t('New Incident') : title}</span>
          </>
        }
        title={createMode ? t('Create Incident') : title}
        actions={
          <>
            {!createMode && <StatusBadge status={status} />}
            {!createMode && <StatusBadge status={severity} />}
            {!!(vesselId || selectedEnvironment?.vesselId) && (
              <>
                <button className="btn btn-sm" onClick={handlePlanHotfix}>{t('Plan Hotfix')}</button>
                <button className="btn btn-sm" onClick={handleDispatchHotfix}>{t('Dispatch Hotfix')}</button>
              </>
            )}
            {!createMode && (
              <button className="btn btn-sm" onClick={handleRunbook}>{t('Runbook')}</button>
            )}
            {!createMode && incident?.deploymentId && (
              <button className="btn btn-sm" onClick={handleRollback}>{t('Rollback Deployment')}</button>
            )}
            {!createMode && incident?.deploymentId && (
              <button className="btn btn-sm" onClick={() => navigate(`/deployments/${incident.deploymentId}`)}>{t('Open Deployment')}</button>
            )}
            {!createMode && incident?.environmentId && (
              <button className="btn btn-sm" onClick={() => navigate(`/environments/${incident.environmentId}`)}>{t('Open Environment')}</button>
            )}
            {!createMode && incident && (
              <button className="btn btn-sm" onClick={() => setJsonData({ open: true, title, data: incident })}>{t('View JSON')}</button>
            )}
            {!createMode && canManage && incident && incident.status !== 'Closed' && (
              <button className="btn btn-sm" onClick={() => setCloseDialog({ open: true, submitting: false, refusal: null })}>{t('Close Incident')}</button>
            )}
            {!createMode && canManage && (
              <button className="btn btn-sm btn-danger" onClick={handleDelete}>{t('Delete')}</button>
            )}
          </>
        }
      />

      <ErrorModal error={error} onClose={() => setError('')} />
      <JsonViewer open={jsonData.open} title={jsonData.title} data={jsonData.data} onClose={() => setJsonData({ open: false, title: '', data: null })} />
      <IncidentCloseDialog
        open={closeDialog.open}
        openedReason={incident?.openedReason ?? null}
        writtenRootCause={incident?.rootCauseWrittenBy ? incident.rootCause : null}
        t={t}
        submitting={closeDialog.submitting}
        refusal={closeDialog.refusal}
        onSubmit={(value) => { void handleCloseIncident(value); }}
        onCancel={() => setCloseDialog({ open: false, submitting: false, refusal: null })}
      />
      <ConfirmDialog
        open={confirm.open}
        title={confirm.title}
        message={confirm.message}
        onConfirm={confirm.onConfirm}
        onCancel={() => setConfirm((current) => ({ ...current, open: false }))}
      />

      {serverChange && (
        <div className="alert alert-warning" role="status" style={{ marginBottom: '1rem' }}>
          {t('This incident changed on the server while you were editing. Your edits are kept.')}{' '}
          <button className="btn btn-sm" onClick={() => applyIncident(serverChange)}>{t('Discard my edits and load the change')}</button>
        </div>
      )}

      <div className="detail-grid">
        <section className="card detail-panel">
          <h3>{t('Incident')}</h3>
          <div className="detail-form-grid">
            <div className="detail-field detail-field-full">
              <span className="detail-label">{t('Title')}</span>
              <input value={title} onChange={(event) => setTitle(event.target.value)} disabled={!canManage} />
            </div>

            <div className="detail-field">
              <span className="detail-label">{t('Status')}</span>
              <select value={status} onChange={(event) => setStatus(event.target.value as IncidentStatus)} disabled={!canManage}>
                {INCIDENT_STATUSES.map((value) => (
                  <option key={value} value={value}>{value}</option>
                ))}
              </select>
            </div>

            <div className="detail-field">
              <span className="detail-label">{t('Severity')}</span>
              <select value={severity} onChange={(event) => setSeverity(event.target.value as IncidentSeverity)} disabled={!canManage}>
                {INCIDENT_SEVERITIES.map((value) => (
                  <option key={value} value={value}>{value}</option>
                ))}
              </select>
            </div>

            <div className="detail-field">
              <span className="detail-label">{t('Vessel')}</span>
              <select value={vesselId} onChange={(event) => setVesselId(event.target.value)} disabled={!canManage}>
                <option value="">{t('Select a vessel')}</option>
                {vessels.map((vessel) => (
                  <option key={vessel.id} value={vessel.id}>{vessel.name}</option>
                ))}
              </select>
            </div>

            <div className="detail-field">
              <span className="detail-label">{t('Environment')}</span>
              <select value={environmentId} onChange={(event) => setEnvironmentId(event.target.value)} disabled={!canManage}>
                <option value="">{t('Select an environment')}</option>
                {environments.map((environment) => (
                  <option key={environment.id} value={environment.id}>{environment.name}</option>
                ))}
              </select>
            </div>

            <div className="detail-field">
              <span className="detail-label">{t('Environment Name')}</span>
              <input value={environmentName} onChange={(event) => setEnvironmentName(event.target.value)} disabled={!canManage} />
            </div>

            <div className="detail-field">
              <span className="detail-label">{t('Deployment')}</span>
              <select value={deploymentId} onChange={(event) => setDeploymentId(event.target.value)} disabled={!canManage}>
                <option value="">{t('No linked deployment')}</option>
                {deployments.map((deployment) => (
                  <option key={deployment.id} value={deployment.id}>{deployment.title}</option>
                ))}
              </select>
            </div>

            <div className="detail-field">
              <span className="detail-label">{t('Release')}</span>
              <select value={releaseId} onChange={(event) => setReleaseId(event.target.value)} disabled={!canManage}>
                <option value="">{t('No linked release')}</option>
                {releases.map((release) => (
                  <option key={release.id} value={release.id}>{release.title}</option>
                ))}
              </select>
            </div>

            <div className="detail-field">
              <span className="detail-label">{t('Mission ID')}</span>
              <input value={missionId} onChange={(event) => setMissionId(event.target.value)} disabled={!canManage} placeholder="mis_..." />
            </div>

            <div className="detail-field">
              <span className="detail-label">{t('Voyage ID')}</span>
              <input value={voyageId} onChange={(event) => setVoyageId(event.target.value)} disabled={!canManage} placeholder="voy_..." />
            </div>

            <div className="detail-field">
              <span className="detail-label">{t('Detected')}</span>
              <input type="datetime-local" value={detectedUtc} onChange={(event) => setDetectedUtc(event.target.value)} disabled={!canManage} />
            </div>

            <div className="detail-field">
              <span className="detail-label">{t('Mitigated')}</span>
              <input type="datetime-local" value={mitigatedUtc} onChange={(event) => setMitigatedUtc(event.target.value)} disabled={!canManage} />
            </div>

            <div className="detail-field">
              <span className="detail-label">{t('Closed')}</span>
              <input type="datetime-local" value={closedUtc} onChange={(event) => setClosedUtc(event.target.value)} disabled={!canManage} />
            </div>

            <div className="detail-field">
              <span className="detail-label">{t('Rollback Deployment')}</span>
              <input value={rollbackDeploymentId} onChange={(event) => setRollbackDeploymentId(event.target.value)} disabled={!canManage} placeholder="dpl_..." />
            </div>

            <div className="detail-field detail-field-full">
              <span className="detail-label">{t('Summary')}</span>
              <textarea rows={3} value={summary} onChange={(event) => setSummary(event.target.value)} disabled={!canManage} />
            </div>

            <div className="detail-field detail-field-full">
              <span className="detail-label">{t('Impact')}</span>
              <textarea rows={3} value={impact} onChange={(event) => setImpact(event.target.value)} disabled={!canManage} />
            </div>

            <div className="detail-field detail-field-full">
              <span className="detail-label">{t('Root Cause')}</span>
              <textarea rows={3} value={rootCause} onChange={(event) => setRootCause(event.target.value)} disabled={!canManage} />
              {!createMode && incident && (
                <span className="text-dim" style={{ fontSize: '0.8rem' }}>
                  {incident.rootCauseWrittenBy
                    ? t('Written by {{author}} {{when}}', {
                      author: incident.rootCauseWrittenBy,
                      when: incident.rootCauseWrittenUtc ? formatRelativeTime(incident.rootCauseWrittenUtc) : '',
                    })
                    : incident.rootCause
                      ? t('Automatic reading, not confirmed by a person.')
                      : t('No root cause recorded.')}
                  {incident.closedAutomatically ? ` ${t('Closed automatically.')}` : ''}
                </span>
              )}
            </div>

            <div className="detail-field detail-field-full">
              <span className="detail-label">{t('Recovery Notes')}</span>
              <textarea rows={4} value={recoveryNotes} onChange={(event) => setRecoveryNotes(event.target.value)} disabled={!canManage} />
            </div>

            <div className="detail-field detail-field-full">
              <span className="detail-label">{t('Postmortem')}</span>
              <textarea rows={5} value={postmortem} onChange={(event) => setPostmortem(event.target.value)} disabled={!canManage} />
            </div>
          </div>

          {canManage && (
            <div className="inline-actions" style={{ marginTop: '1rem' }}>
              <button className="btn btn-primary" disabled={saving} onClick={handleSave}>
                {saving ? t('Saving...') : createMode ? t('Create Incident') : t('Save Incident')}
              </button>
            </div>
          )}
        </section>

        {!createMode && incident && (
          <MissionRecoveryPanel
            missionId={recoveryMissionId}
            report={recoveryReport}
            error={recoveryError}
            loading={recoveryLoading}
          />
        )}

        <section className="card detail-panel">
          <h3>{t('Overview')}</h3>
          <div className="detail-meta-grid">
            <div className="detail-field"><span className="detail-label">{t('Vessel')}</span><span>{selectedVessel?.name || vesselId || '-'}</span></div>
            <div className="detail-field"><span className="detail-label">{t('Environment')}</span><span>{selectedEnvironment?.name || environmentName || '-'}</span></div>
            <div className="detail-field"><span className="detail-label">{t('Deployment')}</span><span>{deploymentId || '-'}</span></div>
            <div className="detail-field"><span className="detail-label">{t('Release')}</span><span>{releaseId || '-'}</span></div>
            <div className="detail-field"><span className="detail-label">{t('Detected')}</span><span>{detectedUtc ? formatDateTime(toUtcValue(detectedUtc) || detectedUtc) : '-'}</span></div>
            <div className="detail-field"><span className="detail-label">{t('Last Updated')}</span><span>{incident?.lastUpdateUtc ? formatRelativeTime(incident.lastUpdateUtc) : '-'}</span></div>
          </div>

          {!createMode && incident && (
            <>
              <div className="detail-divider" />
              <h4>{t('Identifiers')}</h4>
              <div className="detail-key-list">
                <div className="detail-key-row">
                  <span>{t('Incident ID')}</span>
                  <div className="detail-key-actions">
                    <code>{incident.id}</code>
                    <CopyButton text={incident.id} title={t('Copy incident ID')} />
                  </div>
                </div>
                {incident.deploymentId && (
                  <div className="detail-key-row">
                    <span>{t('Deployment')}</span>
                    <div className="detail-key-actions">
                      <Link to={`/deployments/${incident.deploymentId}`}>{incident.deploymentId}</Link>
                    </div>
                  </div>
                )}
                {incident.environmentId && (
                  <div className="detail-key-row">
                    <span>{t('Environment')}</span>
                    <div className="detail-key-actions">
                      <Link to={`/environments/${incident.environmentId}`}>{incident.environmentId}</Link>
                    </div>
                  </div>
                )}
                {incident.releaseId && (
                  <div className="detail-key-row">
                    <span>{t('Release')}</span>
                    <div className="detail-key-actions">
                      <Link to={`/releases/${incident.releaseId}`}>{incident.releaseId}</Link>
                    </div>
                  </div>
                )}
              </div>

              <div className="detail-divider" />
              <h4>{t('Runbook Executions')}</h4>
              {executions.length === 0 ? (
                <p className="text-dim">{t('No runbook executions are linked to this incident yet.')}</p>
              ) : (
                <div style={{ display: 'grid', gap: '0.75rem' }}>
                  {executions.map((execution) => (
                    <div key={execution.id} className="card" style={{ padding: '0.85rem' }}>
                      <div style={{ display: 'flex', justifyContent: 'space-between', gap: '0.75rem', alignItems: 'center' }}>
                        <div>
                          <strong>{execution.title}</strong>
                          <div className="mono text-dim" style={{ fontSize: '0.78rem' }}>{execution.id}</div>
                        </div>
                        <StatusBadge status={execution.status} />
                      </div>
                      <div className="text-dim" style={{ marginTop: '0.4rem' }}>
                        {execution.environmentName || t('No environment')} • {execution.checkType || t('No check type')} • {execution.completedStepIds.length} {t('steps complete')}
                      </div>
                      <div className="text-dim" style={{ marginTop: '0.2rem' }}>
                        {t('Last updated')} {formatRelativeTime(execution.lastUpdateUtc)}
                      </div>
                      <div className="inline-actions" style={{ marginTop: '0.65rem' }}>
                        <button className="btn btn-sm" onClick={() => navigate(`/runbooks/${execution.runbookId}`)}>
                          {t('Open Runbook')}
                        </button>
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </>
          )}
        </section>
      </div>
    </div>
  );
}
