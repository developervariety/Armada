import type { RefObject } from 'react';
import type { PlanningSessionMessage } from '../../types/models';
import StatusBadge from '../shared/StatusBadge';
import CaptainChatPanel, { type ChatTurn } from '../shared/CaptainChatPanel';
import { type ToolEvent } from '../shared/ChatToolChips';

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
  messageTools?: Record<string, ToolEvent[]>;
  messageThinking?: Record<string, string>;
  thinkingMessage?: string;
  streamingEnabled?: boolean;
  onStreamingChange?: (value: boolean) => void;
  showThinking?: boolean;
  onShowThinkingChange?: (value: boolean) => void;
  composer: string;
  sending: boolean;
  canSend: boolean;
  canEndSession: boolean;
  endingSession: boolean;
  deleting: boolean;
  formatDateTime: (value: string) => string;
  formatRelativeTime: (value: string) => string;
  /** Sends a specific assistant reply to the main Dispatch page (its full text becomes the dispatch prompt). */
  onOpenMessageInDispatch: (messageId: string) => void;
  onComposerChange: (value: string) => void;
  onSend: () => void;
  onStopTurn: () => void;
  onEndSession: () => void;
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
    messageTools,
    messageThinking,
    thinkingMessage,
    streamingEnabled = true,
    onStreamingChange,
    showThinking = false,
    onShowThinkingChange,
    composer,
    sending,
    canSend,
    canEndSession,
    endingSession,
    deleting,
    formatDateTime,
    formatRelativeTime,
    onOpenMessageInDispatch,
    onComposerChange,
    onSend,
    onStopTurn,
    onEndSession,
    onDelete,
  } = props;

  // Map the persisted planning transcript onto the shared chat-turn shape so the Planning chat renders
  // through the exact same panel as Ask Armada.
  const busy = currentStatus === 'Responding' || sending;
  const lastAssistantId = [...messages].reverse().find((m) => m.role.toLowerCase() === 'assistant')?.id;

  const turns: ChatTurn[] = messages.map((message) => {
    const role = message.role.toLowerCase();
    const kind: ChatTurn['role'] = role === 'user' ? 'user' : role === 'assistant' ? 'assistant' : 'system';
    return {
      id: message.id,
      role: kind,
      text: message.content,
      metrics: message.metrics ?? undefined,
      tools: kind === 'assistant' ? messageTools?.[message.id] : undefined,
      thinking: kind === 'assistant' ? messageThinking?.[message.id] : undefined,
      streaming: kind === 'assistant' && busy && message.id === lastAssistantId,
    };
  });

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
          <label className="ask-stream-toggle" title={t('Stream the reply as it is produced')}>
            <input type="checkbox" checked={streamingEnabled} onChange={(e) => onStreamingChange?.(e.target.checked)} disabled={busy} />
            {t('Stream responses')}
          </label>
          <label className="ask-stream-toggle" title={t('Surface the model reasoning above the answer. Mux streams it natively; other runtimes are asked to include it.')}>
            <input type="checkbox" checked={showThinking} onChange={(e) => onShowThinkingChange?.(e.target.checked)} disabled={busy} />
            {t('Show thinking')}
          </label>
          <button type="button" className="btn btn-sm" disabled={!canEndSession} onClick={onEndSession}>
            {endingSession || currentStatus === 'Stopping' ? t('Ending...') : t('End Session')}
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

      <div className="planning-chat-shell">
        <CaptainChatPanel
          t={t}
          turns={turns}
          windowRef={transcriptRef}
          notice={captainRuntime === 'Codex' ? t('Codex responses cannot be streamed and will arrive upon completion.') : undefined}
          assistantName={captainName}
          emptyState={<p>{t('No transcript yet. Send the first planning message below.')}</p>}
          input={composer}
          onInputChange={onComposerChange}
          onSend={onSend}
          onStop={onStopTurn}
          busy={busy}
          canSend={canSend}
          canStop={currentStatus === 'Responding'}
          thinking={thinkingMessage}
          inputPlaceholder={t('Describe the problem, ask for a plan, or negotiate the next steps with the captain.')}
          inputDisabled={currentStatus !== 'Active'}
          onClear={onDelete}
          clearDisabled={busy || deleting}
          renderTurnFooter={(turn) =>
            turn.role === 'assistant' && turn.id && turn.text.trim().length > 0 ? (
              <div className="planning-chat-message-actions">
                <button
                  type="button"
                  className="btn btn-sm btn-primary"
                  onClick={() => onOpenMessageInDispatch(turn.id!)}
                  title={t('Open the main Dispatch page with this reply as the prompt')}
                >
                  {t('Open in Dispatch')}
                </button>
              </div>
            ) : null
          }
        />
      </div>
    </div>
  );
}
