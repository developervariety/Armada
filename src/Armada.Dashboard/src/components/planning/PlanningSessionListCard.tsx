import { useState } from 'react';
import type { PlanningSession } from '../../types/models';
import StatusBadge from '../shared/StatusBadge';
import ActionMenu from '../shared/ActionMenu';

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
  onDeleteSession: (session: PlanningSession) => void;
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
    onDeleteSession,
  } = props;

  // Recent Sessions is a supporting list, not the primary workspace, so it starts collapsed.
  const [expanded, setExpanded] = useState(false);

  return (
    <div className="card" style={{ padding: '1rem' }}>
      <button
        type="button"
        className="collapsible-header"
        onClick={() => setExpanded((prev) => !prev)}
        aria-expanded={expanded}
      >
        <span className="collapsible-caret" aria-hidden="true">{expanded ? '▾' : '▸'}</span>
        <span className="collapsible-title">{t('Recent Sessions')}</span>
        <span className="text-muted" style={{ marginLeft: 'auto' }}>{t('{{count}} total', { count: sessions.length })}</span>
      </button>

      {expanded && (
        sessions.length === 0 ? (
          <p className="text-muted" style={{ marginTop: '0.75rem' }}>{t('No planning sessions yet.')}</p>
        ) : (
          <div className="table-wrap" style={{ marginTop: '0.75rem' }}>
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

                  const items = [];
                  if (canEndSession || ending) {
                    items.push({
                      label: ending ? t('Ending...') : t('End Session'),
                      onClick: () => { if (!ending) onEndSession(session); },
                    });
                  }
                  items.push({
                    label: t('Delete'),
                    danger: true,
                    onClick: () => onDeleteSession(session),
                  });

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
                          <ActionMenu id={`planning-session-${session.id}`} items={items} />
                        </div>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )
      )}
    </div>
  );
}
