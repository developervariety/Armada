import StatusBadge from '../shared/StatusBadge';
import type { ObjectiveRefinementSession } from '../../types/models';

interface BacklogRefinementSessionListProps {
  sessions: ObjectiveRefinementSession[];
  activeSessionId: string;
  captainNameById: Map<string, string>;
  formatRelativeTime: (value: string | null | undefined) => string;
  onSelect: (sessionId: string) => void;
}

export default function BacklogRefinementSessionList({
  sessions,
  activeSessionId,
  captainNameById,
  formatRelativeTime,
  onSelect,
}: BacklogRefinementSessionListProps) {
  if (sessions.length < 1) {
    return (
      <div className="playbook-empty-state">
        <strong>No refinement sessions yet.</strong>
        <span>Choose a captain and start a lightweight refinement pass from this backlog item.</span>
      </div>
    );
  }

  return (
    <div className="backlog-session-list">
      {sessions.map((session) => (
        <button
          key={session.id}
          type="button"
          className={`backlog-session-item${activeSessionId === session.id ? ' active' : ''}`}
          onClick={() => onSelect(session.id)}
          title="Open this refinement session to read its transcript and apply its summary to the backlog item."
        >
          <div className="backlog-session-item-head">
            <strong>{session.title}</strong>
            <span title="Active is ready for your next message, Responding is the captain replying, Stopped or Completed means it has ended, Failed means it could not continue."><StatusBadge status={session.status} /></span>
          </div>
          <div className="backlog-session-item-meta">
            <span title="Captain running this refinement session.">{captainNameById.get(session.captainId) || session.captainId}</span>
            <span title="When this session last changed, such as a new message.">{formatRelativeTime(session.lastUpdateUtc)}</span>
          </div>
        </button>
      ))}
    </div>
  );
}
