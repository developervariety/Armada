import type { PlanningSession } from '../../types/models';
import StatusBadge from '../shared/StatusBadge';

interface PlanningSessionListCardProps {
  t: (value: string, vars?: Record<string, string | number>) => string;
  sessions: PlanningSession[];
  activeSessionId?: string;
  endingSessionId?: string | null;
  formatRelativeTime: (value: string) => string;
  resolveCaptainName: (captainId: string) => string;
  resolveVesselName: (vesselId: string) => string;
  resolvePipelineName: (pipelineId: string | null) => string;
  onSelect: (sessionId: string) => void;
  onEndSession: (session: PlanningSession) => void;
}

export default function PlanningSessionListCard(props: PlanningSessionListCardProps) {
  const {
    t,
    sessions,
    activeSessionId,
    endingSessionId,
    formatRelativeTime,
    resolveCaptainName,
    resolveVesselName,
    resolvePipelineName,
    onSelect,
    onEndSession,
  } = props;

  return (
    <div className="card" style={{ padding: '1rem' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: '1rem', marginBottom: '0.75rem' }}>
        <div>
          <h3>{t('Recent Sessions')}</h3>
          <p className="text-muted">
            {t('Select a prior planning conversation to continue it, dispatch from it, or end an active session.')}
          </p>
        </div>
        <span className="text-muted">{t('{{count}} total', { count: sessions.length })}</span>
      </div>

      {sessions.length === 0 ? (
        <p className="text-muted">{t('No planning sessions yet.')}</p>
      ) : (
        <div className="table-wrap">
          <table className="planning-session-table">
            <thead>
              <tr>
                <th>{t('Title')}</th>
                <th>{t('Captain')}</th>
                <th>{t('Vessel')}</th>
                <th>{t('Pipeline')}</th>
                <th>{t('Status')}</th>
                <th>{t('Updated')}</th>
                <th>{t('Actions')}</th>
              </tr>
            </thead>
            <tbody>
              {sessions.map((session) => {
                const canEndSession = session.status === 'Active' || session.status === 'Responding';
                const ending = endingSessionId === session.id || session.status === 'Stopping';

                return (
                  <tr
                    key={session.id}
                    className={`clickable planning-session-row${activeSessionId === session.id ? ' is-active' : ''}`}
                    onClick={() => onSelect(session.id)}
                  >
                    <td>
                      <div className="planning-session-title">{session.title}</div>
                      <div className="planning-session-subtitle mono">{session.id}</div>
                    </td>
                    <td>{resolveCaptainName(session.captainId)}</td>
                    <td>{resolveVesselName(session.vesselId)}</td>
                    <td>{resolvePipelineName(session.pipelineId)}</td>
                    <td><StatusBadge status={session.status} /></td>
                    <td>{formatRelativeTime(session.lastUpdateUtc)}</td>
                    <td>
                      <div className="planning-session-actions">
                        {(canEndSession || ending) ? (
                          <button
                            type="button"
                            className="btn btn-sm"
                            disabled={ending}
                            onClick={(event) => {
                              event.stopPropagation();
                              if (!ending) {
                                onEndSession(session);
                              }
                            }}
                          >
                            {ending ? t('Ending...') : t('End Session')}
                          </button>
                        ) : (
                          <span className="text-muted">-</span>
                        )}
                      </div>
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
