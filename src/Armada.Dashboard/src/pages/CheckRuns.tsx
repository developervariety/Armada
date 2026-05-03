import { useEffect, useMemo, useState } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import {
  listCheckRuns,
  listVessels,
  listWorkflowProfiles,
  resolveWorkflowProfile,
  runCheck,
} from '../api/client';
import type { CheckRun, CheckRunRequest, CheckRunType, Vessel, WorkflowProfile } from '../types/models';
import { useLocale } from '../context/LocaleContext';
import { useNotifications } from '../context/NotificationContext';
import ActionMenu from '../components/shared/ActionMenu';
import ErrorModal from '../components/shared/ErrorModal';
import JsonViewer from '../components/shared/JsonViewer';
import RefreshButton from '../components/shared/RefreshButton';
import StatusBadge from '../components/shared/StatusBadge';

const ALL_CHECK_TYPES: CheckRunType[] = [
  'Lint',
  'Build',
  'UnitTest',
  'IntegrationTest',
  'E2ETest',
  'Package',
  'PublishArtifact',
  'ReleaseVersioning',
  'Changelog',
  'Deploy',
  'Rollback',
  'SmokeTest',
  'HealthCheck',
  'Custom',
];

interface CheckRunPrefillState {
  prefill?: Partial<CheckRunRequest>;
}

function getAvailableCheckTypes(profile: WorkflowProfile | null): CheckRunType[] {
  if (!profile) return ALL_CHECK_TYPES;

  const types: CheckRunType[] = [];
  if (profile.lintCommand) types.push('Lint');
  if (profile.buildCommand) types.push('Build');
  if (profile.unitTestCommand) types.push('UnitTest');
  if (profile.integrationTestCommand) types.push('IntegrationTest');
  if (profile.e2eTestCommand) types.push('E2ETest');
  if (profile.packageCommand) types.push('Package');
  if (profile.publishArtifactCommand) types.push('PublishArtifact');
  if (profile.releaseVersioningCommand) types.push('ReleaseVersioning');
  if (profile.changelogGenerationCommand) types.push('Changelog');
  if (profile.environments.some((environment) => environment.deployCommand)) types.push('Deploy');
  if (profile.environments.some((environment) => environment.rollbackCommand)) types.push('Rollback');
  if (profile.environments.some((environment) => environment.smokeTestCommand)) types.push('SmokeTest');
  if (profile.environments.some((environment) => environment.healthCheckCommand)) types.push('HealthCheck');
  return types.length > 0 ? types : ALL_CHECK_TYPES;
}

function requiresEnvironment(type: CheckRunType): boolean {
  return type === 'Deploy' || type === 'Rollback' || type === 'SmokeTest' || type === 'HealthCheck';
}

export default function CheckRuns() {
  const navigate = useNavigate();
  const location = useLocation();
  const { t, formatDateTime, formatRelativeTime } = useLocale();
  const { pushToast } = useNotifications();

  const [runs, setRuns] = useState<CheckRun[]>([]);
  const [vessels, setVessels] = useState<Vessel[]>([]);
  const [profiles, setProfiles] = useState<WorkflowProfile[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [vesselFilter, setVesselFilter] = useState('all');
  const [statusFilter, setStatusFilter] = useState<'all' | 'Passed' | 'Failed' | 'Running' | 'Pending' | 'Canceled'>('all');
  const [typeFilter, setTypeFilter] = useState<'all' | CheckRunType>('all');
  const [jsonData, setJsonData] = useState<{ open: boolean; title: string; data: unknown }>({ open: false, title: '', data: null });

  const [showRunModal, setShowRunModal] = useState(false);
  const [running, setRunning] = useState(false);
  const [selectedVesselId, setSelectedVesselId] = useState('');
  const [selectedWorkflowProfileId, setSelectedWorkflowProfileId] = useState('');
  const [selectedType, setSelectedType] = useState<CheckRunType>('Build');
  const [environmentName, setEnvironmentName] = useState('');
  const [label, setLabel] = useState('');
  const [missionId, setMissionId] = useState('');
  const [voyageId, setVoyageId] = useState('');
  const [branchName, setBranchName] = useState('');
  const [commitHash, setCommitHash] = useState('');
  const [commandOverride, setCommandOverride] = useState('');
  const [resolvedProfile, setResolvedProfile] = useState<WorkflowProfile | null>(null);
  const [resolvingProfile, setResolvingProfile] = useState(false);

  async function load() {
    try {
      setLoading(true);
      const [runResult, vesselResult, profileResult] = await Promise.all([
        listCheckRuns({ pageSize: 9999 }),
        listVessels({ pageSize: 9999 }),
        listWorkflowProfiles({ pageSize: 9999 }),
      ]);
      setRuns(runResult.objects || []);
      setVessels(vesselResult.objects || []);
      setProfiles(profileResult.objects || []);
      setError('');
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Failed to load check runs.'));
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    load();
  }, []);

  useEffect(() => {
    const state = (location.state || {}) as CheckRunPrefillState;
    if (!state.prefill) return;

    openRunModal(state.prefill);
    navigate(location.pathname, { replace: true, state: null });
  }, [location.pathname, location.state, navigate]);

  useEffect(() => {
    if (!selectedVesselId) {
      setResolvedProfile(null);
      return;
    }

    let cancelled = false;
    setResolvingProfile(true);
    resolveWorkflowProfile(selectedVesselId, selectedWorkflowProfileId || undefined)
      .then((profile) => {
        if (cancelled) return;
        setResolvedProfile(profile);
      })
      .catch(() => {
        if (!cancelled) setResolvedProfile(null);
      })
      .finally(() => {
        if (!cancelled) setResolvingProfile(false);
      });

    return () => { cancelled = true; };
  }, [selectedVesselId, selectedWorkflowProfileId]);

  const availableTypes = useMemo(() => getAvailableCheckTypes(resolvedProfile), [resolvedProfile]);
  const environmentOptions = useMemo(() => resolvedProfile?.environments || [], [resolvedProfile]);

  useEffect(() => {
    if (commandOverride.trim()) return;
    if (availableTypes.includes(selectedType)) return;
    setSelectedType(availableTypes[0] || 'Build');
  }, [availableTypes, commandOverride, selectedType]);

  useEffect(() => {
    if (!requiresEnvironment(selectedType)) {
      if (environmentName) setEnvironmentName('');
      return;
    }

    if (environmentOptions.length === 1 && !environmentName) {
      setEnvironmentName(environmentOptions[0].environmentName);
    }
  }, [environmentName, environmentOptions, selectedType]);

  const filtered = useMemo(() => runs.filter((run) => {
    const matchesVessel = vesselFilter === 'all' || run.vesselId === vesselFilter;
    const matchesStatus = statusFilter === 'all' || run.status === statusFilter;
    const matchesType = typeFilter === 'all' || run.type === typeFilter;
    return matchesVessel && matchesStatus && matchesType;
  }), [runs, statusFilter, typeFilter, vesselFilter]);

  const vesselMap = useMemo(() => new Map(vessels.map((vessel) => [vessel.id, vessel.name])), [vessels]);
  const passedCount = runs.filter((run) => run.status === 'Passed').length;
  const failedCount = runs.filter((run) => run.status === 'Failed').length;
  const runningCount = runs.filter((run) => run.status === 'Running').length;

  function resetRunModal(prefill?: Partial<CheckRunRequest>) {
    setSelectedVesselId(prefill?.vesselId || '');
    setSelectedWorkflowProfileId(prefill?.workflowProfileId || '');
    setSelectedType(prefill?.type || 'Build');
    setEnvironmentName(prefill?.environmentName || '');
    setLabel(prefill?.label || '');
    setMissionId(prefill?.missionId || '');
    setVoyageId(prefill?.voyageId || '');
    setBranchName(prefill?.branchName || '');
    setCommitHash(prefill?.commitHash || '');
    setCommandOverride(prefill?.commandOverride || '');
  }

  function openRunModal(prefill?: Partial<CheckRunRequest>) {
    resetRunModal(prefill);
    setShowRunModal(true);
  }

  async function handleRunCheck() {
    if (!selectedVesselId) {
      setError(t('Select a vessel before running a check.'));
      return;
    }

    if (requiresEnvironment(selectedType) && !environmentName && !commandOverride.trim()) {
      setError(t('Select an environment for this check type.'));
      return;
    }

    try {
      setRunning(true);
      const run = await runCheck({
        vesselId: selectedVesselId,
        workflowProfileId: selectedWorkflowProfileId || null,
        type: selectedType,
        environmentName: environmentName || null,
        label: label || null,
        missionId: missionId || null,
        voyageId: voyageId || null,
        branchName: branchName || null,
        commitHash: commitHash || null,
        commandOverride: commandOverride || null,
      });
      pushToast(run.status === 'Passed' ? 'success' : run.status === 'Failed' ? 'warning' : 'info', t('Check run "{{id}}" completed with status {{status}}.', { id: run.id, status: run.status }));
      setShowRunModal(false);
      await load();
      navigate(`/checks/${run.id}`);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Check run failed.'));
    } finally {
      setRunning(false);
    }
  }

  return (
    <div>
      <div className="view-header">
        <div>
          <h2>{t('Checks')}</h2>
          <p className="text-dim view-subtitle">
            {t('Structured build, test, deploy, and verification runs with durable output, artifacts, and retry support.')}
          </p>
        </div>
        <div className="view-actions">
          <RefreshButton onRefresh={load} title={t('Refresh check runs')} />
          <button className="btn btn-primary" onClick={() => openRunModal()}>
            + {t('Run Check')}
          </button>
        </div>
      </div>

      <ErrorModal error={error} onClose={() => setError('')} />
      <JsonViewer open={jsonData.open} title={jsonData.title} data={jsonData.data} onClose={() => setJsonData({ open: false, title: '', data: null })} />

      {showRunModal && (
        <div className="modal-overlay" onClick={() => !running && setShowRunModal(false)}>
          <div className="modal modal-lg" onClick={(event) => event.stopPropagation()}>
            <h3>{t('Run Check')}</h3>
            <div className="detail-grid" style={{ marginBottom: '1rem' }}>
              <label className="playbook-editor-field">
                <span>{t('Vessel')}</span>
                <select value={selectedVesselId} onChange={(event) => setSelectedVesselId(event.target.value)}>
                  <option value="">{t('Select a vessel...')}</option>
                  {vessels.map((vessel) => (
                    <option key={vessel.id} value={vessel.id}>{vessel.name}</option>
                  ))}
                </select>
              </label>
              <label className="playbook-editor-field">
                <span>{t('Workflow Profile')}</span>
                <select value={selectedWorkflowProfileId} onChange={(event) => setSelectedWorkflowProfileId(event.target.value)}>
                  <option value="">{t('Resolved default')}</option>
                  {profiles.map((profile) => (
                    <option key={profile.id} value={profile.id}>{profile.name}</option>
                  ))}
                </select>
              </label>
              <label className="playbook-editor-field">
                <span>{t('Check Type')}</span>
                <select value={selectedType} onChange={(event) => setSelectedType(event.target.value as CheckRunType)}>
                  {availableTypes.map((type) => (
                    <option key={type} value={type}>{type}</option>
                  ))}
                </select>
              </label>
              {requiresEnvironment(selectedType) && (
                <label className="playbook-editor-field">
                  <span>{t('Environment')}</span>
                  <select value={environmentName} onChange={(event) => setEnvironmentName(event.target.value)}>
                    <option value="">{t('Select an environment...')}</option>
                    {environmentOptions.map((environment) => (
                      <option key={environment.environmentName} value={environment.environmentName}>{environment.environmentName}</option>
                    ))}
                  </select>
                </label>
              )}
            </div>

            <div className="detail-grid" style={{ marginBottom: '1rem' }}>
              <label className="playbook-editor-field">
                <span>{t('Label')}</span>
                <input type="text" value={label} onChange={(event) => setLabel(event.target.value)} placeholder={t('Optional display label')} />
              </label>
              <label className="playbook-editor-field">
                <span>{t('Mission ID')}</span>
                <input type="text" value={missionId} onChange={(event) => setMissionId(event.target.value)} placeholder="mis_..." />
              </label>
              <label className="playbook-editor-field">
                <span>{t('Voyage ID')}</span>
                <input type="text" value={voyageId} onChange={(event) => setVoyageId(event.target.value)} placeholder="voy_..." />
              </label>
              <label className="playbook-editor-field">
                <span>{t('Branch')}</span>
                <input type="text" value={branchName} onChange={(event) => setBranchName(event.target.value)} />
              </label>
              <label className="playbook-editor-field">
                <span>{t('Commit')}</span>
                <input type="text" value={commitHash} onChange={(event) => setCommitHash(event.target.value)} />
              </label>
            </div>

            <label className="playbook-editor-field" style={{ marginBottom: '1rem' }}>
              <span>{t('Command Override')}</span>
              <textarea
                value={commandOverride}
                onChange={(event) => setCommandOverride(event.target.value)}
                rows={4}
                placeholder={t('Optional ad-hoc command. Leave blank to use the selected or resolved workflow profile command.')}
              />
            </label>

            <div className="card" style={{ marginBottom: '1rem', padding: '1rem' }}>
              <strong>{t('Resolved Profile')}</strong>
              <div className="text-dim" style={{ marginTop: '0.4rem' }}>
                {resolvingProfile
                  ? t('Resolving...')
                  : resolvedProfile
                    ? `${resolvedProfile.name} (${resolvedProfile.scope})`
                    : t('No workflow profile resolved. Provide a command override or select a profile.')}
              </div>
            </div>

            <div className="modal-actions">
              <button className="btn btn-primary" disabled={running} onClick={handleRunCheck}>
                {running ? t('Running...') : t('Run Check')}
              </button>
              <button className="btn" disabled={running} onClick={() => setShowRunModal(false)}>
                {t('Cancel')}
              </button>
            </div>
          </div>
        </div>
      )}

      <div className="playbook-overview-grid">
        <div className="card playbook-overview-card">
          <span>{t('Total Runs')}</span>
          <strong>{runs.length}</strong>
        </div>
        <div className="card playbook-overview-card">
          <span>{t('Passed')}</span>
          <strong>{passedCount}</strong>
        </div>
        <div className="card playbook-overview-card">
          <span>{t('Failed')}</span>
          <strong>{failedCount}</strong>
        </div>
        <div className="card playbook-overview-card">
          <span>{t('Running')}</span>
          <strong>{runningCount}</strong>
        </div>
      </div>

      <div className="card" style={{ padding: '1rem', marginBottom: '1rem' }}>
        <div className="playbook-filter-row">
          <select value={vesselFilter} onChange={(event) => setVesselFilter(event.target.value)}>
            <option value="all">{t('All vessels')}</option>
            {vessels.map((vessel) => (
              <option key={vessel.id} value={vessel.id}>{vessel.name}</option>
            ))}
          </select>
          <select value={statusFilter} onChange={(event) => setStatusFilter(event.target.value as typeof statusFilter)}>
            <option value="all">{t('All statuses')}</option>
            <option value="Passed">{t('Passed')}</option>
            <option value="Failed">{t('Failed')}</option>
            <option value="Running">{t('Running')}</option>
            <option value="Pending">{t('Pending')}</option>
            <option value="Canceled">{t('Canceled')}</option>
          </select>
          <select value={typeFilter} onChange={(event) => setTypeFilter(event.target.value as typeof typeFilter)}>
            <option value="all">{t('All check types')}</option>
            {ALL_CHECK_TYPES.map((type) => (
              <option key={type} value={type}>{type}</option>
            ))}
          </select>
        </div>
      </div>

      {loading && runs.length === 0 ? (
        <p className="text-dim">{t('Loading...')}</p>
      ) : filtered.length === 0 ? (
        <div className="playbook-empty-state">
          <strong>{t('No check runs match the current filters.')}</strong>
          <span>{t('Run a structured check to capture build, test, or deploy evidence for a vessel.')}</span>
        </div>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>{t('Check')}</th>
                <th>{t('Vessel')}</th>
                <th>{t('Status')}</th>
                <th>{t('Environment')}</th>
                <th>{t('Duration')}</th>
                <th>{t('Created')}</th>
                <th className="text-right">{t('Actions')}</th>
              </tr>
            </thead>
            <tbody>
              {filtered.map((run) => (
                <tr key={run.id} className="clickable" onClick={() => navigate(`/checks/${run.id}`)}>
                  <td>
                    <strong>{run.label || run.type}</strong>
                    <div className="text-dim" style={{ marginTop: '0.2rem' }}>{run.type}</div>
                    <div className="mono text-dim" style={{ fontSize: '0.78rem' }}>{run.id}</div>
                  </td>
                  <td>{run.vesselId ? (vesselMap.get(run.vesselId) || run.vesselId) : '-'}</td>
                  <td><StatusBadge status={run.status} /></td>
                  <td className="text-dim">{run.environmentName || '-'}</td>
                  <td className="text-dim">{run.durationMs != null ? `${Math.round(run.durationMs)} ms` : '-'}</td>
                  <td className="text-dim" title={formatDateTime(run.createdUtc)}>{formatRelativeTime(run.createdUtc)}</td>
                  <td className="text-right" onClick={(event) => event.stopPropagation()}>
                    <ActionMenu
                      id={`check-run-${run.id}`}
                      items={[
                        { label: 'Open', onClick: () => navigate(`/checks/${run.id}`) },
                        { label: 'View JSON', onClick: () => setJsonData({ open: true, title: run.label || run.id, data: run }) },
                      ]}
                    />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
