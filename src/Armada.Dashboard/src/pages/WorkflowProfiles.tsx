import { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { deleteWorkflowProfile, listWorkflowProfiles } from '../api/client';
import type { WorkflowProfile } from '../types/models';
import { useAuth } from '../context/AuthContext';
import { useLocale } from '../context/LocaleContext';
import { useNotifications } from '../context/NotificationContext';
import ActionMenu from '../components/shared/ActionMenu';
import ConfirmDialog from '../components/shared/ConfirmDialog';
import ErrorModal from '../components/shared/ErrorModal';
import JsonViewer from '../components/shared/JsonViewer';
import RefreshButton from '../components/shared/RefreshButton';
import StatusBadge from '../components/shared/StatusBadge';

function countProfileCapabilities(profile: WorkflowProfile): number {
  const commands = [
    profile.lintCommand,
    profile.buildCommand,
    profile.unitTestCommand,
    profile.integrationTestCommand,
    profile.e2eTestCommand,
    profile.packageCommand,
    profile.publishArtifactCommand,
    profile.releaseVersioningCommand,
    profile.changelogGenerationCommand,
  ].filter((value) => !!value).length;

  const environmentCommands = profile.environments.reduce((total, environment) => total
    + (environment.deployCommand ? 1 : 0)
    + (environment.rollbackCommand ? 1 : 0)
    + (environment.smokeTestCommand ? 1 : 0)
    + (environment.healthCheckCommand ? 1 : 0), 0);

  return commands + environmentCommands;
}

export default function WorkflowProfiles() {
  const navigate = useNavigate();
  const { isAdmin, isTenantAdmin } = useAuth();
  const { t, formatDateTime, formatRelativeTime } = useLocale();
  const { pushToast } = useNotifications();
  const [profiles, setProfiles] = useState<WorkflowProfile[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [search, setSearch] = useState('');
  const [scopeFilter, setScopeFilter] = useState<'all' | 'Global' | 'Fleet' | 'Vessel'>('all');
  const [statusFilter, setStatusFilter] = useState<'all' | 'active' | 'inactive'>('all');
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
      const result = await listWorkflowProfiles({ pageSize: 9999 });
      setProfiles(result.objects || []);
      setError('');
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Failed to load workflow profiles.'));
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    load();
  }, []);

  const filtered = useMemo(() => profiles.filter((profile) => {
    const matchesSearch = search.trim().length === 0
      || profile.name.toLowerCase().includes(search.toLowerCase())
      || (profile.description || '').toLowerCase().includes(search.toLowerCase())
      || profile.id.toLowerCase().includes(search.toLowerCase());

    const matchesScope = scopeFilter === 'all' || profile.scope === scopeFilter;
    const matchesStatus = statusFilter === 'all'
      || (statusFilter === 'active' && profile.active)
      || (statusFilter === 'inactive' && !profile.active);

    return matchesSearch && matchesScope && matchesStatus;
  }), [profiles, scopeFilter, search, statusFilter]);

  const defaultCount = profiles.filter((profile) => profile.isDefault).length;
  const activeCount = profiles.filter((profile) => profile.active).length;

  function handleDelete(profile: WorkflowProfile) {
    setConfirm({
      open: true,
      title: t('Delete Workflow Profile'),
      message: t('Delete "{{name}}"? Existing check runs remain, but this profile will no longer be available.', { name: profile.name }),
      onConfirm: async () => {
        setConfirm((current) => ({ ...current, open: false }));
        try {
          await deleteWorkflowProfile(profile.id);
          pushToast('warning', t('Workflow profile "{{name}}" deleted.', { name: profile.name }));
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
          <h2>{t('Workflow Profiles')}</h2>
          <p className="text-dim view-subtitle">
            {t('Tenant-scoped command profiles that tell Armada how each project builds, tests, packages, releases, deploys, and verifies itself.')}
          </p>
        </div>
        <div className="view-actions">
          <RefreshButton onRefresh={load} title={t('Refresh workflow profiles')} />
          {canManage && (
            <button className="btn btn-primary" onClick={() => navigate('/workflow-profiles/new')}>
              + {t('Workflow Profile')}
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
          <span>{t('Total Profiles')}</span>
          <strong>{profiles.length}</strong>
        </div>
        <div className="card playbook-overview-card">
          <span>{t('Active')}</span>
          <strong>{activeCount}</strong>
        </div>
        <div className="card playbook-overview-card">
          <span>{t('Defaults')}</span>
          <strong>{defaultCount}</strong>
        </div>
        <div className="card playbook-overview-card">
          <span>{t('Environment Targets')}</span>
          <strong>{profiles.reduce((total, profile) => total + profile.environments.length, 0)}</strong>
        </div>
      </div>

      <div className="card" style={{ padding: '1rem', marginBottom: '1rem' }}>
        <div className="playbook-filter-row">
          <input
            type="text"
            value={search}
            onChange={(event) => setSearch(event.target.value)}
            placeholder={t('Search by name, description, or ID...')}
          />
          <select value={scopeFilter} onChange={(event) => setScopeFilter(event.target.value as typeof scopeFilter)}>
            <option value="all">{t('All scopes')}</option>
            <option value="Global">{t('Global')}</option>
            <option value="Fleet">{t('Fleet')}</option>
            <option value="Vessel">{t('Vessel')}</option>
          </select>
          <select value={statusFilter} onChange={(event) => setStatusFilter(event.target.value as typeof statusFilter)}>
            <option value="all">{t('All statuses')}</option>
            <option value="active">{t('Active only')}</option>
            <option value="inactive">{t('Inactive only')}</option>
          </select>
        </div>
      </div>

      {loading && profiles.length === 0 ? (
        <p className="text-dim">{t('Loading...')}</p>
      ) : filtered.length === 0 ? (
        <div className="playbook-empty-state">
          <strong>{t('No workflow profiles match the current filters.')}</strong>
          <span>{canManage ? t('Create a workflow profile to teach Armada how a project actually builds and ships.') : t('Ask a tenant administrator to define workflow profiles for shared build and deploy actions.')}</span>
        </div>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>{t('Profile')}</th>
                <th>{t('Scope')}</th>
                <th>{t('Capabilities')}</th>
                <th>{t('Targets')}</th>
                <th>{t('Status')}</th>
                <th>{t('Last Updated')}</th>
                <th className="text-right">{t('Actions')}</th>
              </tr>
            </thead>
            <tbody>
              {filtered.map((profile) => (
                <tr key={profile.id} className="clickable" onClick={() => navigate(`/workflow-profiles/${profile.id}`)}>
                  <td>
                    <strong>{profile.name}</strong>
                    <div className="mono text-dim" style={{ fontSize: '0.78rem' }}>{profile.id}</div>
                    {profile.description && (
                      <div className="text-dim" style={{ marginTop: '0.2rem' }}>{profile.description}</div>
                    )}
                  </td>
                  <td>
                    <StatusBadge status={profile.scope} />
                    {profile.isDefault && <div className="text-dim" style={{ marginTop: '0.25rem' }}>{t('Default')}</div>}
                  </td>
                  <td className="text-dim">{countProfileCapabilities(profile)} {t('commands')}</td>
                  <td className="text-dim">{profile.environments.length} {t('environments')}</td>
                  <td><StatusBadge status={profile.active ? 'Active' : 'Inactive'} /></td>
                  <td className="text-dim" title={formatDateTime(profile.lastUpdateUtc)}>{formatRelativeTime(profile.lastUpdateUtc)}</td>
                  <td className="text-right" onClick={(event) => event.stopPropagation()}>
                    <ActionMenu
                      id={`workflow-profile-${profile.id}`}
                      items={[
                        { label: 'Open', onClick: () => navigate(`/workflow-profiles/${profile.id}`) },
                        { label: 'View JSON', onClick: () => setJsonData({ open: true, title: profile.name, data: profile }) },
                        ...(canManage ? [{ label: 'Delete', danger: true as const, onClick: () => handleDelete(profile) }] : []),
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
