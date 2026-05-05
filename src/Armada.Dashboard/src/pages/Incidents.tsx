import { useEffect, useMemo, useState } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import {
  deleteIncident,
  listDeployments,
  listEnvironments,
  listIncidents,
  listReleases,
  listVessels,
} from '../api/client';
import type {
  Deployment,
  DeploymentEnvironment,
  Incident,
  IncidentSeverity,
  IncidentStatus,
  Release,
  Vessel,
} from '../types/models';
import { useAuth } from '../context/AuthContext';
import { useLocale } from '../context/LocaleContext';
import { useNotifications } from '../context/NotificationContext';
import ActionMenu from '../components/shared/ActionMenu';
import ConfirmDialog from '../components/shared/ConfirmDialog';
import ErrorModal from '../components/shared/ErrorModal';
import JsonViewer from '../components/shared/JsonViewer';
import RefreshButton from '../components/shared/RefreshButton';
import StatusBadge from '../components/shared/StatusBadge';

const INCIDENT_STATUSES: IncidentStatus[] = ['Open', 'Monitoring', 'Mitigated', 'RolledBack', 'Closed'];
const INCIDENT_SEVERITIES: IncidentSeverity[] = ['Critical', 'High', 'Medium', 'Low'];

interface IncidentPageState {
  prefill?: Record<string, unknown>;
}

export default function Incidents() {
  const navigate = useNavigate();
  const location = useLocation();
  const { isAdmin, isTenantAdmin } = useAuth();
  const { t, formatDateTime, formatRelativeTime } = useLocale();
  const { pushToast } = useNotifications();

  const [incidents, setIncidents] = useState<Incident[]>([]);
  const [vessels, setVessels] = useState<Vessel[]>([]);
  const [environments, setEnvironments] = useState<DeploymentEnvironment[]>([]);
  const [deployments, setDeployments] = useState<Deployment[]>([]);
  const [releases, setReleases] = useState<Release[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [search, setSearch] = useState('');
  const [statusFilter, setStatusFilter] = useState<'all' | IncidentStatus>('all');
  const [severityFilter, setSeverityFilter] = useState<'all' | IncidentSeverity>('all');
  const [jsonData, setJsonData] = useState<{ open: boolean; title: string; data: unknown }>({ open: false, title: '', data: null });
  const [confirm, setConfirm] = useState<{ open: boolean; title: string; message: string; onConfirm: () => void }>({
    open: false,
    title: '',
    message: '',
    onConfirm: () => {},
  });

  const canManage = isAdmin || isTenantAdmin;
  const carryState = (location.state as IncidentPageState | null) || null;

  async function load() {
    try {
      setLoading(true);
      const [incidentResult, vesselResult, environmentResult, deploymentResult, releaseResult] = await Promise.all([
        listIncidents({ pageSize: 9999 }),
        listVessels({ pageSize: 9999 }),
        listEnvironments({ pageSize: 9999 }),
        listDeployments({ pageSize: 9999 }),
        listReleases({ pageSize: 9999 }),
      ]);
      setIncidents(incidentResult.objects || []);
      setVessels(vesselResult.objects || []);
      setEnvironments(environmentResult.objects || []);
      setDeployments(deploymentResult.objects || []);
      setReleases(releaseResult.objects || []);
      setError('');
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Failed to load incidents.'));
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    void load();
  }, []);

  const vesselMap = useMemo(() => new Map(vessels.map((vessel) => [vessel.id, vessel.name])), [vessels]);
  const environmentMap = useMemo(() => new Map(environments.map((environment) => [environment.id, environment.name])), [environments]);
  const deploymentMap = useMemo(() => new Map(deployments.map((deployment) => [deployment.id, deployment.title])), [deployments]);
  const releaseMap = useMemo(() => new Map(releases.map((release) => [release.id, release.title])), [releases]);

  const filtered = useMemo(() => incidents.filter((incident) => {
    const normalizedSearch = search.trim().toLowerCase();
    const matchesSearch = normalizedSearch.length === 0
      || incident.title.toLowerCase().includes(normalizedSearch)
      || (incident.summary || '').toLowerCase().includes(normalizedSearch)
      || (incident.environmentName || '').toLowerCase().includes(normalizedSearch)
      || (incident.impact || '').toLowerCase().includes(normalizedSearch)
      || incident.id.toLowerCase().includes(normalizedSearch);
    const matchesStatus = statusFilter === 'all' || incident.status === statusFilter;
    const matchesSeverity = severityFilter === 'all' || incident.severity === severityFilter;
    return matchesSearch && matchesStatus && matchesSeverity;
  }), [incidents, search, severityFilter, statusFilter]);

  const openCount = incidents.filter((incident) => incident.status === 'Open').length;
  const monitoringCount = incidents.filter((incident) => incident.status === 'Monitoring').length;
  const mitigatedCount = incidents.filter((incident) => incident.status === 'Mitigated').length;
  const closedCount = incidents.filter((incident) => incident.status === 'Closed' || incident.status === 'RolledBack').length;

  function handleDelete(incident: Incident) {
    setConfirm({
      open: true,
      title: t('Delete Incident'),
      message: t('Delete "{{title}}"? This removes the incident snapshot chain but does not affect deployments, checks, or releases.', { title: incident.title }),
      onConfirm: async () => {
        setConfirm((current) => ({ ...current, open: false }));
        try {
          await deleteIncident(incident.id);
          pushToast('warning', t('Incident "{{title}}" deleted.', { title: incident.title }));
          await load();
        } catch (err: unknown) {
          setError(err instanceof Error ? err.message : t('Delete failed.'));
        }
      },
    });
  }

  return (
    <div>
      <div className="view-header">
        <div>
          <h2>{t('Incidents')}</h2>
          <p className="text-dim view-subtitle">
            {t('Track incidents, hotfix context, rollback history, recovery notes, and postmortems alongside deployments and releases.')}
          </p>
        </div>
        <div className="view-actions">
          <RefreshButton onRefresh={load} title={t('Refresh incidents')} />
          {canManage && (
            <button className="btn btn-primary" onClick={() => navigate('/incidents/new', { state: carryState })}>
              + {t('Incident')}
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

      <div className="playbook-overview-grid">
        <div className="card playbook-overview-card">
          <span>{t('Total Incidents')}</span>
          <strong>{incidents.length}</strong>
        </div>
        <div className="card playbook-overview-card">
          <span>{t('Open')}</span>
          <strong>{openCount}</strong>
        </div>
        <div className="card playbook-overview-card">
          <span>{t('Monitoring')}</span>
          <strong>{monitoringCount}</strong>
        </div>
        <div className="card playbook-overview-card">
          <span>{t('Mitigated')}</span>
          <strong>{mitigatedCount}</strong>
        </div>
        <div className="card playbook-overview-card">
          <span>{t('Closed / Rolled Back')}</span>
          <strong>{closedCount}</strong>
        </div>
      </div>

      <div className="card" style={{ padding: '1rem', marginBottom: '1rem' }}>
        <div className="playbook-filter-row">
          <input
            type="text"
            value={search}
            onChange={(event) => setSearch(event.target.value)}
            placeholder={t('Search by title, summary, impact, environment, or ID...')}
          />
          <select value={statusFilter} onChange={(event) => setStatusFilter(event.target.value as typeof statusFilter)}>
            <option value="all">{t('All statuses')}</option>
            {INCIDENT_STATUSES.map((status) => (
              <option key={status} value={status}>{status}</option>
            ))}
          </select>
          <select value={severityFilter} onChange={(event) => setSeverityFilter(event.target.value as typeof severityFilter)}>
            <option value="all">{t('All severities')}</option>
            {INCIDENT_SEVERITIES.map((severity) => (
              <option key={severity} value={severity}>{severity}</option>
            ))}
          </select>
        </div>
      </div>

      {loading && incidents.length === 0 ? (
        <p className="text-dim">{t('Loading...')}</p>
      ) : filtered.length === 0 ? (
        <div className="playbook-empty-state">
          <strong>{t('No incidents match the current filters.')}</strong>
          <span>{canManage ? t('Create incidents from deployments or environments to preserve hotfix and rollback context.') : t('Ask a tenant administrator to create and manage incident records.')}</span>
        </div>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>{t('Incident')}</th>
                <th>{t('Status')}</th>
                <th>{t('Severity')}</th>
                <th>{t('Environment')}</th>
                <th>{t('Deployment')}</th>
                <th>{t('Release')}</th>
                <th>{t('Last Updated')}</th>
                <th className="text-right">{t('Actions')}</th>
              </tr>
            </thead>
            <tbody>
              {filtered.map((incident) => (
                <tr key={incident.id} className="clickable" onClick={() => navigate(`/incidents/${incident.id}`, { state: carryState })}>
                  <td>
                    <strong>{incident.title}</strong>
                    <div className="text-dim" style={{ marginTop: '0.2rem' }}>
                      {incident.summary || incident.impact || t('No summary provided')}
                    </div>
                    <div className="mono text-dim" style={{ fontSize: '0.78rem' }}>{incident.id}</div>
                  </td>
                  <td><StatusBadge status={incident.status} /></td>
                  <td><StatusBadge status={incident.severity} /></td>
                  <td className="text-dim">{incident.environmentId ? (environmentMap.get(incident.environmentId) || incident.environmentName || incident.environmentId) : (incident.environmentName || '-')}</td>
                  <td className="text-dim">{incident.deploymentId ? (deploymentMap.get(incident.deploymentId) || incident.deploymentId) : '-'}</td>
                  <td className="text-dim">{incident.releaseId ? (releaseMap.get(incident.releaseId) || incident.releaseId) : '-'}</td>
                  <td className="text-dim" title={formatDateTime(incident.lastUpdateUtc)}>{formatRelativeTime(incident.lastUpdateUtc)}</td>
                  <td className="text-right" onClick={(event) => event.stopPropagation()}>
                    <ActionMenu
                      id={`incident-${incident.id}`}
                      items={[
                        { label: 'Open', onClick: () => navigate(`/incidents/${incident.id}`, { state: carryState }) },
                        { label: 'View JSON', onClick: () => setJsonData({ open: true, title: incident.title, data: incident }) },
                        ...(canManage ? [{ label: 'Delete', danger: true as const, onClick: () => handleDelete(incident) }] : []),
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
