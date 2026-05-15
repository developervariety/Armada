import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link, useLocation, useNavigate, useParams } from 'react-router-dom';
import {
  createPlanningSession,
  deletePlanningSession,
  dispatchPlanningSession,
  getVesselReadiness,
  getPlanningSession,
  listCaptains,
  listFleets,
  listPipelines,
  listPlanningSessions,
  listVessels,
  sendPlanningSessionMessage,
  stopPlanningSession,
  summarizePlanningSession,
} from '../api/client';
import type {
  Captain,
  Fleet,
  Pipeline,
  PlanningSession,
  PlanningSessionDetail,
  PlanningSessionMessage,
  SelectedPlaybook,
  Vessel,
  VesselReadinessResult,
  WebSocketMessage,
} from '../types/models';
import { useLocale } from '../context/LocaleContext';
import { useNotifications } from '../context/NotificationContext';
import { useWebSocket } from '../context/WebSocketContext';
import ConfirmDialog from '../components/shared/ConfirmDialog';
import StatusBadge from '../components/shared/StatusBadge';
import PlanningDispatchCard from '../components/planning/PlanningDispatchCard';
import PlanningSessionListCard from '../components/planning/PlanningSessionListCard';
import PlanningStartCard from '../components/planning/PlanningStartCard';
import PlanningTranscriptCard from '../components/planning/PlanningTranscriptCard';
import ReadinessPanel from '../components/shared/ReadinessPanel';
import {
  buildDispatchSeed,
  type DispatchSeedState,
  getLatestAssistantMessage,
  mergeCaptainState,
  removeSession,
  resolveDispatchSeedUpdate,
  upsertMessage,
  upsertSession,
} from './planning/planningUtils';

interface PlanningSummaryEventPayload {
  sessionId?: string;
  messageId?: string;
  draft?: {
    title?: string;
    description?: string;
    method?: string;
  };
}

interface PlanningPrefillState {
  fromWorkspace?: boolean;
  fromIncident?: boolean;
  fromObjective?: boolean;
  objectiveId?: string;
  vesselId?: string;
  fleetId?: string;
  pipelineId?: string;
  title?: string;
  initialPrompt?: string;
}

export default function Planning() {
  const { id } = useParams<{ id?: string }>();
  const navigate = useNavigate();
  const location = useLocation();
  const { t, formatDateTime, formatRelativeTime } = useLocale();
  const { pushToast } = useNotifications();
  const { subscribe } = useWebSocket();

  const [sessions, setSessions] = useState<PlanningSession[]>([]);
  const [captains, setCaptains] = useState<Captain[]>([]);
  const [fleets, setFleets] = useState<Fleet[]>([]);
  const [vessels, setVessels] = useState<Vessel[]>([]);
  const [pipelines, setPipelines] = useState<Pipeline[]>([]);
  const [detail, setDetail] = useState<PlanningSessionDetail | null>(null);
  const [readiness, setReadiness] = useState<VesselReadinessResult | null>(null);
  const [loadingCatalog, setLoadingCatalog] = useState(true);
  const [loadingDetail, setLoadingDetail] = useState(false);
  const [loadingReadiness, setLoadingReadiness] = useState(false);
  const [error, setError] = useState('');

  const [title, setTitle] = useState('');
  const [captainId, setCaptainId] = useState('');
  const [fleetId, setFleetId] = useState('');
  const [vesselId, setVesselId] = useState('');
  const [pipelineId, setPipelineId] = useState('');
  const [objectiveId, setObjectiveId] = useState('');
  const [selectedPlaybooks, setSelectedPlaybooks] = useState<SelectedPlaybook[]>([]);
  const [composer, setComposer] = useState('');
  const [selectedMessageId, setSelectedMessageId] = useState('');
  const [dispatchTitle, setDispatchTitle] = useState('');
  const [dispatchDescription, setDispatchDescription] = useState('');

  const [creating, setCreating] = useState(false);
  const [sending, setSending] = useState(false);
  const [summarizing, setSummarizing] = useState(false);
  const [dispatching, setDispatching] = useState(false);
  const [stopping, setStopping] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [confirmDeleteOpen, setConfirmDeleteOpen] = useState(false);

  const transcriptRef = useRef<HTMLDivElement | null>(null);
  const dispatchSeedRef = useRef<DispatchSeedState | null>(null);
  const planningPrefillAppliedRef = useRef(false);

  const loadCatalog = useCallback(async () => {
    try {
      setLoadingCatalog(true);
      setError('');
      const [sessionItems, captainResult, fleetResult, vesselResult, pipelineResult] = await Promise.all([
        listPlanningSessions().catch(() => []),
        listCaptains({ pageSize: 9999 }).catch(() => null),
        listFleets({ pageSize: 9999 }).catch(() => null),
        listVessels({ pageSize: 9999 }).catch(() => null),
        listPipelines({ pageSize: 9999 }).catch(() => null),
      ]);

      setSessions(sessionItems);
      setCaptains(captainResult?.objects || []);
      setFleets(fleetResult?.objects || []);
      setVessels(vesselResult?.objects || []);
      setPipelines(pipelineResult?.objects || []);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Failed to load planning data.'));
    } finally {
      setLoadingCatalog(false);
    }
  }, [t]);

  const loadDetail = useCallback(async (sessionId: string) => {
    try {
      setLoadingDetail(true);
      setError('');
      const result = await getPlanningSession(sessionId);
      setDetail(result);
      setSessions((current) => upsertSession(current, result.session));
    } catch (err: unknown) {
      setDetail(null);
      setError(err instanceof Error ? err.message : t('Failed to load planning session.'));
    } finally {
      setLoadingDetail(false);
    }
  }, [t]);

  useEffect(() => {
    loadCatalog();
  }, [loadCatalog]);

  useEffect(() => {
    if (!id) {
      setDetail(null);
      setSelectedMessageId('');
      setDispatchTitle('');
      setDispatchDescription('');
      dispatchSeedRef.current = null;
      return;
    }

    loadDetail(id);
  }, [id, loadDetail]);

  useEffect(() => {
    const unsubscribe = subscribe((msg: WebSocketMessage) => {
      if (msg.type === 'planning-session.changed') {
        const payload = msg.data as { session?: PlanningSession } | undefined;
        if (!payload?.session) return;

        setSessions((current) => upsertSession(current, payload.session!));
        setDetail((current) => current && current.session.id === payload.session!.id
          ? { ...current, session: payload.session! }
          : current);
        return;
      }

      if (msg.type === 'captain.changed') {
        const payload = msg.data as { id?: string; name?: string; state?: string } | undefined;
        if (!payload?.id || !payload.state) return;
        const captainUpdate = {
          id: payload.id,
          name: payload.name,
          state: payload.state,
        };

        setCaptains((current) => mergeCaptainState(current, captainUpdate));
        setDetail((current) => {
          if (!current?.captain || current.captain.id !== captainUpdate.id) return current;
          return {
            ...current,
            captain: {
              ...current.captain,
              state: captainUpdate.state,
              name: captainUpdate.name ?? current.captain.name,
            },
          };
        });
        return;
      }

      if (msg.type === 'planning-session.message.created' || msg.type === 'planning-session.message.updated') {
        const payload = msg.data as { sessionId?: string; message?: PlanningSessionMessage } | undefined;
        if (!payload?.sessionId || !payload.message) return;

        setDetail((current) => {
          if (!current || current.session.id !== payload.sessionId) return current;
          return {
            ...current,
            messages: upsertMessage(current.messages, payload.message!),
          };
        });
        return;
      }

      if (msg.type === 'planning-session.summary.created') {
        const payload = msg.data as PlanningSummaryEventPayload | undefined;
        if (!payload?.sessionId || payload.sessionId !== id || !payload.draft) return;

        if (payload.messageId) {
          setSelectedMessageId(payload.messageId);
        }
        setDispatchTitle(payload.draft.title || '');
        setDispatchDescription(payload.draft.description || '');
        dispatchSeedRef.current = {
          key: `${payload.sessionId}:${payload.messageId || 'latest'}`,
          title: payload.draft.title || '',
          description: payload.draft.description || '',
          source: 'summary',
        };
        return;
      }

      if (msg.type === 'planning-session.dispatch.created') {
        const payload = msg.data as { sessionId?: string; voyageId?: string } | undefined;
        if (!payload?.sessionId || payload.sessionId !== id || !payload.voyageId) return;
        pushToast('success', t('Dispatch created from this planning session.'));
        return;
      }

      if (msg.type === 'planning-session.deleted') {
        const payload = msg.data as { sessionId?: string } | undefined;
        if (!payload?.sessionId) return;

        setSessions((current) => removeSession(current, payload.sessionId!));
        if (payload.sessionId === id) {
          setDetail(null);
          setSelectedMessageId('');
          setDispatchTitle('');
          setDispatchDescription('');
          dispatchSeedRef.current = null;
          navigate('/planning');
          if (!deleting) {
            pushToast('warning', t('Planning session deleted.'));
          }
        }
      }
    });

    return unsubscribe;
  }, [deleting, id, navigate, pushToast, subscribe, t]);

  useEffect(() => {
    if (!detail) return;

    const latestAssistant = getLatestAssistantMessage(detail.messages);
    if (!latestAssistant) return;

    const selectedStillExists = detail.messages.some((message) => message.id === selectedMessageId);
    if (!selectedMessageId || !selectedStillExists) {
      setSelectedMessageId(latestAssistant.id);
    }
  }, [detail, selectedMessageId]);

  useEffect(() => {
    if (!detail) return;

    const selectedMessage = detail.messages.find((message) => message.id === selectedMessageId) || null;
    const nextSeed = resolveDispatchSeedUpdate({
      sessionId: detail.session.id,
      sessionTitle: detail.session.title,
      message: selectedMessage,
      currentTitle: dispatchTitle,
      currentDescription: dispatchDescription,
      previousSeed: dispatchSeedRef.current,
    });
    if (!nextSeed) return;

    dispatchSeedRef.current = nextSeed;
    if (nextSeed.title !== dispatchTitle) {
      setDispatchTitle(nextSeed.title);
    }
    if (nextSeed.description !== dispatchDescription) {
      setDispatchDescription(nextSeed.description);
    }
  }, [detail, dispatchDescription, dispatchTitle, selectedMessageId]);

  useEffect(() => {
    if (planningPrefillAppliedRef.current || id || loadingCatalog) return;

    const prefill = location.state as PlanningPrefillState | null;
    if (!prefill?.fromWorkspace && !prefill?.fromIncident && !prefill?.fromObjective) return;

    if (prefill.title) setTitle(prefill.title);
    if (prefill.fleetId) setFleetId(prefill.fleetId);
    if (prefill.vesselId) setVesselId(prefill.vesselId);
    if (prefill.pipelineId) setPipelineId(prefill.pipelineId);
    if (prefill.initialPrompt) setComposer(prefill.initialPrompt);
    if (prefill.objectiveId) setObjectiveId(prefill.objectiveId);

    planningPrefillAppliedRef.current = true;
  }, [id, loadingCatalog, location.state]);

  useEffect(() => {
    if (!transcriptRef.current) return;
    transcriptRef.current.scrollTop = transcriptRef.current.scrollHeight;
  }, [detail?.messages]);

  useEffect(() => {
    if (!fleetId || !vesselId || loadingCatalog) return;

    const selectedVessel = vessels.find((vessel) => vessel.id === vesselId);
    if (!selectedVessel) return;

    if (selectedVessel.fleetId !== fleetId) {
      setVesselId('');
    }
  }, [fleetId, loadingCatalog, vesselId, vessels]);

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

  const availableVessels = useMemo(() => {
    return fleetId ? vessels.filter((vessel) => vessel.fleetId === fleetId) : vessels;
  }, [fleetId, vessels]);

  const selectedCaptain = useMemo(
    () => captains.find((captain) => captain.id === captainId) || null,
    [captainId, captains],
  );

  const currentSession = detail?.session || null;
  const currentMessages = detail?.messages || [];
  const selectedMessage = currentMessages.find((message) => message.id === selectedMessageId) || null;
  const sessionStopping = currentSession?.status === 'Stopping';
  const canStartSession = !!selectedCaptain && selectedCaptain.supportsPlanningSessions && selectedCaptain.state === 'Idle' && !!vesselId && !creating;
  const canSend = currentSession?.status === 'Active' && composer.trim().length > 0 && !sending;
  const canStop = !!currentSession && ['Active', 'Responding'].includes(currentSession.status) && !stopping;
  const canSummarize = !!currentSession && !!selectedMessage?.content.trim() && !summarizing;
  const canOpenInDispatch = !!currentSession && dispatchDescription.trim().length > 0;
  const canDispatch = !!currentSession && !!selectedMessage?.content.trim() && dispatchDescription.trim().length > 0 && !dispatching;
  const planningPrefill = location.state as PlanningPrefillState | null;

  async function handleCreateSession() {
    if (!captainId || !vesselId) return;

    try {
      setCreating(true);
      setError('');
      const result = await createPlanningSession({
        title: title.trim() || undefined,
        captainId,
        vesselId,
        fleetId: fleetId || undefined,
        pipelineId: pipelineId || undefined,
        selectedPlaybooks,
        objectiveId: objectiveId || undefined,
      });

      setSessions((current) => upsertSession(current, result.session));
      pushToast('success', t('Planning session started.'));
      navigate(`/planning/${result.session.id}`, {
        state: composer.trim() ? { fromWorkspace: true, initialPrompt: composer.trim() } : undefined,
      });
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : t('Failed to start planning session.');
      if (message === 'Request timed out') {
        await loadCatalog();
        setError(t('Starting the planning session is taking longer than expected. Armada is still provisioning the dock and worktree. If setup completes, the session will appear in the list on the left.'));
      } else {
        setError(message);
      }
    } finally {
      setCreating(false);
    }
  }

  async function handleSendMessage() {
    if (!currentSession || !composer.trim()) return;

    try {
      setSending(true);
      setError('');
      const result = await sendPlanningSessionMessage(currentSession.id, { content: composer.trim() });
      setComposer('');
      setDetail(result);
      setSessions((current) => upsertSession(current, result.session));
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Failed to send message.'));
    } finally {
      setSending(false);
    }
  }

  async function handleSummarize() {
    if (!currentSession || !selectedMessageId) return;

    try {
      setSummarizing(true);
      setError('');
      const result = await summarizePlanningSession(currentSession.id, {
        messageId: selectedMessageId,
        title: dispatchTitle.trim() || undefined,
      });
      setDispatchTitle(result.title);
      setDispatchDescription(result.description);
      dispatchSeedRef.current = {
        key: `${result.sessionId}:${result.messageId}`,
        title: result.title,
        description: result.description,
        source: 'summary',
      };
      pushToast('success', t('Dispatch draft summarized from planning output.'));
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Failed to summarize planning output.'));
    } finally {
      setSummarizing(false);
    }
  }

  function handleOpenInDispatch() {
    if (!currentSession || !dispatchDescription.trim()) return;

    navigate('/dispatch', {
      state: {
        fromPlanning: true,
        vesselId: currentSession.vesselId,
        pipelineName: pipelines.find((pipeline) => pipeline.id === currentSession.pipelineId)?.name,
        selectedPlaybooks: currentSession.selectedPlaybooks || [],
        prompt: dispatchDescription.trim(),
        voyageTitle: dispatchTitle.trim() || undefined,
      },
    });
  }

  async function handleDispatch() {
    if (!currentSession || !dispatchDescription.trim()) return;

    try {
      setDispatching(true);
      setError('');
      const voyage = await dispatchPlanningSession(currentSession.id, {
        messageId: selectedMessageId || undefined,
        title: dispatchTitle.trim() || undefined,
        description: dispatchDescription.trim(),
      });

      pushToast('success', t('Dispatch created from planning session.'));
      navigate(`/voyages/${voyage.id}`);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Failed to dispatch from planning session.'));
    } finally {
      setDispatching(false);
    }
  }

  async function handleStopSession() {
    if (!currentSession) return;

    try {
      setStopping(true);
      setError('');
      const result = await stopPlanningSession(currentSession.id);
      setDetail(result);
      setSessions((current) => upsertSession(current, result.session));
      pushToast('warning', t('Planning session is stopping.'));
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Failed to stop planning session.'));
    } finally {
      setStopping(false);
    }
  }

  async function handleDeleteSession() {
    if (!currentSession) return;

    try {
      setDeleting(true);
      setError('');
      await deletePlanningSession(currentSession.id);
      setSessions((current) => removeSession(current, currentSession.id));
      setDetail(null);
      setSelectedMessageId('');
      setDispatchTitle('');
      setDispatchDescription('');
      dispatchSeedRef.current = null;
      navigate('/planning');
      pushToast('warning', t('Planning session deleted.'));
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Failed to delete planning session.'));
    } finally {
      setDeleting(false);
    }
  }

  return (
    <div>
      <div className="page-header">
        <div>
          <h2>{t('Planning')}</h2>
          <p className="text-muted">
            {t('Chat with a captain against a specific vessel, preserve the transcript, and dispatch directly from the planning output.')}
          </p>
        </div>
      </div>

      {error && (
        <div className="alert alert-error" style={{ marginBottom: '1rem' }}>
          {error}
        </div>
      )}

      {!id && (planningPrefill?.fromWorkspace || planningPrefill?.fromIncident || planningPrefill?.fromObjective) && (
        <div className="alert" style={{ marginBottom: '1rem' }}>
          {planningPrefill?.fromObjective
            ? t('Prefilled from a backlog item. Review the vessel and prompt, then start a planning session to turn that scoped work into an execution plan.')
            : planningPrefill?.fromIncident
            ? t('Prefilled from an incident. Review the vessel and prompt, then start a planning session for the hotfix or recovery path.')
            : t('Prefilled from Workspace. Review the vessel and prompt, then start a planning session.')}
          {objectiveId && (
            <>
              {' '}
              <button type="button" className="btn btn-sm" style={{ marginLeft: '0.75rem' }} onClick={() => navigate(`/backlog/${objectiveId}`)}>
                {t('Open Backlog Item')}
              </button>
            </>
          )}
        </div>
      )}

      <PlanningStartCard
        t={t}
        loading={loadingCatalog}
        captains={captains}
        fleets={fleets}
        vessels={vessels}
        pipelines={pipelines}
        title={title}
        captainId={captainId}
        fleetId={fleetId}
        vesselId={vesselId}
        pipelineId={pipelineId}
        availableVessels={availableVessels}
        selectedCaptain={selectedCaptain}
        selectedPlaybooks={selectedPlaybooks}
        pendingInitialPrompt={composer}
        creating={creating}
        canStartSession={canStartSession}
        onTitleChange={setTitle}
        onCaptainChange={setCaptainId}
        onFleetChange={setFleetId}
        onVesselChange={setVesselId}
        onPipelineChange={setPipelineId}
        onSelectedPlaybooksChange={setSelectedPlaybooks}
        onStart={handleCreateSession}
      />

      {vesselId && (
        <ReadinessPanel
          title={t('Vessel Readiness')}
          readiness={readiness}
          loading={loadingReadiness}
          emptyMessage={t('Select a vessel to inspect readiness.')}
          compact
        />
      )}

      <div style={{ display: 'grid', gridTemplateColumns: '360px minmax(0, 1fr)', gap: '1rem', alignItems: 'start' }}>
        <div style={{ display: 'grid', gap: '1rem' }}>
          <PlanningSessionListCard
            t={t}
            sessions={sessions}
            activeSessionId={id}
            formatRelativeTime={formatRelativeTime}
            onSelect={(sessionId) => navigate(`/planning/${sessionId}`)}
          />
        </div>

        <div style={{ display: 'grid', gap: '1rem' }}>
          {!id ? (
            <div className="card" style={{ padding: '1.25rem' }}>
              <h3>{t('Select Or Start A Session')}</h3>
              <p className="text-muted">
                {t('Choose an existing planning session from the left, or start a new one to begin chatting with a captain.')}
              </p>
            </div>
          ) : loadingDetail ? (
            <div className="card" style={{ padding: '1.25rem' }}>
              <p className="text-muted">{t('Loading planning session...')}</p>
            </div>
          ) : !detail ? (
            <div className="card" style={{ padding: '1.25rem' }}>
              <p className="text-muted">{t('Planning session not found.')}</p>
            </div>
          ) : (
            <>
              <div className="card" style={{ padding: '1rem' }}>
                <div style={{ display: 'flex', justifyContent: 'space-between', gap: '1rem', alignItems: 'start' }}>
                  <div>
                    <div className="breadcrumb" style={{ marginBottom: '0.5rem' }}>
                      <Link to="/planning">{t('Planning')}</Link>
                      <span className="breadcrumb-sep">&gt;</span>
                      <span>{detail.session.title}</span>
                    </div>
                    <h3 style={{ marginBottom: '0.35rem' }}>{detail.session.title}</h3>
                    <p className="text-muted">
                      {t('Captain {{captain}} on vessel {{vessel}}', {
                        captain: detail.captain?.name || detail.session.captainId,
                        vessel: detail.vessel?.name || detail.session.vesselId,
                      })}
                    </p>
                  </div>
                  <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'center', flexWrap: 'wrap', justifyContent: 'flex-end' }}>
                    <StatusBadge status={detail.session.status} />
                    <button type="button" className="btn btn-sm" disabled={!canStop} onClick={handleStopSession}>
                      {stopping || sessionStopping ? t('Stopping...') : t('Stop Session')}
                    </button>
                    <button type="button" className="btn btn-sm" disabled={deleting} onClick={() => setConfirmDeleteOpen(true)}>
                      {deleting ? t('Deleting...') : t('Delete Session')}
                    </button>
                  </div>
                </div>

                <div className="detail-grid" style={{ marginTop: '1rem' }}>
                  <div className="detail-field">
                    <span className="detail-label">{t('Captain')}</span>
                    <span>{detail.captain?.name || detail.session.captainId}</span>
                  </div>
                  <div className="detail-field">
                    <span className="detail-label">{t('Runtime')}</span>
                    <span>{detail.captain?.runtime || '-'}</span>
                  </div>
                  <div className="detail-field">
                    <span className="detail-label">{t('Vessel')}</span>
                    <span>{detail.vessel?.name || detail.session.vesselId}</span>
                  </div>
                  <div className="detail-field">
                    <span className="detail-label">{t('Branch')}</span>
                    <span className="mono">{detail.session.branchName || '-'}</span>
                  </div>
                  <div className="detail-field">
                    <span className="detail-label">{t('Pipeline')}</span>
                    <span>{pipelines.find((pipeline) => pipeline.id === detail.session.pipelineId)?.name || detail.session.pipelineId || '-'}</span>
                  </div>
                  <div className="detail-field">
                    <span className="detail-label">{t('Playbooks')}</span>
                    <span>{detail.session.selectedPlaybooks?.length || 0}</span>
                  </div>
                  <div className="detail-field">
                    <span className="detail-label">{t('Updated')}</span>
                    <span title={formatDateTime(detail.session.lastUpdateUtc)}>{formatRelativeTime(detail.session.lastUpdateUtc)}</span>
                  </div>
                </div>

                <div className="text-muted" style={{ marginTop: '1rem' }}>
                  {t('Each planning reply is generated from the preserved transcript and repo context, then streamed back into this session.')}
                </div>

                {detail.session.failureReason && (
                  <div className="alert alert-error" style={{ marginTop: '1rem' }}>
                    {detail.session.failureReason}
                  </div>
                )}
              </div>

              <PlanningTranscriptCard
                t={t}
                transcriptRef={transcriptRef}
                messages={currentMessages}
                selectedMessageId={selectedMessageId}
                currentStatus={currentSession?.status}
                composer={composer}
                sending={sending}
                canSend={canSend}
                formatDateTime={formatDateTime}
                formatRelativeTime={formatRelativeTime}
                onSelectMessage={(messageId) => {
                  setSelectedMessageId(messageId);
                  dispatchSeedRef.current = null;
                }}
                onComposerChange={setComposer}
                onSend={handleSendMessage}
              />

              <PlanningDispatchCard
                t={t}
                selectedMessage={selectedMessage}
                dispatchTitle={dispatchTitle}
                dispatchDescription={dispatchDescription}
                canSummarize={canSummarize}
                canOpenInDispatch={canOpenInDispatch}
                canDispatch={canDispatch}
                summarizing={summarizing}
                dispatching={dispatching}
                onDispatchTitleChange={setDispatchTitle}
                onDispatchDescriptionChange={setDispatchDescription}
                onSummarize={handleSummarize}
                onOpenInDispatch={handleOpenInDispatch}
                onDispatch={handleDispatch}
              />
            </>
          )}
        </div>
      </div>

      <ConfirmDialog
        open={confirmDeleteOpen}
        title={t('Delete Planning Session')}
        message={t('Delete this planning session and its transcript?')}
        confirmLabel={t('Delete Session')}
        cancelLabel={t('Cancel')}
        danger
        onConfirm={() => {
          setConfirmDeleteOpen(false);
          void handleDeleteSession();
        }}
        onCancel={() => setConfirmDeleteOpen(false)}
      />
    </div>
  );
}
