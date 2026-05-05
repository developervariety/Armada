import { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { deleteObjective, listFleets, listObjectives, listVessels } from '../api/client';
import type { Fleet, Objective, ObjectiveStatus, Vessel } from '../types/models';
import { useAuth } from '../context/AuthContext';
import { useLocale } from '../context/LocaleContext';
import { useNotifications } from '../context/NotificationContext';
import ActionMenu from '../components/shared/ActionMenu';
import ConfirmDialog from '../components/shared/ConfirmDialog';
import ErrorModal from '../components/shared/ErrorModal';
import JsonViewer from '../components/shared/JsonViewer';
import RefreshButton from '../components/shared/RefreshButton';
import StatusBadge from '../components/shared/StatusBadge';

const OBJECTIVE_STATUSES: ObjectiveStatus[] = ['Draft', 'Scoped', 'Planned', 'InProgress', 'Released', 'Deployed', 'Completed', 'Blocked', 'Cancelled'];

export default function Objectives() {
  const navigate = useNavigate();
  const { isAdmin, isTenantAdmin } = useAuth();
  const { t, formatDateTime, formatRelativeTime } = useLocale();
  const { pushToast } = useNotifications();

  const [objectives, setObjectives] = useState<Objective[]>([]);
  const [fleets, setFleets] = useState<Fleet[]>([]);
  const [vessels, setVessels] = useState<Vessel[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [search, setSearch] = useState('');
  const [statusFilter, setStatusFilter] = useState<'all' | ObjectiveStatus>('all');
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
      const [objectiveResult, fleetResult, vesselResult] = await Promise.all([
        listObjectives({ pageSize: 9999 }),
        listFleets({ pageSize: 9999 }),
        listVessels({ pageSize: 9999 }),
      ]);
      setObjectives(objectiveResult.objects || []);
      setFleets(fleetResult.objects || []);
      setVessels(vesselResult.objects || []);
      setError('');
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Failed to load objectives.'));
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    void load();
  }, []);

  const fleetMap = useMemo(() => new Map(fleets.map((fleet) => [fleet.id, fleet.name])), [fleets]);
  const vesselMap = useMemo(() => new Map(vessels.map((vessel) => [vessel.id, vessel.name])), [vessels]);

  const filtered = useMemo(() => objectives.filter((objective) => {
    const normalizedSearch = search.trim().toLowerCase();
    const matchesSearch = normalizedSearch.length === 0
      || objective.title.toLowerCase().includes(normalizedSearch)
      || (objective.description || '').toLowerCase().includes(normalizedSearch)
      || (objective.owner || '').toLowerCase().includes(normalizedSearch)
      || objective.tags.some((tag) => tag.toLowerCase().includes(normalizedSearch))
      || objective.id.toLowerCase().includes(normalizedSearch);
    const matchesStatus = statusFilter === 'all' || objective.status === statusFilter;
    const matchesVessel = vesselFilter === 'all' || objective.vesselIds.includes(vesselFilter);
    return matchesSearch && matchesStatus && matchesVessel;
  }), [objectives, search, statusFilter, vesselFilter]);

  const inFlightCount = objectives.filter((objective) => objective.status === 'Scoped' || objective.status === 'Planned' || objective.status === 'InProgress').length;
  const completedCount = objectives.filter((objective) => objective.status === 'Completed').length;
  const blockedCount = objectives.filter((objective) => objective.status === 'Blocked').length;

  function handleDelete(objective: Objective) {
    setConfirm({
      open: true,
      title: t('Delete Objective'),
      message: t('Delete "{{title}}"? This removes the objective record and its snapshot history, but leaves linked missions, releases, deployments, and incidents intact.', { title: objective.title }),
      onConfirm: async () => {
        setConfirm((current) => ({ ...current, open: false }));
        try {
          await deleteObjective(objective.id);
          pushToast('warning', t('Objective "{{title}}" deleted.', { title: objective.title }));
          await load();
        } catch (err: unknown) {
          setError(err instanceof Error ? err.message : t('Delete failed.'));
        }
      },
    });
  }

  function renderNames(ids: string[], sourceMap: Map<string, string>) {
    if (ids.length === 0) return t('None');
    return ids.slice(0, 2).map((id) => sourceMap.get(id) || id).join(', ') + (ids.length > 2 ? ` +${ids.length - 2}` : '');
  }

  return (
    <div>
      <div className="view-header">
        <div>
          <h2>{t('Objectives')}</h2>
          <p className="text-dim view-subtitle">
            {t('Capture scoped work, acceptance criteria, non-goals, rollout constraints, and linked delivery evidence before or alongside execution.')}
          </p>
        </div>
        <div className="view-actions">
          <RefreshButton onRefresh={load} title={t('Refresh objectives')} />
          {canManage && (
            <button className="btn btn-primary" onClick={() => navigate('/objectives/new')}>
              + {t('Objective')}
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
          <span>{t('Total Objectives')}</span>
          <strong>{objectives.length}</strong>
        </div>
        <div className="card playbook-overview-card">
          <span>{t('In Flight')}</span>
          <strong>{inFlightCount}</strong>
        </div>
        <div className="card playbook-overview-card">
          <span>{t('Completed')}</span>
          <strong>{completedCount}</strong>
        </div>
        <div className="card playbook-overview-card">
          <span>{t('Blocked')}</span>
          <strong>{blockedCount}</strong>
        </div>
      </div>

      <div className="card" style={{ padding: '1rem', marginBottom: '1rem' }}>
        <div className="playbook-filter-row">
          <input
            type="text"
            value={search}
            onChange={(event) => setSearch(event.target.value)}
            placeholder={t('Search by title, owner, tags, description, or ID...')}
          />
          <select value={statusFilter} onChange={(event) => setStatusFilter(event.target.value as typeof statusFilter)}>
            <option value="all">{t('All statuses')}</option>
            {OBJECTIVE_STATUSES.map((status) => (
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

      {loading && objectives.length === 0 ? (
        <p className="text-dim">{t('Loading...')}</p>
      ) : filtered.length === 0 ? (
        <div className="playbook-empty-state">
          <strong>{t('No objectives match the current filters.')}</strong>
          <span>{canManage ? t('Create an objective to capture scoped work, linked repositories, and release/deployment evidence.') : t('Ask a tenant administrator to create and manage objectives.')}</span>
        </div>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>{t('Objective')}</th>
                <th>{t('Status')}</th>
                <th>{t('Scope')}</th>
                <th>{t('Linked Delivery')}</th>
                <th>{t('Last Updated')}</th>
                <th className="text-right">{t('Actions')}</th>
              </tr>
            </thead>
            <tbody>
              {filtered.map((objective) => (
                <tr key={objective.id} className="clickable" onClick={() => navigate(`/objectives/${objective.id}`)}>
                  <td>
                    <strong>{objective.title}</strong>
                    <div className="text-dim" style={{ marginTop: '0.2rem' }}>
                      {(objective.owner || t('No owner'))}
                    </div>
                    <div className="mono text-dim" style={{ fontSize: '0.78rem' }}>{objective.id}</div>
                    {objective.description && (
                      <div className="text-dim" style={{ marginTop: '0.2rem' }}>{objective.description}</div>
                    )}
                  </td>
                  <td><StatusBadge status={objective.status} /></td>
                  <td className="text-dim">
                    {renderNames(objective.vesselIds, vesselMap)}
                    <div style={{ marginTop: '0.25rem' }}>{renderNames(objective.fleetIds, fleetMap)}</div>
                  </td>
                  <td className="text-dim">
                    {`${objective.planningSessionIds.length} ${t('planning')}, ${objective.voyageIds.length} ${t('voyages')}, ${objective.releaseIds.length} ${t('releases')}, ${objective.deploymentIds.length} ${t('deployments')}, ${objective.incidentIds.length} ${t('incidents')}`}
                  </td>
                  <td className="text-dim" title={formatDateTime(objective.lastUpdateUtc)}>
                    {formatRelativeTime(objective.lastUpdateUtc)}
                  </td>
                  <td className="text-right" onClick={(event) => event.stopPropagation()}>
                    <ActionMenu
                      id={`objective-${objective.id}`}
                      items={[
                        { label: 'Open', onClick: () => navigate(`/objectives/${objective.id}`) },
                        { label: 'View JSON', onClick: () => setJsonData({ open: true, title: objective.title, data: objective }) },
                        ...(canManage ? [{ label: 'Delete', danger: true as const, onClick: () => handleDelete(objective) }] : []),
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
