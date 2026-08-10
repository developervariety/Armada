import { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { deleteProjectProfile, listProjectProfiles } from '../api/client';
import type { ProjectProfile } from '../types/models';
import { useAuth } from '../context/AuthContext';
import { useLocale } from '../context/LocaleContext';
import { useNotifications } from '../context/NotificationContext';
import ActionMenu from '../components/shared/ActionMenu';
import ConfirmDialog from '../components/shared/ConfirmDialog';
import ErrorModal from '../components/shared/ErrorModal';
import JsonViewer from '../components/shared/JsonViewer';
import RefreshButton from '../components/shared/RefreshButton';
import StatusBadge from '../components/shared/StatusBadge';

export default function ProjectProfiles() {
  const navigate = useNavigate();
  const { isAdmin, isTenantAdmin } = useAuth();
  const { t, formatDateTime, formatRelativeTime } = useLocale();
  const { pushToast } = useNotifications();
  const [profiles, setProfiles] = useState<ProjectProfile[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [search, setSearch] = useState('');
  const [scopeFilter, setScopeFilter] = useState<'all' | 'Global' | 'Fleet' | 'Vessel'>('all');
  const [statusFilter, setStatusFilter] = useState<'all' | 'active' | 'inactive'>('all');
  const [colFilters, setColFilters] = useState({ name: '' });
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
      const result = await listProjectProfiles({ pageSize: 9999 });
      setProfiles(result.objects || []);
      setError('');
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Failed to load project profiles.'));
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

    const matchesColumns = !colFilters.name || (profile.name ?? '').toLowerCase().includes(colFilters.name.toLowerCase());

    return matchesSearch && matchesScope && matchesStatus && matchesColumns;
  }), [profiles, scopeFilter, search, statusFilter, colFilters]);

  const defaultCount = profiles.filter((profile) => profile.isDefault).length;
  const activeCount = profiles.filter((profile) => profile.active).length;
  const overrideCount = profiles.reduce((total, profile) => total + (profile.personaOverrides?.length || 0), 0);

  function handleDelete(profile: ProjectProfile) {
    setConfirm({
      open: true,
      title: t('Delete Project Profile'),
      message: t('Delete "{{name}}"? Projects using it will fall back to their fleet or global profile.', { name: profile.name }),
      onConfirm: async () => {
        setConfirm((current) => ({ ...current, open: false }));
        try {
          await deleteProjectProfile(profile.id);
          pushToast('warning', t('Project profile "{{name}}" deleted.', { name: profile.name }));
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
          <h2>{t('Project Profiles')}</h2>
          <p className="text-dim view-subtitle">
            {t('Per-project customization that binds a pipeline, workflow profile, persona prompt overrides, and skills, resolved global to fleet to vessel.')}
          </p>
        </div>
        <div className="view-actions">
          <RefreshButton onRefresh={load} title={t('Refresh project profiles')} />
          {canManage && (
            <button className="btn btn-primary" onClick={() => navigate('/project-profiles/new')}>
              + {t('Project Profile')}
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
          <span>{t('Persona Overrides')}</span>
          <strong>{overrideCount}</strong>
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
          <strong>{t('No project profiles match the current filters.')}</strong>
          <span>{canManage ? t('Create a project profile to customize personas, pipeline, and skills for a project.') : t('Ask a tenant administrator to define project profiles.')}</span>
        </div>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>{t('Profile')}</th>
                <th>{t('Scope')}</th>
                <th>{t('Overrides')}</th>
                <th>{t('Skills')}</th>
                <th>{t('Status')}</th>
                <th>{t('Last Updated')}</th>
                <th className="text-right">{t('Actions')}</th>
              </tr>
              <tr className="column-filter-row">
                <td><input type="text" className="col-filter" value={colFilters.name} onChange={e => setColFilters(f => ({ ...f, name: e.target.value }))} placeholder={t('Filter...')} /></td>
                <td></td>
                <td></td>
                <td></td>
                <td></td>
                <td></td>
                <td></td>
              </tr>
            </thead>
            <tbody>
              {filtered.map((profile) => (
                <tr key={profile.id} className="clickable" onClick={() => navigate(`/project-profiles/${profile.id}`)}>
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
                  <td className="text-dim">{profile.personaOverrides?.length || 0} {t('personas')}</td>
                  <td className="text-dim">{profile.skills?.length || 0} {t('skills')}</td>
                  <td><StatusBadge status={profile.active ? 'Active' : 'Inactive'} /></td>
                  <td className="text-dim" title={formatDateTime(profile.lastUpdateUtc)}>{formatRelativeTime(profile.lastUpdateUtc)}</td>
                  <td className="text-right" onClick={(event) => event.stopPropagation()}>
                    <ActionMenu
                      id={`project-profile-${profile.id}`}
                      items={[
                        { label: 'Open', onClick: () => navigate(`/project-profiles/${profile.id}`) },
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
