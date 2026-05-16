import type { PlanningSession } from '../../types/models';
import StatusBadge from '../shared/StatusBadge';

interface PlanningSessionListCardProps {
  t: (value: string, vars?: Record<string, string | number>) => string;
  sessions: PlanningSession[];
  activeSessionId?: string;
  formatRelativeTime: (value: string) => string;
  resolveCaptainName: (captainId: string) => string;
  resolveVesselName: (vesselId: string) => string;
  resolvePipelineName: (pipelineId: string | null) => string;
  onSelect: (sessionId: string) => void;
}

export default function PlanningSessionListCard(props: PlanningSessionListCardProps) {
  const {
    t,
    sessions,
    activeSessionId,
    formatRelativeTime,
    resolveCaptainName,
    resolveVesselName,
    resolvePipelineName,
    onSelect,
  } = props;

  return (
    <div className="card" style={{ padding: '1rem' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: '1rem', marginBottom: '0.75rem' }}>
        <div>
          <h3>{t('Recent Sessions')}</h3>
          <p className="text-muted">
            {t('Select a prior planning conversation to continue it or dispatch from it.')}
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
              </tr>
            </thead>
            <tbody>
              {sessions.map((session) => (
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
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
