import type { RefObject } from 'react';
import type { PlanningSessionMessage } from '../../types/models';

interface PlanningTranscriptCardProps {
  t: (value: string, vars?: Record<string, string | number>) => string;
  transcriptRef: RefObject<HTMLDivElement | null>;
  messages: PlanningSessionMessage[];
  selectedMessageId: string;
  currentStatus?: string;
  composer: string;
  sending: boolean;
  canSend: boolean;
  formatDateTime: (value: string) => string;
  formatRelativeTime: (value: string) => string;
  onSelectMessage: (messageId: string) => void;
  onComposerChange: (value: string) => void;
  onSend: () => void;
}

export default function PlanningTranscriptCard(props: PlanningTranscriptCardProps) {
  const {
    t,
    transcriptRef,
    messages,
    selectedMessageId,
    currentStatus,
    composer,
    sending,
    canSend,
    formatDateTime,
    formatRelativeTime,
    onSelectMessage,
    onComposerChange,
    onSend,
  } = props;
  const composerId = 'planning-session-composer';

  return (
    <div className="card" style={{ padding: '1rem' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', gap: '1rem', alignItems: 'center', marginBottom: '0.75rem' }}>
        <h3>{t('Transcript')}</h3>
        <span className="text-muted">{t('{{count}} message(s)', { count: messages.length })}</span>
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
        {messages.length === 0 ? (
          <div className="text-muted">{t('No transcript yet. Send the first planning message below.')}</div>
        ) : messages.map((message) => {
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
                      onClick={() => onSelectMessage(message.id)}
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
        <label htmlFor={composerId}>{t('Send Message')}</label>
        <textarea
          id={composerId}
          value={composer}
          onChange={(event) => onComposerChange(event.target.value)}
          rows={6}
          disabled={currentStatus !== 'Active' || sending}
          placeholder={t('Describe the problem, ask for a plan, or negotiate the next steps with the captain.')}
        />
        <div className="form-actions" style={{ marginTop: '0.75rem' }}>
          <button type="button" className="btn-primary" disabled={!canSend} onClick={onSend}>
            {sending
              ? t('Sending...')
              : currentStatus === 'Responding'
                ? t('Captain Responding...')
                : t('Send')}
          </button>
        </div>
      </div>
    </div>
  );
}
