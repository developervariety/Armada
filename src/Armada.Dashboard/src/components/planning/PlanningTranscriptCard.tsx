import type { RefObject } from 'react';
import type { PlanningSessionMessage } from '../../types/models';
import StatusBadge from '../shared/StatusBadge';

interface PlanningTranscriptCardProps {
  t: (value: string, vars?: Record<string, string | number>) => string;
  transcriptRef: RefObject<HTMLDivElement | null>;
  title: string;
  captainName: string;
  captainRuntime: string;
  vesselName: string;
  branchName: string | null;
  pipelineName: string;
  playbookCount: number;
  currentStatus?: string;
  failureReason?: string | null;
  updatedUtc: string;
  messages: PlanningSessionMessage[];
  selectedMessageId: string;
  composer: string;
  sending: boolean;
  canSend: boolean;
  canStop: boolean;
  stopping: boolean;
  deleting: boolean;
  formatDateTime: (value: string) => string;
  formatRelativeTime: (value: string) => string;
  onSelectMessage: (messageId: string) => void;
  onComposerChange: (value: string) => void;
  onSend: () => void;
  onStop: () => void;
  onDelete: () => void;
}

export default function PlanningTranscriptCard(props: PlanningTranscriptCardProps) {
  const {
    t,
    transcriptRef,
    title,
    captainName,
    captainRuntime,
    vesselName,
    branchName,
    pipelineName,
    playbookCount,
    currentStatus,
    failureReason,
    updatedUtc,
    messages,
    selectedMessageId,
    composer,
    sending,
    canSend,
    canStop,
    stopping,
    deleting,
    formatDateTime,
    formatRelativeTime,
    onSelectMessage,
    onComposerChange,
    onSend,
    onStop,
    onDelete,
  } = props;
  const composerId = 'planning-session-composer';

  return (
    <div className="card planning-current-session">
      <div className="planning-current-session-head">
        <div>
          <div className="card-label">{t('Current Session')}</div>
          <h3 style={{ marginTop: 0, marginBottom: '0.35rem' }}>{title}</h3>
          <p className="text-muted">
            {t('Chat with {{captain}} against {{vessel}}, keep the transcript intact, and promote the right reply into dispatch.', {
              captain: captainName,
              vessel: vesselName,
            })}
          </p>
        </div>
        <div className="planning-current-session-actions">
          {currentStatus && <StatusBadge status={currentStatus} />}
          <button type="button" className="btn btn-sm" disabled={!canStop} onClick={onStop}>
            {stopping || currentStatus === 'Stopping' ? t('Stopping...') : t('Stop Session')}
          </button>
          <button type="button" className="btn btn-sm" disabled={deleting} onClick={onDelete}>
            {deleting ? t('Deleting...') : t('Delete Session')}
          </button>
        </div>
      </div>

      <div className="planning-current-session-summary">
        <div className="planning-current-session-summary-item">
          <span>{t('Captain')}</span>
          <strong>{captainName}</strong>
        </div>
        <div className="planning-current-session-summary-item">
          <span>{t('Runtime')}</span>
          <strong>{captainRuntime || '-'}</strong>
        </div>
        <div className="planning-current-session-summary-item">
          <span>{t('Vessel')}</span>
          <strong>{vesselName}</strong>
        </div>
        <div className="planning-current-session-summary-item">
          <span>{t('Branch')}</span>
          <strong className="mono">{branchName || '-'}</strong>
        </div>
        <div className="planning-current-session-summary-item">
          <span>{t('Pipeline')}</span>
          <strong>{pipelineName}</strong>
        </div>
        <div className="planning-current-session-summary-item">
          <span>{t('Playbooks')}</span>
          <strong>{playbookCount}</strong>
        </div>
        <div className="planning-current-session-summary-item">
          <span>{t('Updated')}</span>
          <strong title={formatDateTime(updatedUtc)}>{formatRelativeTime(updatedUtc)}</strong>
        </div>
        <div className="planning-current-session-summary-item">
          <span>{t('Messages')}</span>
          <strong>{messages.length}</strong>
        </div>
      </div>

      {failureReason && (
        <div className="alert alert-error">
          {failureReason}
        </div>
      )}

      <div
        ref={transcriptRef}
        className="planning-chat-window"
      >
        {messages.length === 0 ? (
          <div className="planning-chat-empty text-muted">
            {t('No transcript yet. Send the first planning message below.')}
          </div>
        ) : messages.map((message) => {
          const role = message.role.toLowerCase();
          const isAssistant = role === 'assistant';
          const isUser = role === 'user';
          const isSelected = selectedMessageId === message.id;
          const roleLabel = isAssistant ? t('Captain') : isUser ? t('You') : t(message.role);

          return (
            <div
              key={message.id}
              className={`planning-chat-message planning-chat-message-${isUser ? 'user' : isAssistant ? 'assistant' : 'system'}${isSelected ? ' is-selected' : ''}`}
            >
              <div className="planning-chat-message-meta">
                <span className="planning-chat-role">{roleLabel}</span>
                <span className="text-dim" title={formatDateTime(message.lastUpdateUtc)}>
                  {formatRelativeTime(message.lastUpdateUtc)}
                </span>
              </div>

              <div className="planning-chat-bubble">
                <pre className="planning-chat-content">
                  {message.content || (isAssistant ? t('Waiting for response...') : '')}
                </pre>
              </div>

              {isAssistant && message.content.trim().length > 0 && (
                <div className="planning-chat-message-actions">
                  <button
                    type="button"
                    className={`btn btn-sm${isSelected ? ' btn-primary' : ''}`}
                    onClick={() => onSelectMessage(message.id)}
                  >
                    {isSelected ? t('Selected For Dispatch') : t('Use For Dispatch')}
                  </button>
                </div>
              )}
            </div>
          );
        })}
      </div>

      <div className="planning-chat-composer">
        <label htmlFor={composerId}>{t('Send Message')}</label>
        <textarea
          id={composerId}
          value={composer}
          onChange={(event) => onComposerChange(event.target.value)}
          rows={5}
          disabled={currentStatus !== 'Active' || sending}
          placeholder={t('Describe the problem, ask for a plan, or negotiate the next steps with the captain.')}
        />
        <div className="planning-chat-composer-actions">
          <span className="text-muted">
            {currentStatus === 'Active'
              ? t('Messages are appended to the preserved session transcript.')
              : t('This session is not currently accepting new messages.')}
          </span>
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
