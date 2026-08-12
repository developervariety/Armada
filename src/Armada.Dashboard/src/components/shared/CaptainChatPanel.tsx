import { useEffect, useRef, type ReactNode, type RefObject } from 'react';
import type { CaptainChatMetrics } from '../../types/models';
import Markdown from './Markdown';
import ChatMetricsInfo from './ChatMetricsInfo';
import ChatToolChips, { type ToolEvent } from './ChatToolChips';

/**
 * One rendered turn in a captain chat. Shared by Ask Armada and the Planning current-session chat so
 * both surfaces render identically. `id` lets callers correlate a turn back to a persisted message.
 */
export interface ChatTurn {
  id?: string;
  role: 'user' | 'assistant' | 'system';
  text: string;
  metrics?: CaptainChatMetrics | null;
  model?: string | null;
  streaming?: boolean;
  tools?: ToolEvent[];
}

interface CaptainChatPanelProps {
  t: (value: string, vars?: Record<string, string | number>) => string;
  turns: ChatTurn[];
  /** Content shown when there are no turns yet (centered, dimmed). */
  emptyState?: ReactNode;
  /** Fallback display name for assistant turns that carry no model label. */
  assistantName?: string;
  input: string;
  onInputChange: (value: string) => void;
  onSend: () => void;
  /** Invoked to abort the in-flight generation. When omitted, no Stop button is shown. */
  onStop?: () => void;
  /** True while a reply is generating (shows the thinking line and the Stop button). */
  busy: boolean;
  canSend: boolean;
  canStop?: boolean;
  /** Rotating "waiting" message shown while busy. */
  thinking?: string;
  inputPlaceholder?: string;
  /** Disables the composer input independently of busy (e.g. no target selected / session inactive). */
  inputDisabled?: boolean;
  /** Optional per-turn footer (e.g. Planning's "Use For Dispatch" action) rendered under the bubble. */
  renderTurnFooter?: (turn: ChatTurn, index: number) => ReactNode;
  /** Optional external ref to the scrolling chat window (callers that need to scroll it themselves). */
  windowRef?: RefObject<HTMLDivElement | null>;
}

/**
 * The shared captain-chat surface: a scrolling window of aligned message bubbles (user right, assistant
 * left) with per-turn (i) statistics and tool-call chips, an "AI can make mistakes" disclaimer, and a
 * single-line composer whose action toggles between Send and Stop while a reply is generating. Ask Armada
 * is the gold standard for this layout; the Planning current-session chat renders through the same panel.
 */
export default function CaptainChatPanel(props: CaptainChatPanelProps) {
  const {
    t,
    turns,
    emptyState,
    assistantName,
    input,
    onInputChange,
    onSend,
    onStop,
    busy,
    canSend,
    canStop,
    thinking,
    inputPlaceholder,
    inputDisabled,
    renderTurnFooter,
    windowRef,
  } = props;

  const endRef = useRef<HTMLDivElement | null>(null);
  const lastText = turns.length > 0 ? turns[turns.length - 1].text : '';

  // Keep the newest content in view as turns stream in or the thinking line appears. (Optional-call so
  // environments without scrollIntoView, such as jsdom under test, are a no-op.)
  useEffect(() => {
    endRef.current?.scrollIntoView?.({ behavior: 'smooth' });
  }, [turns.length, lastText, busy, thinking]);

  function roleLabel(turn: ChatTurn): string {
    if (turn.role === 'user') return t('You');
    if (turn.role === 'system') return t('System');
    return turn.model || assistantName || t('Captain');
  }

  return (
    <div className="chat-panel">
      <div
        ref={windowRef}
        className="card ask-chat-window"
        style={{ padding: '1rem', display: 'flex', flexDirection: 'column', gap: '0.75rem' }}
      >
        {turns.length === 0 ? (
          <div className="text-dim" style={{ margin: 'auto', textAlign: 'center' }}>
            {emptyState ?? <p>{t('Send the first message to begin.')}</p>}
          </div>
        ) : (
          turns.map((turn, i) => (
            <div key={turn.id ?? i} style={{ alignSelf: turn.role === 'user' ? 'flex-end' : 'flex-start', maxWidth: '85%' }}>
              <div
                className="card"
                style={{ padding: '0.6rem 0.85rem', background: turn.role === 'user' ? 'var(--accent-soft, rgba(80,120,255,0.12))' : undefined }}
              >
                <div className="text-dim chat-turn-header" style={{ fontSize: '0.7rem', marginBottom: '0.2rem' }}>
                  <span>{roleLabel(turn)}</span>
                  {turn.role === 'assistant' && turn.metrics && <ChatMetricsInfo metrics={turn.metrics} />}
                </div>
                {turn.role === 'assistant' && (
                  <ChatToolChips
                    tools={turn.tools}
                    runningLabel={t('running…')}
                    argumentsLabel={t('Arguments')}
                    resultLabel={t('Result')}
                    noDetailsLabel={t('No details available.')}
                  />
                )}
                {turn.role === 'assistant'
                  ? <Markdown>{turn.text}</Markdown>
                  : <div style={{ whiteSpace: 'pre-wrap' }}>{turn.text}</div>}
              </div>
              {renderTurnFooter?.(turn, i)}
            </div>
          ))
        )}
        {busy && (
          <div key={thinking} className="text-dim ask-thinking" style={{ alignSelf: 'flex-start' }}>
            {thinking || t('Thinking...')}
          </div>
        )}
        <div ref={endRef} />
      </div>

      <p className="ask-disclaimer">{t('AI can make mistakes. Check answers.')}</p>

      <form
        className="ask-input-form"
        style={{ display: 'flex', gap: '0.5rem' }}
        onSubmit={(e) => { e.preventDefault(); if (!busy) onSend(); }}
      >
        <input
          type="text"
          value={input}
          onChange={(e) => onInputChange(e.target.value)}
          placeholder={inputPlaceholder ?? t('Message the captain...')}
          style={{ flex: 1 }}
          disabled={busy || inputDisabled}
        />
        {busy && onStop ? (
          <button type="button" className="btn" onClick={onStop} disabled={canStop === false}>
            {t('Stop')}
          </button>
        ) : (
          <button type="submit" className="btn btn-primary" disabled={!canSend}>
            {t('Send')}
          </button>
        )}
      </form>
    </div>
  );
}
