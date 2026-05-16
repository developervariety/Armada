import { useEffect, useMemo, useState } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import {
  createRunbook,
  deleteRunbook,
  listEnvironments,
  listRunbookExecutions,
  listRunbooks,
  listWorkflowProfiles,
} from '../api/client';
import type {
  DeploymentEnvironment,
  Runbook,
  RunbookExecution,
  RunbookExecutionStartRequest,
  WorkflowProfile,
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
import { buildRunbookDuplicatePayload } from '../lib/duplicates';

interface RunbookPageState {
  prefillExecution?: Partial<RunbookExecutionStartRequest>;
}

export default function Runbooks() {
  const navigate = useNavigate();
  const location = useLocation();
  const { isAdmin, isTenantAdmin } = useAuth();
  const { t, formatDateTime, formatRelativeTime } = useLocale();
  const { pushToast } = useNotifications();

  const [runbooks, setRunbooks] = useState<Runbook[]>([]);
  const [executions, setExecutions] = useState<RunbookExecution[]>([]);
  const [profiles, setProfiles] = useState<WorkflowProfile[]>([]);
  const [environments, setEnvironments] = useState<DeploymentEnvironment[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [search, setSearch] = useState('');
  const [activeFilter, setActiveFilter] = useState<'all' | 'active' | 'inactive'>('all');
  const [jsonData, setJsonData] = useState<{ open: boolean; title: string; data: unknown }>({ open: false, title: '', data: null });
  const [confirm, setConfirm] = useState<{ open: boolean; title: string; message: string; onConfirm: () => void }>({
    open: false,
    title: '',
    message: '',
    onConfirm: () => {},
  });

  const canManage = isAdmin || isTenantAdmin;
  const carryState = (location.state as RunbookPageState | null) || null;

  async function load() {
    try {
      setLoading(true);
      const [runbookResult, executionResult, profileResult, environmentResult] = await Promise.all([
        listRunbooks({ pageSize: 9999 }),
        listRunbookExecutions({ pageSize: 9999 }),
        listWorkflowProfiles({ pageSize: 9999 }),
        listEnvironments({ pageSize: 9999 }),
      ]);
      setRunbooks(runbookResult.objects || []);
      setExecutions(executionResult.objects || []);
      setProfiles(profileResult.objects || []);
      setEnvironments(environmentResult.objects || []);
      setError('');
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Failed to load runbooks.'));
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    void load();
  }, []);

  const profileMap = useMemo(() => new Map(profiles.map((profile) => [profile.id, profile.name])), [profiles]);
  const environmentMap = useMemo(() => new Map(environments.map((environment) => [environment.id, environment.name])), [environments]);
  const executionCounts = useMemo(() => {
    const counts = new Map<string, { total: number; running: number }>();
    for (const execution of executions) {
      const current = counts.get(execution.runbookId) || { total: 0, running: 0 };
      current.total += 1;
      if (execution.status === 'Running') current.running += 1;
      counts.set(execution.runbookId, current);
    }
    return counts;
  }, [executions]);

  const filtered = useMemo(() => runbooks.filter((runbook) => {
    const normalizedSearch = search.trim().toLowerCase();
    const matchesSearch = normalizedSearch.length === 0
      || runbook.title.toLowerCase().includes(normalizedSearch)
      || runbook.fileName.toLowerCase().includes(normalizedSearch)
      || (runbook.description || '').toLowerCase().includes(normalizedSearch)
      || (runbook.environmentName || '').toLowerCase().includes(normalizedSearch)
      || runbook.id.toLowerCase().includes(normalizedSearch);
    const matchesActive = activeFilter === 'all'
      || (activeFilter === 'active' && runbook.active)
      || (activeFilter === 'inactive' && !runbook.active);
    return matchesSearch && matchesActive;
  }), [activeFilter, runbooks, search]);

  function handleDelete(runbook: Runbook) {
    setConfirm({
      open: true,
      title: t('Delete Runbook'),
      message: t('Delete "{{title}}"? This removes the runbook definition but does not touch deployments, incidents, or completed check runs.', { title: runbook.title }),
      onConfirm: async () => {
        setConfirm((current) => ({ ...current, open: false }));
        try {
          await deleteRunbook(runbook.id);
          pushToast('warning', t('Runbook "{{title}}" deleted.', { title: runbook.title }));
          await load();
        } catch (err: unknown) {
          setError(err instanceof Error ? err.message : t('Delete failed.'));
        }
      },
    });
  }

  async function handleDuplicate(runbook: Runbook) {
    try {
      const created = await createRunbook(buildRunbookDuplicatePayload(runbook));
      pushToast('success', t('Runbook "{{title}}" duplicated.', { title: created.title }));
      navigate(`/runbooks/${created.id}`, { state: carryState });
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Duplicate failed.'));
    }
  }

  return (
    <div>
      <div className="view-header">
        <div>
          <h2>{t('Runbooks')}</h2>
          <p className="text-dim view-subtitle">
            {t('Playbook-backed operational runbooks with bound workflow profiles, environments, parameters, step tracking, and execution history.')}
          </p>
        </div>
        <div className="view-actions">
          <RefreshButton onRefresh={load} title={t('Refresh runbooks')} />
          {canManage && (
            <button className="btn btn-primary" onClick={() => navigate('/runbooks/new', { state: carryState })}>
              + {t('Runbook')}
            </button>
          )}
        </div>
      </div>

      {carryState?.prefillExecution && (
        <div className="alert" style={{ marginBottom: '1rem' }}>
          {t('An incident or deployment handed off a prefilled runbook execution context. Open a runbook to start the execution with those defaults.')}
        </div>
      )}

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
          <span>{t('Total Runbooks')}</span>
          <strong>{runbooks.length}</strong>
        </div>
        <div className="card playbook-overview-card">
          <span>{t('Active')}</span>
          <strong>{runbooks.filter((runbook) => runbook.active).length}</strong>
        </div>
        <div className="card playbook-overview-card">
          <span>{t('Executions')}</span>
          <strong>{executions.length}</strong>
        </div>
        <div className="card playbook-overview-card">
          <span>{t('Running')}</span>
          <strong>{executions.filter((execution) => execution.status === 'Running').length}</strong>
        </div>
      </div>

      <div className="card" style={{ padding: '1rem', marginBottom: '1rem' }}>
        <div className="playbook-filter-row">
          <input
            type="text"
            value={search}
            onChange={(event) => setSearch(event.target.value)}
            placeholder={t('Search by title, file name, description, environment, or ID...')}
          />
          <select value={activeFilter} onChange={(event) => setActiveFilter(event.target.value as typeof activeFilter)}>
            <option value="all">{t('All states')}</option>
            <option value="active">{t('Active')}</option>
            <option value="inactive">{t('Inactive')}</option>
          </select>
        </div>
      </div>

      {loading && runbooks.length === 0 ? (
        <p className="text-dim">{t('Loading...')}</p>
      ) : filtered.length === 0 ? (
        <div className="playbook-empty-state">
          <strong>{t('No runbooks match the current filters.')}</strong>
          <span>{canManage ? t('Create a runbook to guide release, deploy, rollback, migration, or incident work step by step.') : t('Ask a tenant administrator to create and manage runbooks.')}</span>
        </div>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>{t('Runbook')}</th>
                <th>{t('Binding')}</th>
                <th>{t('Steps')}</th>
                <th>{t('Executions')}</th>
                <th>{t('Last Updated')}</th>
                <th className="text-right">{t('Actions')}</th>
              </tr>
            </thead>
            <tbody>
              {filtered.map((runbook) => {
                const counts = executionCounts.get(runbook.id) || { total: 0, running: 0 };
                return (
                  <tr key={runbook.id} className="clickable" onClick={() => navigate(`/runbooks/${runbook.id}`, { state: carryState })}>
                    <td>
                      <strong>{runbook.title}</strong>
                      <div className="text-dim" style={{ marginTop: '0.2rem' }}>
                        {runbook.fileName} {runbook.active ? `• ${t('Active')}` : `• ${t('Inactive')}`}
                      </div>
                      <div className="mono text-dim" style={{ fontSize: '0.78rem' }}>{runbook.id}</div>
                      {runbook.description && (
                        <div className="text-dim" style={{ marginTop: '0.2rem' }}>{runbook.description}</div>
                      )}
                    </td>
                    <td className="text-dim">
                      <div>{runbook.workflowProfileId ? (profileMap.get(runbook.workflowProfileId) || runbook.workflowProfileId) : t('No workflow profile')}</div>
                      <div>{runbook.environmentId ? (environmentMap.get(runbook.environmentId) || runbook.environmentName || runbook.environmentId) : (runbook.environmentName || t('No environment'))}</div>
                      <div>{runbook.defaultCheckType || t('No default check')}</div>
                    </td>
                    <td className="text-dim">{runbook.steps.length} {t('steps')} • {runbook.parameters.length} {t('parameters')}</td>
                    <td className="text-dim">
                      <div>{counts.total} {t('total')}</div>
                      <div>{counts.running} {t('running')}</div>
                    </td>
                    <td className="text-dim" title={formatDateTime(runbook.lastUpdateUtc)}>{formatRelativeTime(runbook.lastUpdateUtc)}</td>
                    <td className="text-right" onClick={(event) => event.stopPropagation()}>
                      <ActionMenu
                        id={`runbook-${runbook.id}`}
                        items={[
                        { label: 'Open', onClick: () => navigate(`/runbooks/${runbook.id}`, { state: carryState }) },
                        ...(canManage ? [{ label: 'Duplicate', onClick: () => void handleDuplicate(runbook) }] : []),
                        { label: 'View JSON', onClick: () => setJsonData({ open: true, title: runbook.title, data: runbook }) },
                        ...(counts.running > 0 ? [{ label: `Running: ${counts.running}`, onClick: () => navigate(`/runbooks/${runbook.id}`, { state: carryState }) }] : []),
                        ...(canManage ? [{ label: 'Delete', danger: true as const, onClick: () => handleDelete(runbook) }] : []),
                        ]}
                      />
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
