import { type RefObject } from 'react';
import type { CoordinationMessage, CoordinationParticipant } from '../../types/models';

interface CoordinationChatCardProps {
  transcriptRef: RefObject<HTMLDivElement | null>;
  roomName: string;
  messages: CoordinationMessage[];
  participants: CoordinationParticipant[];
  composer: string;
  sending: boolean;
  canSend: boolean;
  formatDateTime: (value?: string | null) => string;
  formatRelativeTime: (value?: string | null) => string;
  onComposerChange: (value: string) => void;
  onSend: () => void;
}

/**
 * Presentational chat window for a coordination room: presence strip,
 * chronological message stream, and composer.
 */
export default function CoordinationChatCard({
  transcriptRef,
  roomName,
  messages,
  participants,
  composer,
  sending,
  canSend,
  formatDateTime,
  formatRelativeTime,
  onComposerChange,
  onSend,
}: CoordinationChatCardProps) {
  return (
    <div className="card">
      <div className="card-label">{roomName}</div>

      <div className="coordination-presence">
        {participants.length === 0 ? (
          <span className="text-dim">{`No one else is here right now.`}</span>
        ) : (
          participants.map((p) => (
            <span
              key={p.id}
              className="coordination-presence-chip"
              title={`Last seen ${formatDateTime(p.lastSeenUtc)}`}
            >
              <span className="coordination-presence-dot" />
              {p.displayName}
              <span className="text-dim">{formatRelativeTime(p.lastSeenUtc)}</span>
            </span>
          ))
        )}
      </div>

      <div ref={transcriptRef} className="planning-chat-window coordination-chat-window">
        {messages.length === 0 ? (
          <div className="planning-chat-empty text-muted">
            No notes yet. Post what you are working on so other sessions know.
          </div>
        ) : (
          messages.map((message) => {
            const isOperator = message.authorType.toLowerCase() === 'operator';
            const isSystem = message.authorType.toLowerCase() === 'system';

            return (
              <div
                key={message.id}
                className={`planning-chat-message coordination-chat-message-${isSystem ? 'system' : isOperator ? 'user' : 'assistant'}`}
              >
                <div className="planning-chat-message-meta">
                  <span className="planning-chat-role">{message.authorName}</span>
                  <span className="text-dim" title={formatDateTime(message.createdUtc)}>
                    {formatRelativeTime(message.createdUtc)}
                  </span>
                </div>
                <div className="planning-chat-bubble">
                  <pre className="planning-chat-content">{message.content}</pre>
                </div>
                {(message.voyageId || message.missionId || message.vesselId || message.incidentId) && (
                  <div className="coordination-message-refs text-dim mono">
                    {[
                      message.voyageId && `voyage ${message.voyageId}`,
                      message.missionId && `mission ${message.missionId}`,
                      message.vesselId && `vessel ${message.vesselId}`,
                      message.incidentId && `incident ${message.incidentId}`,
                    ]
                      .filter(Boolean)
                      .join(' · ')}
                  </div>
                )}
              </div>
            );
          })
        )}
      </div>

      <div className="planning-chat-composer">
        <label htmlFor="coordination-composer">Post to the board</label>
        <textarea
          id="coordination-composer"
          value={composer}
          onChange={(event) => onComposerChange(event.target.value)}
          rows={2}
          placeholder="Claim work before you start it. Report outcomes when you finish."
        />
        <div className="planning-chat-composer-actions">
          <span className="text-muted">Notes are visible to every session and the dashboard.</span>
          <button
            type="button"
            className="btn-primary"
            disabled={!canSend}
            onClick={onSend}
          >
            {sending ? 'Posting...' : 'Post'}
          </button>
        </div>
      </div>
    </div>
  );
}
