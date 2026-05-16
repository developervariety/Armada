import { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { createEnvironment, deleteEnvironment, listEnvironments, listVessels } from '../api/client';
import type { DeploymentEnvironment, EnvironmentKind, Vessel } from '../types/models';
import { useAuth } from '../context/AuthContext';
import { useLocale } from '../context/LocaleContext';
import { useNotifications } from '../context/NotificationContext';
import ActionMenu from '../components/shared/ActionMenu';
import ConfirmDialog from '../components/shared/ConfirmDialog';
import ErrorModal from '../components/shared/ErrorModal';
import JsonViewer from '../components/shared/JsonViewer';
import RefreshButton from '../components/shared/RefreshButton';
import { buildEnvironmentDuplicatePayload } from '../lib/duplicates';

const ENVIRONMENT_KINDS: EnvironmentKind[] = ['Development', 'Test', 'Staging', 'Production', 'CustomerHosted', 'Custom'];

export default function Environments() {
  const navigate = useNavigate();
  const { isAdmin, isTenantAdmin } = useAuth();
  const { t, formatDateTime, formatRelativeTime } = useLocale();
  const { pushToast } = useNotifications();

  const [environments, setEnvironments] = useState<DeploymentEnvironment[]>([]);
  const [vessels, setVessels] = useState<Vessel[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [search, setSearch] = useState('');
  const [kindFilter, setKindFilter] = useState<'all' | EnvironmentKind>('all');
  const [vesselFilter, setVesselFilter] = useState('all');
  const [activeFilter, setActiveFilter] = useState<'all' | 'active' | 'inactive'>('all');
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
      const [environmentResult, vesselResult] = await Promise.all([
        listEnvironments({ pageSize: 9999 }),
        listVessels({ pageSize: 9999 }),
      ]);
      setEnvironments(environmentResult.objects || []);
      setVessels(vesselResult.objects || []);
      setError('');
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Failed to load environments.'));
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    load();
  }, []);

  const vesselMap = useMemo(() => new Map(vessels.map((vessel) => [vessel.id, vessel.name])), [vessels]);

  const filtered = useMemo(() => environments.filter((environment) => {
    const normalizedSearch = search.trim().toLowerCase();
    const matchesSearch = normalizedSearch.length === 0
      || environment.name.toLowerCase().includes(normalizedSearch)
      || (environment.description || '').toLowerCase().includes(normalizedSearch)
      || (environment.baseUrl || '').toLowerCase().includes(normalizedSearch)
      || (environment.configurationSource || '').toLowerCase().includes(normalizedSearch)
      || environment.id.toLowerCase().includes(normalizedSearch);

    const matchesKind = kindFilter === 'all' || environment.kind === kindFilter;
    const matchesVessel = vesselFilter === 'all' || environment.vesselId === vesselFilter;
    const matchesActive = activeFilter === 'all'
      || (activeFilter === 'active' && environment.active)
      || (activeFilter === 'inactive' && !environment.active);

    return matchesSearch && matchesKind && matchesVessel && matchesActive;
  }), [activeFilter, environments, kindFilter, search, vesselFilter]);

  const activeCount = environments.filter((environment) => environment.active).length;
  const defaultCount = environments.filter((environment) => environment.isDefault).length;
  const approvalCount = environments.filter((environment) => environment.requiresApproval).length;

  function handleDelete(environment: DeploymentEnvironment) {
    setConfirm({
      open: true,
      title: t('Delete Environment'),
      message: t('Delete "{{name}}"? This removes only the environment record.', { name: environment.name }),
      onConfirm: async () => {
        setConfirm((current) => ({ ...current, open: false }));
        try {
          await deleteEnvironment(environment.id);
          pushToast('warning', t('Environment "{{name}}" deleted.', { name: environment.name }));
          await load();
        } catch (err: unknown) {
          setError(err instanceof Error ? err.message : t('Delete failed.'));
        }
      },
    });
  }

  async function handleDuplicate(environment: DeploymentEnvironment) {
    try {
      const created = await createEnvironment(buildEnvironmentDuplicatePayload(environment));
      pushToast('success', t('Environment "{{name}}" duplicated.', { name: created.name }));
      navigate(`/environments/${created.id}`);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Duplicate failed.'));
    }
  }

  return (
    <div>
      <div className="view-header">
        <div>
          <h2>{t('Environments')}</h2>
          <p className="text-dim view-subtitle">
            {t('Named deployment targets for vessels, with URLs, configuration sources, approval requirements, and operator notes.')}
          </p>
        </div>
        <div className="view-actions">
          <RefreshButton onRefresh={load} title={t('Refresh environments')} />
          {canManage && (
            <button className="btn btn-primary" onClick={() => navigate('/environments/new')}>
              + {t('Environment')}
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
          <span>{t('Total Environments')}</span>
          <strong>{environments.length}</strong>
        </div>
        <div className="card playbook-overview-card">
          <span>{t('Active')}</span>
          <strong>{activeCount}</strong>
        </div>
        <div className="card playbook-overview-card">
          <span>{t('Default Targets')}</span>
          <strong>{defaultCount}</strong>
        </div>
        <div className="card playbook-overview-card">
          <span>{t('Require Approval')}</span>
          <strong>{approvalCount}</strong>
        </div>
      </div>

      <div className="card" style={{ padding: '1rem', marginBottom: '1rem' }}>
        <div className="playbook-filter-row">
          <input
            type="text"
            value={search}
            onChange={(event) => setSearch(event.target.value)}
            placeholder={t('Search by name, description, base URL, configuration source, or ID...')}
          />
          <select value={kindFilter} onChange={(event) => setKindFilter(event.target.value as typeof kindFilter)}>
            <option value="all">{t('All kinds')}</option>
            {ENVIRONMENT_KINDS.map((kind) => (
              <option key={kind} value={kind}>{kind}</option>
            ))}
          </select>
          <select value={vesselFilter} onChange={(event) => setVesselFilter(event.target.value)}>
            <option value="all">{t('All vessels')}</option>
            {vessels.map((vessel) => (
              <option key={vessel.id} value={vessel.id}>{vessel.name}</option>
            ))}
          </select>
          <select value={activeFilter} onChange={(event) => setActiveFilter(event.target.value as typeof activeFilter)}>
            <option value="all">{t('All states')}</option>
            <option value="active">{t('Active')}</option>
            <option value="inactive">{t('Inactive')}</option>
          </select>
        </div>
      </div>

      {loading && environments.length === 0 ? (
        <p className="text-dim">{t('Loading...')}</p>
      ) : filtered.length === 0 ? (
        <div className="playbook-empty-state">
          <strong>{t('No environments match the current filters.')}</strong>
          <span>{canManage ? t('Create an environment to capture deployment metadata, URLs, and approval rules for a vessel.') : t('Ask a tenant administrator to create and manage environment records.')}</span>
        </div>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>{t('Environment')}</th>
                <th>{t('Kind')}</th>
                <th>{t('Vessel')}</th>
                <th>{t('Base URL')}</th>
                <th>{t('Health')}</th>
                <th>{t('Policy')}</th>
                <th>{t('Last Updated')}</th>
                <th className="text-right">{t('Actions')}</th>
              </tr>
            </thead>
            <tbody>
              {filtered.map((environment) => (
                <tr key={environment.id} className="clickable" onClick={() => navigate(`/environments/${environment.id}`)}>
                  <td>
                    <strong>{environment.name}</strong>
                    <div className="text-dim" style={{ marginTop: '0.2rem' }}>
                      {environment.isDefault ? t('Default target') : t('Non-default')} {environment.active ? '• ' + t('Active') : '• ' + t('Inactive')}
                    </div>
                    <div className="mono text-dim" style={{ fontSize: '0.78rem' }}>{environment.id}</div>
                    {environment.description && (
                      <div className="text-dim" style={{ marginTop: '0.2rem' }}>{environment.description}</div>
                    )}
                  </td>
                  <td className="text-dim">{environment.kind}</td>
                  <td className="text-dim">{environment.vesselId ? (vesselMap.get(environment.vesselId) || environment.vesselId) : '-'}</td>
                  <td className="text-dim">{environment.baseUrl || '-'}</td>
                  <td className="text-dim">{environment.healthEndpoint || '-'}</td>
                  <td className="text-dim">
                    {environment.requiresApproval ? t('Approval required') : t('Self-service')}
                  </td>
                  <td className="text-dim" title={formatDateTime(environment.lastUpdateUtc)}>
                    {formatRelativeTime(environment.lastUpdateUtc)}
                  </td>
                  <td className="text-right" onClick={(event) => event.stopPropagation()}>
                    <ActionMenu
                      id={`environment-${environment.id}`}
                      items={[
                        { label: 'Open', onClick: () => navigate(`/environments/${environment.id}`) },
                        ...(canManage ? [{ label: 'Duplicate', onClick: () => void handleDuplicate(environment) }] : []),
                        { label: 'View JSON', onClick: () => setJsonData({ open: true, title: environment.name, data: environment }) },
                        ...(canManage ? [{ label: 'Delete', danger: true as const, onClick: () => handleDelete(environment) }] : []),
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
