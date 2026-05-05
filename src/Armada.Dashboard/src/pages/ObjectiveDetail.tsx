import { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import {
  createObjective,
  deleteObjective,
  getObjective,
  listCheckRuns,
  listDeployments,
  listFleets,
  listIncidents,
  listMissions,
  listPlanningSessions,
  listReleases,
  listVessels,
  listVoyages,
  updateObjective,
} from '../api/client';
import type {
  CheckRun,
  Deployment,
  Fleet,
  Incident,
  Mission,
  Objective,
  ObjectiveStatus,
  ObjectiveUpsertRequest,
  PlanningSession,
  Release,
  Vessel,
  Voyage,
} from '../types/models';
import { useAuth } from '../context/AuthContext';
import { useLocale } from '../context/LocaleContext';
import { useNotifications } from '../context/NotificationContext';
import ConfirmDialog from '../components/shared/ConfirmDialog';
import ErrorModal from '../components/shared/ErrorModal';
import JsonViewer from '../components/shared/JsonViewer';
import StatusBadge from '../components/shared/StatusBadge';

const OBJECTIVE_STATUSES: ObjectiveStatus[] = ['Draft', 'Scoped', 'Planned', 'InProgress', 'Released', 'Deployed', 'Completed', 'Blocked', 'Cancelled'];

function splitList(value: string): string[] {
  return value
    .split(/\r?\n|,/)
    .map((item) => item.trim())
    .filter(Boolean);
}

function joinList(values: string[] | null | undefined): string {
  return (values || []).join('\n');
}

function buildObjectivePlanningPrompt(objective: Objective): string {
  const lines: string[] = [];
  lines.push(`Objective: ${objective.title}`);
  if (objective.description) {
    lines.push('');
    lines.push('Context');
    lines.push(objective.description);
  }
  if (objective.acceptanceCriteria.length > 0) {
    lines.push('');
    lines.push('Acceptance Criteria');
    objective.acceptanceCriteria.forEach((item) => lines.push(`- ${item}`));
  }
  if (objective.nonGoals.length > 0) {
    lines.push('');
    lines.push('Non-Goals');
    objective.nonGoals.forEach((item) => lines.push(`- ${item}`));
  }
  if (objective.rolloutConstraints.length > 0) {
    lines.push('');
    lines.push('Rollout Constraints');
    objective.rolloutConstraints.forEach((item) => lines.push(`- ${item}`));
  }
  if (objective.evidenceLinks.length > 0) {
    lines.push('');
    lines.push('Evidence Links');
    objective.evidenceLinks.forEach((item) => lines.push(`- ${item}`));
  }
  lines.push('');
  lines.push('Create a practical implementation plan, call out risks, and identify the best dispatch shape for this objective.');
  return lines.join('\n');
}

function buildObjectiveDispatchPrompt(objective: Objective): string {
  const lines: string[] = [];
  lines.push(`Implement objective: ${objective.title}`);
  if (objective.description) {
    lines.push('');
    lines.push(objective.description);
  }
  if (objective.acceptanceCriteria.length > 0) {
    lines.push('');
    lines.push('Acceptance Criteria');
    objective.acceptanceCriteria.forEach((item) => lines.push(`- ${item}`));
  }
  if (objective.nonGoals.length > 0) {
    lines.push('');
    lines.push('Non-Goals');
    objective.nonGoals.forEach((item) => lines.push(`- ${item}`));
  }
  if (objective.rolloutConstraints.length > 0) {
    lines.push('');
    lines.push('Constraints');
    objective.rolloutConstraints.forEach((item) => lines.push(`- ${item}`));
  }
  return lines.join('\n');
}

function buildObjectiveReleaseNotes(objective: Objective): string {
  const lines: string[] = [];
  lines.push(`Objective-derived release notes for ${objective.title}`);
  if (objective.description) {
    lines.push('');
    lines.push(objective.description);
  }
  if (objective.acceptanceCriteria.length > 0) {
    lines.push('');
    lines.push('Acceptance Criteria');
    objective.acceptanceCriteria.forEach((item) => lines.push(`- ${item}`));
  }
  if (objective.rolloutConstraints.length > 0) {
    lines.push('');
    lines.push('Rollout Constraints');
    objective.rolloutConstraints.forEach((item) => lines.push(`- ${item}`));
  }
  if (objective.evidenceLinks.length > 0) {
    lines.push('');
    lines.push('Evidence Links');
    objective.evidenceLinks.forEach((item) => lines.push(`- ${item}`));
  }
  return lines.join('\n');
}

export default function ObjectiveDetail() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { isAdmin, isTenantAdmin } = useAuth();
  const { t, formatDateTime, formatRelativeTime } = useLocale();
  const { pushToast } = useNotifications();

  const createMode = id === 'new';
  const canManage = isAdmin || isTenantAdmin;

  const [objective, setObjective] = useState<Objective | null>(null);
  const [fleets, setFleets] = useState<Fleet[]>([]);
  const [vessels, setVessels] = useState<Vessel[]>([]);
  const [planningSessions, setPlanningSessions] = useState<PlanningSession[]>([]);
  const [voyages, setVoyages] = useState<Voyage[]>([]);
  const [missions, setMissions] = useState<Mission[]>([]);
  const [checkRuns, setCheckRuns] = useState<CheckRun[]>([]);
  const [releases, setReleases] = useState<Release[]>([]);
  const [deployments, setDeployments] = useState<Deployment[]>([]);
  const [incidents, setIncidents] = useState<Incident[]>([]);
  const [title, setTitle] = useState('Objective');
  const [description, setDescription] = useState('');
  const [status, setStatus] = useState<ObjectiveStatus>('Draft');
  const [owner, setOwner] = useState('');
  const [tags, setTags] = useState('');
  const [acceptanceCriteria, setAcceptanceCriteria] = useState('');
  const [nonGoals, setNonGoals] = useState('');
  const [rolloutConstraints, setRolloutConstraints] = useState('');
  const [evidenceLinks, setEvidenceLinks] = useState('');
  const [fleetIds, setFleetIds] = useState('');
  const [vesselIds, setVesselIds] = useState('');
  const [planningSessionIds, setPlanningSessionIds] = useState('');
  const [voyageIds, setVoyageIds] = useState('');
  const [missionIds, setMissionIds] = useState('');
  const [checkRunIds, setCheckRunIds] = useState('');
  const [releaseIds, setReleaseIds] = useState('');
  const [deploymentIds, setDeploymentIds] = useState('');
  const [incidentIds, setIncidentIds] = useState('');
  const [loading, setLoading] = useState(!createMode);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [jsonData, setJsonData] = useState<{ open: boolean; title: string; data: unknown }>({ open: false, title: '', data: null });
  const [confirm, setConfirm] = useState<{ open: boolean; title: string; message: string; onConfirm: () => void }>({
    open: false,
    title: '',
    message: '',
    onConfirm: () => {},
  });

  useEffect(() => {
    let cancelled = false;

    Promise.all([
      listFleets({ pageSize: 9999 }),
      listVessels({ pageSize: 9999 }),
      listPlanningSessions(),
      listVoyages({ pageSize: 9999 }),
      listMissions({ pageSize: 9999 }),
      listCheckRuns({ pageSize: 9999 }),
      listReleases({ pageSize: 9999 }),
      listDeployments({ pageSize: 9999 }),
      listIncidents({ pageSize: 9999 }),
    ]).then(([fleetResult, vesselResult, planningResult, voyageResult, missionResult, checkRunResult, releaseResult, deploymentResult, incidentResult]) => {
      if (cancelled) return;
      setFleets(fleetResult.objects || []);
      setVessels(vesselResult.objects || []);
      setPlanningSessions(planningResult || []);
      setVoyages(voyageResult.objects || []);
      setMissions(missionResult.objects || []);
      setCheckRuns(checkRunResult.objects || []);
      setReleases(releaseResult.objects || []);
      setDeployments(deploymentResult.objects || []);
      setIncidents(incidentResult.objects || []);
    }).catch((err: unknown) => {
      if (!cancelled) setError(err instanceof Error ? err.message : t('Failed to load objective reference data.'));
    });

    return () => { cancelled = true; };
  }, [t]);

  useEffect(() => {
    if (createMode || !id) return;
    let mounted = true;
    const objectiveId = id;

    async function load() {
      try {
        setLoading(true);
        const result = await getObjective(objectiveId);
        if (!mounted) return;
        setObjective(result);
        setTitle(result.title);
        setDescription(result.description || '');
        setStatus(result.status);
        setOwner(result.owner || '');
        setTags(joinList(result.tags));
        setAcceptanceCriteria(joinList(result.acceptanceCriteria));
        setNonGoals(joinList(result.nonGoals));
        setRolloutConstraints(joinList(result.rolloutConstraints));
        setEvidenceLinks(joinList(result.evidenceLinks));
        setFleetIds(joinList(result.fleetIds));
        setVesselIds(joinList(result.vesselIds));
        setPlanningSessionIds(joinList(result.planningSessionIds));
        setVoyageIds(joinList(result.voyageIds));
        setMissionIds(joinList(result.missionIds));
        setCheckRunIds(joinList(result.checkRunIds));
        setReleaseIds(joinList(result.releaseIds));
        setDeploymentIds(joinList(result.deploymentIds));
        setIncidentIds(joinList(result.incidentIds));
        setError('');
      } catch (err: unknown) {
        if (mounted) setError(err instanceof Error ? err.message : t('Failed to load objective.'));
      } finally {
        if (mounted) setLoading(false);
      }
    }

    void load();
    return () => { mounted = false; };
  }, [createMode, id, t]);

  const fleetMap = useMemo(() => new Map(fleets.map((item) => [item.id, item.name])), [fleets]);
  const vesselMap = useMemo(() => new Map(vessels.map((item) => [item.id, item.name])), [vessels]);
  const planningMap = useMemo(() => new Map(planningSessions.map((item) => [item.id, item.title || item.id])), [planningSessions]);
  const voyageMap = useMemo(() => new Map(voyages.map((item) => [item.id, item.title || item.id])), [voyages]);
  const missionMap = useMemo(() => new Map(missions.map((item) => [item.id, item.title || item.id])), [missions]);
  const checkRunMap = useMemo(() => new Map(checkRuns.map((item) => [item.id, item.label || item.type])), [checkRuns]);
  const releaseMap = useMemo(() => new Map(releases.map((item) => [item.id, item.title || item.id])), [releases]);
  const deploymentMap = useMemo(() => new Map(deployments.map((item) => [item.id, item.title || item.id])), [deployments]);
  const incidentMap = useMemo(() => new Map(incidents.map((item) => [item.id, item.title || item.id])), [incidents]);
  const primaryFleetId = objective?.fleetIds[0] || '';
  const primaryVesselId = objective?.vesselIds[0] || '';

  function buildPayload(): ObjectiveUpsertRequest {
    return {
      title: title.trim() || null,
      description: description.trim() || null,
      status,
      owner: owner.trim() || null,
      tags: splitList(tags),
      acceptanceCriteria: splitList(acceptanceCriteria),
      nonGoals: splitList(nonGoals),
      rolloutConstraints: splitList(rolloutConstraints),
      evidenceLinks: splitList(evidenceLinks),
      fleetIds: splitList(fleetIds),
      vesselIds: splitList(vesselIds),
      planningSessionIds: splitList(planningSessionIds),
      voyageIds: splitList(voyageIds),
      missionIds: splitList(missionIds),
      checkRunIds: splitList(checkRunIds),
      releaseIds: splitList(releaseIds),
      deploymentIds: splitList(deploymentIds),
      incidentIds: splitList(incidentIds),
    };
  }

  async function handleSave() {
    if (!canManage) return;
    try {
      setSaving(true);
      const payload = buildPayload();
      if (createMode) {
        const created = await createObjective(payload);
        pushToast('success', t('Objective "{{title}}" created.', { title: created.title }));
        navigate(`/objectives/${created.id}`);
        return;
      }

      if (!id) return;
      const updated = await updateObjective(id, payload);
      setObjective(updated);
      setTitle(updated.title);
      setDescription(updated.description || '');
      setStatus(updated.status);
      setOwner(updated.owner || '');
      pushToast('success', t('Objective "{{title}}" saved.', { title: updated.title }));
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Save failed.'));
    } finally {
      setSaving(false);
    }
  }

  function handleDelete() {
    if (!objective || !canManage) return;
    setConfirm({
      open: true,
      title: t('Delete Objective'),
      message: t('Delete "{{title}}"? This removes the objective snapshot chain only.', { title: objective.title }),
      onConfirm: async () => {
        setConfirm((current) => ({ ...current, open: false }));
        try {
          await deleteObjective(objective.id);
          pushToast('warning', t('Objective "{{title}}" deleted.', { title: objective.title }));
          navigate('/objectives');
        } catch (err: unknown) {
          setError(err instanceof Error ? err.message : t('Delete failed.'));
        }
      },
    });
  }

  function renderLinks(ids: string[], kind: 'fleet' | 'vessel' | 'planning' | 'voyage' | 'mission' | 'check' | 'release' | 'deployment' | 'incident') {
    if (ids.length === 0)
      return <span className="text-dim">{t('None')}</span>;

    const map = kind === 'fleet'
      ? fleetMap
      : kind === 'vessel'
        ? vesselMap
        : kind === 'planning'
          ? planningMap
          : kind === 'voyage'
            ? voyageMap
            : kind === 'mission'
              ? missionMap
              : kind === 'check'
                ? checkRunMap
                : kind === 'release'
                  ? releaseMap
                  : kind === 'deployment'
                    ? deploymentMap
                    : incidentMap;

    const pathPrefix = kind === 'fleet'
      ? '/fleets/'
      : kind === 'vessel'
        ? '/vessels/'
        : kind === 'planning'
          ? '/planning/'
          : kind === 'voyage'
            ? '/voyages/'
            : kind === 'mission'
              ? '/missions/'
              : kind === 'check'
                ? '/checks/'
                : kind === 'release'
                  ? '/releases/'
                  : kind === 'deployment'
                    ? '/deployments/'
                    : '/incidents/';

    return (
      <div style={{ display: 'grid', gap: '0.35rem' }}>
        {ids.map((item) => (
          <div key={`${kind}-${item}`}>
            <Link to={`${pathPrefix}${item}`}>{map.get(item) || item}</Link>
            <span className="mono text-dim" style={{ marginLeft: '0.45rem', fontSize: '0.78rem' }}>{item}</span>
          </div>
        ))}
      </div>
    );
  }

  if (loading) return <p className="text-dim">{t('Loading...')}</p>;

  return (
    <div>
      <div className="breadcrumb">
        <Link to="/objectives">{t('Objectives')}</Link> <span className="breadcrumb-sep">&gt;</span> <span>{createMode ? t('New Objective') : title}</span>
      </div>

      <div className="detail-header">
        <h2>{createMode ? t('Create Objective') : title}</h2>
        <div className="inline-actions">
          {!createMode && <StatusBadge status={status} />}
          {!createMode && (
            <button className="btn btn-sm" onClick={() => setJsonData({ open: true, title, data: objective })}>
              {t('View JSON')}
            </button>
          )}
          {!createMode && (
            <button className="btn btn-sm" onClick={() => navigate(`/history?objectiveId=${encodeURIComponent(objective?.id || '')}`)}>
              {t('History')}
            </button>
          )}
          {!createMode && objective && (
            <button
              className="btn btn-sm"
              disabled={!primaryVesselId}
              onClick={() => navigate('/planning', {
                state: {
                  fromObjective: true,
                  objectiveId: objective.id,
                  title: `${objective.title} Planning`,
                  fleetId: primaryFleetId || undefined,
                  vesselId: primaryVesselId || undefined,
                  initialPrompt: buildObjectivePlanningPrompt(objective),
                },
              })}
            >
              {t('Plan')}
            </button>
          )}
          {!createMode && objective && (
            <button
              className="btn btn-sm"
              disabled={!primaryVesselId}
              onClick={() => navigate('/dispatch', {
                state: {
                  fromObjective: true,
                  objectiveId: objective.id,
                  vesselId: primaryVesselId || undefined,
                  prompt: buildObjectiveDispatchPrompt(objective),
                  voyageTitle: objective.title,
                },
              })}
            >
              {t('Dispatch')}
            </button>
          )}
          {!createMode && objective && (
            <button
              className="btn btn-sm"
              disabled={!primaryVesselId}
              onClick={() => navigate('/releases/new', {
                state: {
                  prefill: {
                    vesselId: primaryVesselId || null,
                    title: `${objective.title} Release`,
                    summary: objective.description || null,
                    notes: buildObjectiveReleaseNotes(objective),
                    status: 'Draft',
                  },
                  objectiveIds: [objective.id],
                },
              })}
            >
              {t('Draft Release')}
            </button>
          )}
          {!createMode && canManage && (
            <button className="btn btn-sm btn-danger" onClick={handleDelete}>
              {t('Delete')}
            </button>
          )}
        </div>
      </div>

      <ErrorModal error={error} onClose={() => setError('')} />
      <JsonViewer open={jsonData.open} title={jsonData.title} data={jsonData.data} onClose={() => setJsonData({ open: false, title: '', data: null })} />
      <ConfirmDialog
        open={confirm.open}
        title={confirm.title}
        message={confirm.message}
        onConfirm={confirm.onConfirm}
        onCancel={() => setConfirm((current) => ({ ...current, open: false }))}
      />

      {!canManage && (
        <div className="alert alert-error" style={{ marginBottom: '1rem' }}>
          {t('You can view objectives, but only tenant administrators can create or change them.')}
        </div>
      )}

      {!createMode && objective && (
        <div className="detail-grid" style={{ marginBottom: '1rem' }}>
          <div className="info-card">
            <span>{t('Owner')}</span>
            <strong>{objective.owner || t('Unassigned')}</strong>
          </div>
          <div className="info-card">
            <span>{t('Linked Vessels')}</span>
            <strong>{objective.vesselIds.length}</strong>
          </div>
          <div className="info-card">
            <span>{t('Linked Releases')}</span>
            <strong>{objective.releaseIds.length}</strong>
          </div>
          <div className="info-card">
            <span>{t('Linked Deployments')}</span>
            <strong>{objective.deploymentIds.length}</strong>
          </div>
            <div className="info-card">
              <span>{t('Linked Incidents')}</span>
              <strong>{objective.incidentIds.length}</strong>
            </div>
            <div className="info-card">
              <span>{t('Last Updated')}</span>
              <strong title={formatDateTime(objective.lastUpdateUtc)}>{formatRelativeTime(objective.lastUpdateUtc)}</strong>
            </div>
          </div>
      )}

      {!createMode && objective && !primaryVesselId && (
        <div className="alert" style={{ marginBottom: '1rem' }}>
          {t('Link at least one vessel to this objective before launching planning, dispatch, or release follow-through from it.')}
        </div>
      )}

      <div className="detail-sections">
        <div className="card detail-section">
          <div className="detail-section-header">
            <h3>{t('Objective')}</h3>
          </div>
          <div className="detail-form-grid">
            <div className="form-field">
              <label>{t('Title')}</label>
              <input value={title} onChange={(event) => setTitle(event.target.value)} disabled={!canManage} />
            </div>
            <div className="form-field">
              <label>{t('Owner')}</label>
              <input value={owner} onChange={(event) => setOwner(event.target.value)} disabled={!canManage} />
            </div>
            <div className="form-field" style={{ gridColumn: '1 / -1' }}>
              <label>{t('Status')}</label>
              <select value={status} onChange={(event) => setStatus(event.target.value as ObjectiveStatus)} disabled={!canManage}>
                {OBJECTIVE_STATUSES.map((value) => (
                  <option key={value} value={value}>{value}</option>
                ))}
              </select>
            </div>
            <div className="form-field" style={{ gridColumn: '1 / -1' }}>
              <label>{t('Description')}</label>
              <textarea rows={4} value={description} onChange={(event) => setDescription(event.target.value)} disabled={!canManage} />
            </div>
            <div className="form-field" style={{ gridColumn: '1 / -1' }}>
              <label>{t('Tags')}</label>
              <textarea rows={2} value={tags} onChange={(event) => setTags(event.target.value)} disabled={!canManage} placeholder={t('One per line or comma-separated')} />
            </div>
          </div>
        </div>

        <div className="card detail-section">
          <div className="detail-section-header">
            <h3>{t('Scope')}</h3>
          </div>
          <div className="detail-form-grid">
            <div className="form-field">
              <label>{t('Acceptance Criteria')}</label>
              <textarea rows={6} value={acceptanceCriteria} onChange={(event) => setAcceptanceCriteria(event.target.value)} disabled={!canManage} />
            </div>
            <div className="form-field">
              <label>{t('Non-Goals')}</label>
              <textarea rows={6} value={nonGoals} onChange={(event) => setNonGoals(event.target.value)} disabled={!canManage} />
            </div>
            <div className="form-field" style={{ gridColumn: '1 / -1' }}>
              <label>{t('Rollout Constraints')}</label>
              <textarea rows={4} value={rolloutConstraints} onChange={(event) => setRolloutConstraints(event.target.value)} disabled={!canManage} />
            </div>
            <div className="form-field" style={{ gridColumn: '1 / -1' }}>
              <label>{t('Evidence Links')}</label>
              <textarea rows={4} value={evidenceLinks} onChange={(event) => setEvidenceLinks(event.target.value)} disabled={!canManage} />
            </div>
          </div>
        </div>

        <div className="card detail-section">
          <div className="detail-section-header">
            <h3>{t('Links')}</h3>
          </div>
          <div className="detail-form-grid">
            <div className="form-field">
              <label>{t('Fleet IDs')}</label>
              <textarea rows={4} value={fleetIds} onChange={(event) => setFleetIds(event.target.value)} disabled={!canManage} />
            </div>
            <div className="form-field">
              <label>{t('Vessel IDs')}</label>
              <textarea rows={4} value={vesselIds} onChange={(event) => setVesselIds(event.target.value)} disabled={!canManage} />
            </div>
            <div className="form-field">
              <label>{t('Planning Session IDs')}</label>
              <textarea rows={4} value={planningSessionIds} onChange={(event) => setPlanningSessionIds(event.target.value)} disabled={!canManage} />
            </div>
            <div className="form-field">
              <label>{t('Voyage IDs')}</label>
              <textarea rows={4} value={voyageIds} onChange={(event) => setVoyageIds(event.target.value)} disabled={!canManage} />
            </div>
            <div className="form-field">
              <label>{t('Mission IDs')}</label>
              <textarea rows={4} value={missionIds} onChange={(event) => setMissionIds(event.target.value)} disabled={!canManage} />
            </div>
            <div className="form-field">
              <label>{t('Check Run IDs')}</label>
              <textarea rows={4} value={checkRunIds} onChange={(event) => setCheckRunIds(event.target.value)} disabled={!canManage} />
            </div>
            <div className="form-field">
              <label>{t('Release IDs')}</label>
              <textarea rows={4} value={releaseIds} onChange={(event) => setReleaseIds(event.target.value)} disabled={!canManage} />
            </div>
            <div className="form-field">
              <label>{t('Deployment IDs')}</label>
              <textarea rows={4} value={deploymentIds} onChange={(event) => setDeploymentIds(event.target.value)} disabled={!canManage} />
            </div>
            <div className="form-field" style={{ gridColumn: '1 / -1' }}>
              <label>{t('Incident IDs')}</label>
              <textarea rows={4} value={incidentIds} onChange={(event) => setIncidentIds(event.target.value)} disabled={!canManage} />
            </div>
          </div>
        </div>

        {!createMode && objective && (
          <>
            <div className="card detail-section">
              <div className="detail-section-header">
                <h3>{t('Linked Fleets and Vessels')}</h3>
              </div>
              <div className="linked-grid linked-grid-two">
                <div>
                  <h4>{t('Fleets')}</h4>
                  {renderLinks(objective.fleetIds, 'fleet')}
                </div>
                <div>
                  <h4>{t('Vessels')}</h4>
                  {renderLinks(objective.vesselIds, 'vessel')}
                </div>
              </div>
            </div>

            <div className="card detail-section">
              <div className="detail-section-header">
                <h3>{t('Linked Planning and Work')}</h3>
              </div>
              <div className="linked-grid linked-grid-two">
                <div>
                  <h4>{t('Planning Sessions')}</h4>
                  {renderLinks(objective.planningSessionIds, 'planning')}
                </div>
                <div>
                  <h4>{t('Voyages')}</h4>
                  {renderLinks(objective.voyageIds, 'voyage')}
                </div>
                <div>
                  <h4>{t('Missions')}</h4>
                  {renderLinks(objective.missionIds, 'mission')}
                </div>
                <div>
                  <h4>{t('Checks')}</h4>
                  {renderLinks(objective.checkRunIds, 'check')}
                </div>
              </div>
            </div>

            <div className="card detail-section">
              <div className="detail-section-header">
                <h3>{t('Linked Delivery')}</h3>
              </div>
              <div className="linked-grid linked-grid-two">
                <div>
                  <h4>{t('Releases')}</h4>
                  {renderLinks(objective.releaseIds, 'release')}
                </div>
                <div>
                  <h4>{t('Deployments')}</h4>
                  {renderLinks(objective.deploymentIds, 'deployment')}
                </div>
                <div style={{ gridColumn: '1 / -1' }}>
                  <h4>{t('Incidents')}</h4>
                  {renderLinks(objective.incidentIds, 'incident')}
                </div>
              </div>
            </div>
          </>
        )}
      </div>

      <div className="detail-footer">
        <button className="btn btn-secondary" onClick={() => navigate('/objectives')}>
          {t('Back')}
        </button>
        {canManage && (
          <button className="btn btn-primary" disabled={saving} onClick={handleSave}>
            {saving ? t('Saving...') : createMode ? t('Create Objective') : t('Save Changes')}
          </button>
        )}
      </div>
    </div>
  );
}
