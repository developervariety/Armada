import { useEffect, useRef, useState } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { Link } from 'react-router-dom';
import { listVessels, listPipelines, createVoyage, getVesselReadiness } from '../api/client';
import type { Vessel, Pipeline, SelectedPlaybook, VesselReadinessResult } from '../types/models';
import { useLocale } from '../context/LocaleContext';
import { useNotifications } from '../context/NotificationContext';
import PlaybookSelector from '../components/shared/PlaybookSelector';
import ReadinessPanel from '../components/shared/ReadinessPanel';

interface DispatchPrefillState {
  fromPlanning?: boolean;
  fromWorkspace?: boolean;
  fromIncident?: boolean;
  fromObjective?: boolean;
  objectiveId?: string;
  vesselId?: string;
  pipelineName?: string;
  prompt?: string;
  selectedPlaybooks?: SelectedPlaybook[];
  voyageTitle?: string;
}

export default function Dispatch() {
  const { t } = useLocale();
  const { pushToast } = useNotifications();
  const navigate = useNavigate();
  const location = useLocation();

  const [vessels, setVessels] = useState<Vessel[]>([]);
  const [pipelines, setPipelines] = useState<Pipeline[]>([]);
  const [selectedPipeline, setSelectedPipeline] = useState('');
  const [voyageTitle, setVoyageTitle] = useState('');

  const [vesselId, setVesselId] = useState('');
  const [objectiveId, setObjectiveId] = useState('');
  const [prompt, setPrompt] = useState('');
  const [priority, setPriority] = useState(100);
  const [selectedPlaybooks, setSelectedPlaybooks] = useState<SelectedPlaybook[]>([]);
  const [dispatching, setDispatching] = useState(false);
  const [result, setResult] = useState<{ ok: boolean; message: string } | null>(null);
  const [readiness, setReadiness] = useState<VesselReadinessResult | null>(null);
  const [loadingReadiness, setLoadingReadiness] = useState(false);
  const prefillAppliedRef = useRef(false);

  useEffect(() => {
    Promise.all([
      listVessels({ pageSize: 9999 }).catch(() => null),
      listPipelines({ pageSize: 9999 }).catch(() => null),
    ]).then(([vRes, pRes]) => {
      if (vRes) setVessels(vRes.objects);
      if (pRes) setPipelines(pRes.objects);
    });
  }, []);

  useEffect(() => {
    if (prefillAppliedRef.current) return;

    const prefill = location.state as DispatchPrefillState | null;
    if (!prefill?.fromPlanning && !prefill?.fromWorkspace && !prefill?.fromIncident && !prefill?.fromObjective) return;

    if (prefill.vesselId) setVesselId(prefill.vesselId);
    if (prefill.pipelineName) setSelectedPipeline(prefill.pipelineName);
    if (prefill.prompt) setPrompt(prefill.prompt);
    if (prefill.selectedPlaybooks?.length) setSelectedPlaybooks(prefill.selectedPlaybooks);
    if (prefill.voyageTitle) setVoyageTitle(prefill.voyageTitle);
    if (prefill.objectiveId) setObjectiveId(prefill.objectiveId);

    prefillAppliedRef.current = true;
  }, [location.state]);

  useEffect(() => {
    if (!vesselId) {
      setReadiness(null);
      setLoadingReadiness(false);
      return;
    }

    let cancelled = false;
    setLoadingReadiness(true);
    getVesselReadiness(vesselId)
      .then((value) => {
        if (!cancelled) setReadiness(value);
      })
      .catch(() => {
        if (!cancelled) setReadiness(null);
      })
      .finally(() => {
        if (!cancelled) setLoadingReadiness(false);
      });

    return () => {
      cancelled = true;
    };
  }, [vesselId]);

  const handleDispatch = async () => {
    if (!prompt.trim()) return;
    if (!vesselId) {
      setResult({ ok: false, message: t('Please select a vessel.') });
      return;
    }

    const selectedPipelineObj = pipelines.find((p) => p.name === selectedPipeline);
    const isMultiStage = selectedPipelineObj != null && selectedPipelineObj.stages.length > 1;

    const tasks = [prompt.trim()];
    if (!tasks.length) return;

    setDispatching(true);
    setResult(null);
    try {
      const resolvedVoyageTitle = voyageTitle.trim() || (tasks.length > 1 ? t('Multi-task voyage') : tasks[0].substring(0, 80));
      const missions = tasks.map((t) => ({
        vesselId,
        title: t.substring(0, 80),
        description: t,
        priority,
      }));
      const voyage = await createVoyage({
        title: resolvedVoyageTitle,
        vesselId,
        missions,
        ...(objectiveId ? { objectiveId } : {}),
        ...(selectedPipeline ? { pipeline: selectedPipeline } : {}),
        ...(selectedPlaybooks.length > 0 ? { selectedPlaybooks } : {}),
      });
      const missionCount = isMultiStage
        ? t('{{count}} pipeline stages', { count: selectedPipelineObj!.stages.length })
        : t('{{count}} mission(s)', { count: missions.length });
      const successMessage = t('Dispatched voyage with {{missionCount}}', { missionCount });
      setResult({ ok: true, message: successMessage });
      pushToast('success', successMessage);
      setVoyageTitle('');
      setPrompt('');
      setTimeout(() => {
        navigate(`/voyages/${voyage.id}`);
      }, 1500);
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : t('Unknown error');
      setResult({ ok: false, message: t('Failed: {{message}}', { message: msg }) });
    } finally {
      setDispatching(false);
    }
  };

  return (
    <div>
      <div className="page-header">
        <div>
          <h2>{t('Dispatch')}</h2>
          <p className="text-muted">
            {t('Describe the work you want Armada to dispatch through the selected vessel and pipeline.')}
          </p>
        </div>
      </div>

      <div className="card" style={{ marginBottom: '1rem' }}>
        <div className="dispatch-form">
          {((location.state as DispatchPrefillState | null)?.fromPlanning
            || (location.state as DispatchPrefillState | null)?.fromWorkspace
            || (location.state as DispatchPrefillState | null)?.fromIncident
            || (location.state as DispatchPrefillState | null)?.fromObjective) && (
            <div className="alert" style={{ marginBottom: '1rem' }}>
              {(location.state as DispatchPrefillState | null)?.fromObjective
                ? t('Prefilled from a backlog item. Review the scoped draft below and dispatch when ready.')
                : (location.state as DispatchPrefillState | null)?.fromPlanning
                ? t('Prefilled from a planning session. Review the draft below and dispatch when ready.')
                : (location.state as DispatchPrefillState | null)?.fromIncident
                  ? t('Prefilled from an incident. Review the hotfix draft below and dispatch when ready.')
                  : t('Prefilled from Workspace selection. Review the scoped draft below and dispatch when ready.')}
              {objectiveId && (
                <button type="button" className="btn btn-sm" style={{ marginLeft: '0.75rem' }} onClick={() => navigate(`/backlog/${objectiveId}`)}>
                  {t('Open Backlog Item')}
                </button>
              )}
            </div>
          )}

          {vesselId && (
            <ReadinessPanel
              title={t('Vessel Readiness')}
              readiness={readiness}
              loading={loadingReadiness}
              emptyMessage={t('Select a vessel to inspect readiness.')}
              compact
            />
          )}

          {/* Row 1: Vessel + Pipeline + Priority + Optional Voyage Title */}
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))', gap: '0 1.5rem' }}>
            <div className="form-group">
              <label>{t('Vessel')}</label>
              <select
                value={vesselId}
                onChange={(e) => setVesselId(e.target.value)}
                required
              >
                <option value="">{t('Select a vessel...')}</option>
                {vessels.map((v) => (
                  <option key={v.id} value={v.id}>
                    {v.name}
                  </option>
                ))}
              </select>
            </div>
            <div className="form-group">
              <div className="form-label-row">
                <label>{t('Pipeline')}</label>
                <Link to="/pipelines" className="form-label-link">{t('Manage pipelines')}</Link>
              </div>
              <select
                value={selectedPipeline}
                onChange={(e) => setSelectedPipeline(e.target.value)}
              >
                <option value="">{t('Inherit (vessel, then fleet, then WorkerOnly)')}</option>
                {pipelines.map((p) => (
                  <option key={p.id} value={p.name}>
                    {p.name} ({p.stages.map((s) => s.personaName).join(' -> ')})
                  </option>
                ))}
              </select>
            </div>
            <div className="form-group">
              <label>{t('Priority')}</label>
              <input
                type="number"
                value={priority}
                onChange={(e) => setPriority(parseInt(e.target.value) || 100)}
                min={0}
                max={1000}
                title={t('Higher priority missions are assigned first (default 100)')}
              />
            </div>
            <div className="form-group">
              <label>{t('Voyage Title')}</label>
              <input
                value={voyageTitle}
                onChange={(e) => setVoyageTitle(e.target.value)}
                placeholder={t('Optional override for the voyage title')}
              />
            </div>
          </div>

          {/* Prompt */}
          <div className="form-group">
            <label>{t('Description')}</label>
            <textarea
              value={prompt}
              onChange={(e) => setPrompt(e.target.value)}
              rows={12}
              placeholder={t(`Describe what you need done.

Armada will dispatch this request as a voyage on the selected vessel.`)}
            />
          </div>

          <PlaybookSelector value={selectedPlaybooks} onChange={setSelectedPlaybooks} disabled={dispatching} />

          <div className="form-actions">
            <button
              type="button"
              className="btn-primary"
              disabled={dispatching || !vesselId || !prompt.trim()}
              onClick={handleDispatch}
            >
              {dispatching ? t('Dispatching...') : t('Dispatch')}
            </button>
          </div>

          {result && (
            <div className={`alert ${result.ok ? 'alert-success' : 'alert-error'}`}>
              {result.message}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
