import { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  deleteDeployment,
  listDeployments,
  listEnvironments,
  listReleases,
  listVessels,
} from '../api/client';
import type {
  Deployment,
  DeploymentEnvironment,
  DeploymentStatus,
  DeploymentVerificationStatus,
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

const DEPLOYMENT_STATUSES: DeploymentStatus[] = [
  'PendingApproval',
  'Running',
  'Succeeded',
  'VerificationFailed',
  'Failed',
  'Denied',
  'RollingBack',
  'RolledBack',
];

const VERIFICATION_STATUSES: DeploymentVerificationStatus[] = [
  'NotRun',
  'Running',
  'Passed',
  'Failed',
  'Partial',
  'Skipped',
];

export default function Deployments() {
  const navigate = useNavigate();
  const { isAdmin, isTenantAdmin } = useAuth();
  const { t, formatDateTime, formatRelativeTime } = useLocale();
  const { pushToast } = useNotifications();

  const [deployments, setDeployments] = useState<Deployment[]>([]);
  const [vessels, setVessels] = useState<Vessel[]>([]);
  const [environments, setEnvironments] = useState<DeploymentEnvironment[]>([]);
  const [releases, setReleases] = useState<Release[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [search, setSearch] = useState('');
  const [statusFilter, setStatusFilter] = useState<'all' | DeploymentStatus>('all');
  const [verificationFilter, setVerificationFilter] = useState<'all' | DeploymentVerificationStatus>('all');
  const [vesselFilter, setVesselFilter] = useState('all');
  const [jsonData, setJsonData] = useState<{ open: boolean; title: string; data: unknown }>({ open: false, title: '', data: null });
  const [confirm, setConfirm] = useState<{ open: boolean; title: string; message: string; onConfirm: () => void }>({
    open: false,
    title: '',
    message: '',
    onConfirm: () => {},
  });

  const canManage = isAdmin || isTenantAdmin;

  async function load() {
    try {
      setLoading(true);
      const [deploymentResult, vesselResult, environmentResult, releaseResult] = await Promise.all([
        listDeployments({ pageSize: 9999 }),
        listVessels({ pageSize: 9999 }),
        listEnvironments({ pageSize: 9999 }),
        listReleases({ pageSize: 9999 }),
      ]);
      setDeployments(deploymentResult.objects || []);
      setVessels(vesselResult.objects || []);
      setEnvironments(environmentResult.objects || []);
      setReleases(releaseResult.objects || []);
      setError('');
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Failed to load deployments.'));
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    load();
  }, []);

  const vesselMap = useMemo(() => new Map(vessels.map((vessel) => [vessel.id, vessel.name])), [vessels]);
  const environmentMap = useMemo(() => new Map(environments.map((environment) => [environment.id, environment.name])), [environments]);
  const releaseMap = useMemo(() => new Map(releases.map((release) => [release.id, release.title])), [releases]);

  const filtered = useMemo(() => deployments.filter((deployment) => {
    const normalizedSearch = search.trim().toLowerCase();
    const matchesSearch = normalizedSearch.length === 0
      || deployment.title.toLowerCase().includes(normalizedSearch)
      || (deployment.environmentName || '').toLowerCase().includes(normalizedSearch)
      || (deployment.summary || '').toLowerCase().includes(normalizedSearch)
      || (deployment.sourceRef || '').toLowerCase().includes(normalizedSearch)
      || deployment.id.toLowerCase().includes(normalizedSearch);

    const matchesStatus = statusFilter === 'all' || deployment.status === statusFilter;
    const matchesVerification = verificationFilter === 'all' || deployment.verificationStatus === verificationFilter;
    const matchesVessel = vesselFilter === 'all' || deployment.vesselId === vesselFilter;
    return matchesSearch && matchesStatus && matchesVerification && matchesVessel;
  }), [deployments, search, statusFilter, verificationFilter, vesselFilter]);

  const pendingApprovalCount = deployments.filter((deployment) => deployment.status === 'PendingApproval').length;
  const runningCount = deployments.filter((deployment) => deployment.status === 'Running' || deployment.status === 'RollingBack').length;
  const successfulCount = deployments.filter((deployment) => deployment.status === 'Succeeded').length;
  const failedCount = deployments.filter((deployment) => deployment.status === 'Failed' || deployment.status === 'VerificationFailed').length;

  function handleDelete(deployment: Deployment) {
    setConfirm({
      open: true,
      title: t('Delete Deployment'),
      message: t('Delete "{{title}}"? This removes only the deployment record and leaves linked checks, releases, and environments intact.', { title: deployment.title }),
      onConfirm: async () => {
        setConfirm((current) => ({ ...current, open: false }));
        try {
          await deleteDeployment(deployment.id);
          pushToast('warning', t('Deployment "{{title}}" deleted.', { title: deployment.title }));
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
          <h2>{t('Deployments')}</h2>
          <p className="text-dim view-subtitle">
            {t('First-class deployment records linking releases, environments, checks, approval, verification, rollback, and request-history evidence.')}
          </p>
        </div>
        <div className="view-actions">
          <RefreshButton onRefresh={load} title={t('Refresh deployments')} />
          {canManage && (
            <button className="btn btn-primary" onClick={() => navigate('/deployments/new')}>
              + {t('Deployment')}
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
          <span>{t('Total Deployments')}</span>
          <strong>{deployments.length}</strong>
        </div>
        <div className="card playbook-overview-card">
          <span>{t('Pending Approval')}</span>
          <strong>{pendingApprovalCount}</strong>
        </div>
        <div className="card playbook-overview-card">
          <span>{t('Running')}</span>
          <strong>{runningCount}</strong>
        </div>
        <div className="card playbook-overview-card">
          <span>{t('Succeeded')}</span>
          <strong>{successfulCount}</strong>
        </div>
        <div className="card playbook-overview-card">
          <span>{t('Failed / Verification Failed')}</span>
          <strong>{failedCount}</strong>
        </div>
      </div>

      <div className="card" style={{ padding: '1rem', marginBottom: '1rem' }}>
        <div className="playbook-filter-row">
          <input
            type="text"
            value={search}
            onChange={(event) => setSearch(event.target.value)}
            placeholder={t('Search by title, environment, source ref, summary, or ID...')}
          />
          <select value={statusFilter} onChange={(event) => setStatusFilter(event.target.value as typeof statusFilter)}>
            <option value="all">{t('All statuses')}</option>
            {DEPLOYMENT_STATUSES.map((status) => (
              <option key={status} value={status}>{status}</option>
            ))}
          </select>
          <select value={verificationFilter} onChange={(event) => setVerificationFilter(event.target.value as typeof verificationFilter)}>
            <option value="all">{t('All verification states')}</option>
            {VERIFICATION_STATUSES.map((status) => (
              <option key={status} value={status}>{status}</option>
            ))}
          </select>
          <select value={vesselFilter} onChange={(event) => setVesselFilter(event.target.value)}>
            <option value="all">{t('All vessels')}</option>
            {vessels.map((vessel) => (
              <option key={vessel.id} value={vessel.id}>{vessel.name}</option>
            ))}
          </select>
        </div>
      </div>

      {loading && deployments.length === 0 ? (
        <p className="text-dim">{t('Loading...')}</p>
      ) : filtered.length === 0 ? (
        <div className="playbook-empty-state">
          <strong>{t('No deployments match the current filters.')}</strong>
          <span>{canManage ? t('Create a deployment from an environment or release to track approval, execution, verification, and rollback in one record.') : t('Ask a tenant administrator to create and manage deployment records.')}</span>
        </div>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>{t('Deployment')}</th>
                <th>{t('Status')}</th>
                <th>{t('Verification')}</th>
                <th>{t('Vessel')}</th>
                <th>{t('Environment')}</th>
                <th>{t('Release')}</th>
                <th>{t('Checks')}</th>
                <th>{t('Last Updated')}</th>
                <th className="text-right">{t('Actions')}</th>
              </tr>
            </thead>
            <tbody>
              {filtered.map((deployment) => (
                <tr key={deployment.id} className="clickable" onClick={() => navigate(`/deployments/${deployment.id}`)}>
                  <td>
                    <strong>{deployment.title}</strong>
                    <div className="text-dim" style={{ marginTop: '0.2rem' }}>
                      {deployment.sourceRef || t('No source ref')} {deployment.approvalRequired ? `• ${t('Approval required')}` : ''}
                    </div>
                    <div className="mono text-dim" style={{ fontSize: '0.78rem' }}>{deployment.id}</div>
                    {deployment.summary && (
                      <div className="text-dim" style={{ marginTop: '0.2rem' }}>{deployment.summary}</div>
                    )}
                  </td>
                  <td><StatusBadge status={deployment.status} /></td>
                  <td><StatusBadge status={deployment.verificationStatus} /></td>
                  <td className="text-dim">{deployment.vesselId ? (vesselMap.get(deployment.vesselId) || deployment.vesselId) : '-'}</td>
                  <td className="text-dim">{deployment.environmentId ? (environmentMap.get(deployment.environmentId) || deployment.environmentName || deployment.environmentId) : (deployment.environmentName || '-')}</td>
                  <td className="text-dim">{deployment.releaseId ? (releaseMap.get(deployment.releaseId) || deployment.releaseId) : '-'}</td>
                  <td className="text-dim">{deployment.checkRunIds.length}</td>
                  <td className="text-dim" title={formatDateTime(deployment.lastUpdateUtc)}>
                    {formatRelativeTime(deployment.lastUpdateUtc)}
                  </td>
                  <td className="text-right" onClick={(event) => event.stopPropagation()}>
                    <ActionMenu
                      id={`deployment-${deployment.id}`}
                      items={[
                        { label: 'Open', onClick: () => navigate(`/deployments/${deployment.id}`) },
                        { label: 'View JSON', onClick: () => setJsonData({ open: true, title: deployment.title, data: deployment }) },
                        ...(canManage ? [{ label: 'Delete', danger: true as const, onClick: () => handleDelete(deployment) }] : []),
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
