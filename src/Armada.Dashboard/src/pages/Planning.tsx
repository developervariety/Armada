import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import {
  createPlanningSession,
  dispatchPlanningSession,
  getPlanningSession,
  listCaptains,
  listFleets,
  listPipelines,
  listPlanningSessions,
  listVessels,
  sendPlanningSessionMessage,
  stopPlanningSession,
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
  WebSocketMessage,
} from '../types/models';
import { useLocale } from '../context/LocaleContext';
import { useNotifications } from '../context/NotificationContext';
import { useWebSocket } from '../context/WebSocketContext';
import PlaybookSelector from '../components/shared/PlaybookSelector';
import StatusBadge from '../components/shared/StatusBadge';

function upsertSession(sessions: PlanningSession[], session: PlanningSession): PlanningSession[] {
  const next = [...sessions];
  const index = next.findIndex((item) => item.id === session.id);
  if (index >= 0) next[index] = session;
  else next.unshift(session);
  return next.sort((a, b) => new Date(b.lastUpdateUtc).getTime() - new Date(a.lastUpdateUtc).getTime());
}

function upsertMessage(messages: PlanningSessionMessage[], message: PlanningSessionMessage): PlanningSessionMessage[] {
  const next = [...messages];
  const index = next.findIndex((item) => item.id === message.id);
  if (index >= 0) next[index] = message;
  else next.push(message);
  return next.sort((a, b) => a.sequence - b.sequence);
}

export default function Planning() {
  const { id } = useParams<{ id?: string }>();
  const navigate = useNavigate();
  const { t, formatDateTime, formatRelativeTime } = useLocale();
  const { pushToast } = useNotifications();
  const { subscribe } = useWebSocket();

  const [sessions, setSessions] = useState<PlanningSession[]>([]);
  const [captains, setCaptains] = useState<Captain[]>([]);
  const [fleets, setFleets] = useState<Fleet[]>([]);
  const [vessels, setVessels] = useState<Vessel[]>([]);
  const [pipelines, setPipelines] = useState<Pipeline[]>([]);
  const [detail, setDetail] = useState<PlanningSessionDetail | null>(null);
  const [loadingCatalog, setLoadingCatalog] = useState(true);
  const [loadingDetail, setLoadingDetail] = useState(false);
  const [error, setError] = useState('');

  const [title, setTitle] = useState('');
  const [captainId, setCaptainId] = useState('');
  const [fleetId, setFleetId] = useState('');
  const [vesselId, setVesselId] = useState('');
  const [pipelineId, setPipelineId] = useState('');
  const [selectedPlaybooks, setSelectedPlaybooks] = useState<SelectedPlaybook[]>([]);
  const [composer, setComposer] = useState('');
  const [selectedMessageId, setSelectedMessageId] = useState('');
  const [dispatchTitle, setDispatchTitle] = useState('');
  const [dispatchDescription, setDispatchDescription] = useState('');

  const [creating, setCreating] = useState(false);
  const [sending, setSending] = useState(false);
  const [dispatching, setDispatching] = useState(false);
  const [stopping, setStopping] = useState(false);

  const transcriptRef = useRef<HTMLDivElement | null>(null);
  const seededDispatchKeyRef = useRef('');

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
      seededDispatchKeyRef.current = '';
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

      if (msg.type === 'planning-session.dispatch.created') {
        const payload = msg.data as { sessionId?: string; voyageId?: string } | undefined;
        if (!payload?.sessionId || payload.sessionId !== id || !payload.voyageId) return;
        pushToast('success', t('Dispatch created from this planning session.'));
      }
    });

    return unsubscribe;
  }, [id, pushToast, subscribe, t]);

  useEffect(() => {
    if (!detail) return;

    const latestAssistant = [...detail.messages]
      .reverse()
      .find((message) => message.role.toLowerCase() === 'assistant' && message.content.trim().length > 0);

    if (!latestAssistant) return;

    const selectedStillExists = detail.messages.some((message) => message.id === selectedMessageId);
    if (!selectedMessageId || !selectedStillExists) {
      setSelectedMessageId(latestAssistant.id);
    }
  }, [detail, selectedMessageId]);

  useEffect(() => {
    if (!detail) return;

    const selectedMessage = detail.messages.find((message) => message.id === selectedMessageId);
    const seedKey = `${detail.session.id}:${selectedMessageId}`;
    if (!selectedMessage || !selectedMessage.content.trim() || seededDispatchKeyRef.current === seedKey) return;

    seededDispatchKeyRef.current = seedKey;
    setDispatchTitle(detail.session.title?.trim() || selectedMessage.content.trim().substring(0, 80));
    setDispatchDescription(selectedMessage.content.trim());
  }, [detail, selectedMessageId]);

  useEffect(() => {
    if (!transcriptRef.current) return;
    transcriptRef.current.scrollTop = transcriptRef.current.scrollHeight;
  }, [detail?.messages]);

  useEffect(() => {
    if (!fleetId) return;
    const fleetStillMatches = vessels.some((vessel) => vessel.id === vesselId && vessel.fleetId === fleetId);
    if (!fleetStillMatches) {
      setVesselId('');
    }
  }, [fleetId, vesselId, vessels]);

  const availableVessels = useMemo(() => {
    return fleetId ? vessels.filter((vessel) => vessel.fleetId === fleetId) : vessels;
  }, [fleetId, vessels]);

  const currentSession = detail?.session || null;
  const currentMessages = detail?.messages || [];
  const selectedMessage = currentMessages.find((message) => message.id === selectedMessageId) || null;
  const canSend = currentSession?.status === 'Active' && composer.trim().length > 0 && !sending;
  const canStop = !!currentSession && ['Active', 'Responding', 'Stopping'].includes(currentSession.status) && !stopping;
  const canDispatch = !!currentSession && !!selectedMessage?.content.trim() && dispatchDescription.trim().length > 0 && !dispatching;

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
      });

      setSessions((current) => upsertSession(current, result.session));
      pushToast('success', t('Planning session started.'));
      navigate(`/planning/${result.session.id}`);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Failed to start planning session.'));
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
      pushToast('warning', t('Planning session stopped.'));
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Failed to stop planning session.'));
    } finally {
      setStopping(false);
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

      <div className="card" style={{ padding: '1rem', marginBottom: '1rem' }}>
        <h3 style={{ marginBottom: '0.35rem' }}>{t('Start Session')}</h3>
        <p className="text-muted" style={{ marginBottom: '1rem' }}>
          {t('Reserve a captain on a vessel, then use the transcript as the source of truth for a later dispatch.')}
        </p>

        {loadingCatalog ? (
          <p className="text-muted">{t('Loading planning catalog...')}</p>
        ) : (
          <div className="dispatch-form">
            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))', gap: '0 1rem' }}>
              <div className="form-group">
                <label>{t('Title')}</label>
                <input
                  value={title}
                  onChange={(event) => setTitle(event.target.value)}
                  placeholder={t('Optional planning session title')}
                />
              </div>

              <div className="form-group">
                <label>{t('Captain')}</label>
                <select value={captainId} onChange={(event) => setCaptainId(event.target.value)}>
                  <option value="">{t('Select a captain...')}</option>
                  {captains.map((captain) => (
                    <option
                      key={captain.id}
                      value={captain.id}
                      disabled={captain.state !== 'Idle'}
                    >
                      {`${captain.name} (${captain.runtime}) - ${captain.state}`}
                    </option>
                  ))}
                </select>
              </div>

              <div className="form-group">
                <label>{t('Fleet')}</label>
                <select value={fleetId} onChange={(event) => setFleetId(event.target.value)}>
                  <option value="">{t('Any fleet')}</option>
                  {fleets.map((fleet) => (
                    <option key={fleet.id} value={fleet.id}>
                      {fleet.name}
                    </option>
                  ))}
                </select>
              </div>

              <div className="form-group">
                <label>{t('Vessel')}</label>
                <select value={vesselId} onChange={(event) => setVesselId(event.target.value)}>
                  <option value="">{t('Select a vessel...')}</option>
                  {availableVessels.map((vessel) => (
                    <option key={vessel.id} value={vessel.id}>
                      {vessel.name}
                    </option>
                  ))}
                </select>
              </div>

              <div className="form-group">
                <label>{t('Pipeline')}</label>
                <select value={pipelineId} onChange={(event) => setPipelineId(event.target.value)}>
                  <option value="">{t('Inherit later during dispatch')}</option>
                  {pipelines.map((pipeline) => (
                    <option key={pipeline.id} value={pipeline.id}>
                      {pipeline.name}
                    </option>
                  ))}
                </select>
              </div>
            </div>

            <PlaybookSelector value={selectedPlaybooks} onChange={setSelectedPlaybooks} disabled={creating} />

            <div className="form-actions">
              <button
                type="button"
                className="btn-primary"
                disabled={creating || !captainId || !vesselId}
                onClick={handleCreateSession}
              >
                {creating ? t('Starting...') : t('Start Planning Session')}
              </button>
            </div>
          </div>
        )}
      </div>

      <div style={{ display: 'grid', gridTemplateColumns: '360px minmax(0, 1fr)', gap: '1rem', alignItems: 'start' }}>
        <div style={{ display: 'grid', gap: '1rem' }}>
          <div className="card" style={{ padding: '1rem' }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '0.75rem' }}>
              <h3>{t('Recent Sessions')}</h3>
              <span className="text-muted">{t('{{count}} total', { count: sessions.length })}</span>
            </div>

            {sessions.length === 0 ? (
              <p className="text-muted">{t('No planning sessions yet.')}</p>
            ) : (
              <div style={{ display: 'grid', gap: '0.5rem' }}>
                {sessions.map((session) => (
                  <button
                    key={session.id}
                    type="button"
                    onClick={() => navigate(`/planning/${session.id}`)}
                    className="btn"
                    style={{
                      textAlign: 'left',
                      padding: '0.75rem',
                      borderColor: id === session.id ? 'var(--accent)' : undefined,
                      background: id === session.id ? 'var(--surface-2)' : undefined,
                    }}
                  >
                    <div style={{ display: 'flex', justifyContent: 'space-between', gap: '0.75rem', alignItems: 'start' }}>
                      <div style={{ minWidth: 0 }}>
                        <div style={{ fontWeight: 600 }}>{session.title}</div>
                        <div className="text-dim mono" style={{ fontSize: '0.78rem' }}>{session.id}</div>
                        <div className="text-dim" style={{ marginTop: '0.2rem', fontSize: '0.82rem' }}>
                          {t('Updated {{time}}', { time: formatRelativeTime(session.lastUpdateUtc) })}
                        </div>
                      </div>
                      <StatusBadge status={session.status} />
                    </div>
                  </button>
                ))}
              </div>
            )}
          </div>
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
                      {stopping ? t('Stopping...') : t('Stop Session')}
                    </button>
                  </div>
                </div>

                <div className="detail-grid" style={{ marginTop: '1rem' }}>
                  <div className="detail-field">
                    <span className="detail-label">{t('Captain')}</span>
                    <span>{detail.captain?.name || detail.session.captainId}</span>
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

                {detail.session.failureReason && (
                  <div className="alert alert-error" style={{ marginTop: '1rem' }}>
                    {detail.session.failureReason}
                  </div>
                )}
              </div>

              <div className="card" style={{ padding: '1rem' }}>
                <div style={{ display: 'flex', justifyContent: 'space-between', gap: '1rem', alignItems: 'center', marginBottom: '0.75rem' }}>
                  <h3>{t('Transcript')}</h3>
                  <span className="text-muted">{t('{{count}} message(s)', { count: currentMessages.length })}</span>
                </div>

                <div
                  ref={transcriptRef}
                  style={{
                    maxHeight: '52vh',
                    overflowY: 'auto',
                    display: 'grid',
                    gap: '0.75rem',
                    paddingRight: '0.25rem',
                  }}
                >
                  {currentMessages.length === 0 ? (
                    <div className="text-muted">{t('No transcript yet. Send the first planning message below.')}</div>
                  ) : currentMessages.map((message) => {
                    const isAssistant = message.role.toLowerCase() === 'assistant';
                    const isUser = message.role.toLowerCase() === 'user';
                    const isSelected = selectedMessageId === message.id;

                    return (
                      <div
                        key={message.id}
                        style={{
                          border: '1px solid var(--border)',
                          borderRadius: 'var(--radius)',
                          padding: '0.9rem',
                          background: isUser ? 'var(--surface-2)' : 'var(--surface)',
                          boxShadow: isSelected ? '0 0 0 1px var(--accent)' : undefined,
                        }}
                      >
                        <div style={{ display: 'flex', justifyContent: 'space-between', gap: '1rem', alignItems: 'start', marginBottom: '0.5rem' }}>
                          <div>
                            <strong>{t(message.role)}</strong>
                            <div className="text-dim mono" style={{ fontSize: '0.78rem' }}>
                              {message.id}
                            </div>
                          </div>
                          <div style={{ display: 'flex', alignItems: 'center', gap: '0.5rem', flexWrap: 'wrap' }}>
                            <span className="text-dim" style={{ fontSize: '0.82rem' }} title={formatDateTime(message.lastUpdateUtc)}>
                              {formatRelativeTime(message.lastUpdateUtc)}
                            </span>
                            {isAssistant && message.content.trim().length > 0 && (
                              <button
                                type="button"
                                className={`btn btn-sm${isSelected ? ' btn-primary' : ''}`}
                                onClick={() => {
                                  setSelectedMessageId(message.id);
                                  seededDispatchKeyRef.current = '';
                                }}
                              >
                                {isSelected ? t('Selected For Dispatch') : t('Use For Dispatch')}
                              </button>
                            )}
                          </div>
                        </div>

                        <pre
                          style={{
                            margin: 0,
                            whiteSpace: 'pre-wrap',
                            wordBreak: 'break-word',
                            fontFamily: isUser ? 'var(--font-body)' : 'var(--mono)',
                            fontSize: '0.92rem',
                            lineHeight: 1.45,
                          }}
                        >
                          {message.content || (isAssistant ? t('Waiting for response...') : '')}
                        </pre>
                      </div>
                    );
                  })}
                </div>

                <div style={{ marginTop: '1rem' }}>
                  <label>{t('Send Message')}</label>
                  <textarea
                    value={composer}
                    onChange={(event) => setComposer(event.target.value)}
                    rows={6}
                    disabled={currentSession?.status !== 'Active' || sending}
                    placeholder={t('Describe the problem, ask for a plan, or negotiate the next steps with the captain.')}
                  />
                  <div className="form-actions" style={{ marginTop: '0.75rem' }}>
                    <button type="button" className="btn-primary" disabled={!canSend} onClick={handleSendMessage}>
                      {sending
                        ? t('Sending...')
                        : currentSession?.status === 'Responding'
                          ? t('Captain Responding...')
                          : t('Send')}
                    </button>
                  </div>
                </div>
              </div>

              <div className="card" style={{ padding: '1rem' }}>
                <div style={{ display: 'flex', justifyContent: 'space-between', gap: '1rem', alignItems: 'start', marginBottom: '0.75rem' }}>
                  <div>
                    <h3>{t('Dispatch From Session')}</h3>
                    <p className="text-muted">
                      {t('Select an assistant response, edit the resulting mission text if needed, and launch a voyage without leaving the session.')}
                    </p>
                  </div>
                  {selectedMessage && (
                    <span className="text-dim mono" style={{ fontSize: '0.8rem' }}>
                      {selectedMessage.id}
                    </span>
                  )}
                </div>

                <div className="dispatch-form">
                  <div className="form-group">
                    <label>{t('Voyage Title')}</label>
                    <input
                      value={dispatchTitle}
                      onChange={(event) => setDispatchTitle(event.target.value)}
                      placeholder={t('Optional override for the resulting voyage title')}
                    />
                  </div>

                  <div className="form-group">
                    <label>{t('Mission Description')}</label>
                    <textarea
                      value={dispatchDescription}
                      onChange={(event) => setDispatchDescription(event.target.value)}
                      rows={10}
                      placeholder={t('Select a planning response to seed the dispatch description.')}
                    />
                  </div>

                  <div className="form-actions">
                    <button type="button" className="btn-primary" disabled={!canDispatch} onClick={handleDispatch}>
                      {dispatching ? t('Dispatching...') : t('Dispatch')}
                    </button>
                  </div>
                </div>
              </div>
            </>
          )}
        </div>
      </div>
    </div>
  );
}
