import type { PlanningSession } from '../../types/models';
import StatusBadge from '../shared/StatusBadge';

interface PlanningSessionListCardProps {
  t: (value: string, vars?: Record<string, string | number>) => string;
  sessions: PlanningSession[];
  activeSessionId?: string;
  formatRelativeTime: (value: string) => string;
  onSelect: (sessionId: string) => void;
}

export default function PlanningSessionListCard(props: PlanningSessionListCardProps) {
  const { t, sessions, activeSessionId, formatRelativeTime, onSelect } = props;

  return (
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
              onClick={() => onSelect(session.id)}
              className="btn"
              style={{
                textAlign: 'left',
                padding: '0.75rem',
                borderColor: activeSessionId === session.id ? 'var(--accent)' : undefined,
                background: activeSessionId === session.id ? 'var(--surface-2)' : undefined,
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
  );
}
