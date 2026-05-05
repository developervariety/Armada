import { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { deleteRelease, listReleases, listVessels, listWorkflowProfiles } from '../api/client';
import type { Release, ReleaseStatus, Vessel, WorkflowProfile } from '../types/models';
import { useAuth } from '../context/AuthContext';
import { useLocale } from '../context/LocaleContext';
import { useNotifications } from '../context/NotificationContext';
import ActionMenu from '../components/shared/ActionMenu';
import ConfirmDialog from '../components/shared/ConfirmDialog';
import ErrorModal from '../components/shared/ErrorModal';
import JsonViewer from '../components/shared/JsonViewer';
import RefreshButton from '../components/shared/RefreshButton';
import StatusBadge from '../components/shared/StatusBadge';

const RELEASE_STATUSES: ReleaseStatus[] = ['Draft', 'Candidate', 'Shipped', 'Failed', 'RolledBack'];

export default function Releases() {
  const navigate = useNavigate();
  const { isAdmin, isTenantAdmin } = useAuth();
  const { t, formatDateTime, formatRelativeTime } = useLocale();
  const { pushToast } = useNotifications();

  const [releases, setReleases] = useState<Release[]>([]);
  const [vessels, setVessels] = useState<Vessel[]>([]);
  const [profiles, setProfiles] = useState<WorkflowProfile[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [search, setSearch] = useState('');
  const [statusFilter, setStatusFilter] = useState<'all' | ReleaseStatus>('all');
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
      const [releaseResult, vesselResult, profileResult] = await Promise.all([
        listReleases({ pageSize: 9999 }),
        listVessels({ pageSize: 9999 }),
        listWorkflowProfiles({ pageSize: 9999 }),
      ]);
      setReleases(releaseResult.objects || []);
      setVessels(vesselResult.objects || []);
      setProfiles(profileResult.objects || []);
      setError('');
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Failed to load releases.'));
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    load();
  }, []);

  const vesselMap = useMemo(() => new Map(vessels.map((vessel) => [vessel.id, vessel.name])), [vessels]);
  const profileMap = useMemo(() => new Map(profiles.map((profile) => [profile.id, profile.name])), [profiles]);

  const filtered = useMemo(() => releases.filter((release) => {
    const normalizedSearch = search.trim().toLowerCase();
    const matchesSearch = normalizedSearch.length === 0
      || release.title.toLowerCase().includes(normalizedSearch)
      || (release.version || '').toLowerCase().includes(normalizedSearch)
      || (release.tagName || '').toLowerCase().includes(normalizedSearch)
      || (release.summary || '').toLowerCase().includes(normalizedSearch)
      || release.id.toLowerCase().includes(normalizedSearch);

    const matchesStatus = statusFilter === 'all' || release.status === statusFilter;
    const matchesVessel = vesselFilter === 'all' || release.vesselId === vesselFilter;
    return matchesSearch && matchesStatus && matchesVessel;
  }), [releases, search, statusFilter, vesselFilter]);

  const shippedCount = releases.filter((release) => release.status === 'Shipped').length;
  const candidateCount = releases.filter((release) => release.status === 'Candidate').length;
  const failedCount = releases.filter((release) => release.status === 'Failed' || release.status === 'RolledBack').length;

  function handleDelete(release: Release) {
    setConfirm({
      open: true,
      title: t('Delete Release'),
      message: t('Delete "{{title}}"? This removes the release record but does not delete linked work or artifacts on disk.', { title: release.title }),
      onConfirm: async () => {
        setConfirm((current) => ({ ...current, open: false }));
        try {
          await deleteRelease(release.id);
          pushToast('warning', t('Release "{{title}}" deleted.', { title: release.title }));
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
          <h2>{t('Releases')}</h2>
          <p className="text-dim view-subtitle">
            {t('First-class release records that bundle versions, notes, linked voyages and missions, structured checks, and derived artifacts.')}
          </p>
        </div>
        <div className="view-actions">
          <RefreshButton onRefresh={load} title={t('Refresh releases')} />
          {canManage && (
            <button className="btn btn-primary" onClick={() => navigate('/releases/new')}>
              + {t('Release')}
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
          <span>{t('Total Releases')}</span>
          <strong>{releases.length}</strong>
        </div>
        <div className="card playbook-overview-card">
          <span>{t('Shipped')}</span>
          <strong>{shippedCount}</strong>
        </div>
        <div className="card playbook-overview-card">
          <span>{t('Candidates')}</span>
          <strong>{candidateCount}</strong>
        </div>
        <div className="card playbook-overview-card">
          <span>{t('Failed / Rolled Back')}</span>
          <strong>{failedCount}</strong>
        </div>
      </div>

      <div className="card" style={{ padding: '1rem', marginBottom: '1rem' }}>
        <div className="playbook-filter-row">
          <input
            type="text"
            value={search}
            onChange={(event) => setSearch(event.target.value)}
            placeholder={t('Search by title, version, tag, summary, or ID...')}
          />
          <select value={statusFilter} onChange={(event) => setStatusFilter(event.target.value as typeof statusFilter)}>
            <option value="all">{t('All statuses')}</option>
            {RELEASE_STATUSES.map((status) => (
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

      {loading && releases.length === 0 ? (
        <p className="text-dim">{t('Loading...')}</p>
      ) : filtered.length === 0 ? (
        <div className="playbook-empty-state">
          <strong>{t('No releases match the current filters.')}</strong>
          <span>{canManage ? t('Create a draft release from voyages, missions, or checks to begin tracking what is shipping.') : t('Ask a tenant administrator to create and manage release records.')}</span>
        </div>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>{t('Release')}</th>
                <th>{t('Status')}</th>
                <th>{t('Vessel')}</th>
                <th>{t('Workflow')}</th>
                <th>{t('Linked Work')}</th>
                <th>{t('Published')}</th>
                <th>{t('Last Updated')}</th>
                <th className="text-right">{t('Actions')}</th>
              </tr>
            </thead>
            <tbody>
              {filtered.map((release) => (
                <tr key={release.id} className="clickable" onClick={() => navigate(`/releases/${release.id}`)}>
                  <td>
                    <strong>{release.title}</strong>
                    <div className="text-dim" style={{ marginTop: '0.2rem' }}>
                      {release.version || t('Unversioned')} {release.tagName ? `• ${release.tagName}` : ''}
                    </div>
                    <div className="mono text-dim" style={{ fontSize: '0.78rem' }}>{release.id}</div>
                    {release.summary && (
                      <div className="text-dim" style={{ marginTop: '0.2rem' }}>{release.summary}</div>
                    )}
                  </td>
                  <td><StatusBadge status={release.status} /></td>
                  <td className="text-dim">{release.vesselId ? (vesselMap.get(release.vesselId) || release.vesselId) : '-'}</td>
                  <td className="text-dim">{release.workflowProfileId ? (profileMap.get(release.workflowProfileId) || release.workflowProfileId) : t('Resolved default')}</td>
                  <td className="text-dim">
                    {`${release.voyageIds.length} ${t('voyages')}, ${release.missionIds.length} ${t('missions')}, ${release.checkRunIds.length} ${t('checks')}, ${release.artifacts.length} ${t('artifacts')}`}
                  </td>
                  <td className="text-dim" title={release.publishedUtc ? formatDateTime(release.publishedUtc) : ''}>
                    {release.publishedUtc ? formatRelativeTime(release.publishedUtc) : '-'}
                  </td>
                  <td className="text-dim" title={formatDateTime(release.lastUpdateUtc)}>
                    {formatRelativeTime(release.lastUpdateUtc)}
                  </td>
                  <td className="text-right" onClick={(event) => event.stopPropagation()}>
                    <ActionMenu
                      id={`release-${release.id}`}
                      items={[
                        { label: 'Open', onClick: () => navigate(`/releases/${release.id}`) },
                        { label: 'View JSON', onClick: () => setJsonData({ open: true, title: release.title, data: release }) },
                        ...(canManage ? [{ label: 'Delete', danger: true as const, onClick: () => handleDelete(release) }] : []),
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
