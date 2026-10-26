import { useCallback, useEffect, useRef, useState } from 'react';
import { useAuth } from '../context/AuthContext';
import { useLocale } from '../context/LocaleContext';
import { cancelFleetActionRun, createFleetAction, deleteFleetAction, listFleetActions, listFleetActionRuns, listAllPipelines, listAllVessels, startFleetActionRun, updateFleetAction } from '../api/client';
import type { FleetAction, FleetActionRun, FleetActionRunRequest, FleetActionUpsertRequest, Pipeline, Vessel } from '../types/models';
import PageHeader from '../components/shared/PageHeader';
import RefreshButton from '../components/shared/RefreshButton';
import ConfirmDialog from '../components/shared/ConfirmDialog';
import MissionActionCard from '../components/fleetActions/MissionActionCard';
import MissionActionEditor from '../components/fleetActions/MissionActionEditor';
import MissionActionRunDialog from '../components/fleetActions/MissionActionRunDialog';
import MissionActionRunCard from '../components/fleetActions/MissionActionRunCard';

export default function MissionActions() {
  const { isAdmin } = useAuth(); const { t } = useLocale();
  const [actions, setActions] = useState<FleetAction[]>([]); const [runs, setRuns] = useState<FleetActionRun[]>([]);
  const [vessels, setVessels] = useState<Vessel[]>([]); const [pipelines, setPipelines] = useState<Pipeline[]>([]);
  const [loading, setLoading] = useState(false); const [error, setError] = useState(''); const [notice, setNotice] = useState('');
  const [edit, setEdit] = useState<FleetAction | null | undefined>(undefined); const [runAction, setRunAction] = useState<FleetAction | null>(null);
  const [deleting, setDeleting] = useState<FleetAction | null>(null); const request = useRef(0);
  const load = useCallback(async () => {
    const version = ++request.current; setLoading(true); setError('');
    try {
      const [a, r, v, p] = await Promise.all([listFleetActions(), listFleetActionRuns(), listAllVessels(), listAllPipelines()]);
      if (request.current !== version) return;
      setActions(a); setRuns(r); setVessels(v); setPipelines(p);
    } catch (e) { if (request.current === version) setError(e instanceof Error ? e.message : t('Could not load mission actions.')); }
    finally { if (request.current === version) setLoading(false); }
  }, [t]);
  useEffect(() => { if (isAdmin) void load(); else { ++request.current; setLoading(false); } return () => { ++request.current; }; }, [isAdmin, load]);
  async function save(data: FleetActionUpsertRequest) {
    if (edit === null) await createFleetAction(data); else if (edit) await updateFleetAction(edit.id, data);
    setEdit(undefined); setNotice(t('Mission action saved.')); await load();
  }
  async function start(data: FleetActionRunRequest) {
    if (!runAction) return;
    const created = await startFleetActionRun(runAction.id, data);
    setRuns((current) => [created, ...current.filter((run) => run.id !== created.id)]); setNotice(t('Run accepted.')); await load();
  }
  async function cancel(id: string) {
    try { const updated = await cancelFleetActionRun(id); setRuns((current) => current.map((run) => run.id === id ? updated : run)); setNotice(t('Cancellation requested.')); }
    catch (e) { setError(e instanceof Error ? e.message : t('Could not cancel the run.')); }
  }
  async function remove() {
    if (!deleting) return;
    try { await deleteFleetAction(deleting.id); setDeleting(null); setNotice(t('Mission action deleted.')); await load(); }
    catch (e) { setError(e instanceof Error ? e.message : t('Could not delete the mission action.')); setDeleting(null); }
  }
  if (!isAdmin) return <main className="page-content"><h2>{t('Mission Actions')}</h2><p>{t('Global admin access is required.')}</p></main>;
  return <main className="page-content">
    <PageHeader title={t('Mission Actions')} subtitle={t('Save reusable mission prompts and review each vessel run.')} actions={<><RefreshButton onRefresh={load} title="Refresh mission actions" /><button className="btn btn-primary" onClick={() => setEdit(null)}>{t('New action')}</button></>} />
    {error && <p className="alert alert-error" role="alert">{error}</p>}{notice && <p className="text-dim" role="status">{notice}</p>}
    {loading && <p>{t('Loading mission actions...')}</p>}
    {!loading && actions.length === 0 && <p className="text-dim">{t('No saved mission actions yet.')}</p>}
    <div className="card-grid">{actions.map((action) => <MissionActionCard key={action.id} action={action} onRun={setRunAction} onEdit={setEdit} onDelete={setDeleting} />)}</div>
    <h2>{t('Recent runs')}</h2>{runs.length === 0 && !loading && <p className="text-dim">{t('No runs yet.')}</p>}
    <div className="card-grid">{runs.map((run) => <MissionActionRunCard key={run.id} run={run} onCancel={cancel} />)}</div>
    {edit !== undefined && <MissionActionEditor action={edit} pipelines={pipelines} onClose={() => setEdit(undefined)} onSave={save} />}
    {runAction && <MissionActionRunDialog action={runAction} vessels={vessels.filter((vessel) => vessel.active)} onClose={() => setRunAction(null)} onStart={start} />}
    <ConfirmDialog open={!!deleting} title="Delete mission action" message={t('Delete this saved mission action? Existing run history will remain.')} confirmLabel="Delete" danger onConfirm={() => void remove()} onCancel={() => setDeleting(null)} />
  </main>;
}
