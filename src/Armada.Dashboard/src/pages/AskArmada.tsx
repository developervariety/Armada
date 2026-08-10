import { useEffect, useRef, useState } from 'react';
import { chatWithCaptain, listCaptains } from '../api/client';
import type { Captain, CaptainChatMessage, CaptainChatMetrics } from '../types/models';
import { useLocale } from '../context/LocaleContext';
import ErrorModal from '../components/shared/ErrorModal';
import Markdown from '../components/shared/Markdown';

interface ChatTurn {
  role: 'user' | 'assistant';
  text: string;
  metrics?: CaptainChatMetrics;
  model?: string | null;
}

// Direct chat is available for Mux captains (they carry a model endpoint); CLI runtimes do not.
function isChattable(captain: Captain): boolean {
  return captain.runtime === 'Mux';
}

function fmtMs(ms: number | null | undefined): string {
  if (ms == null) return '-';
  if (ms >= 1000) return (ms / 1000).toFixed(2) + 's';
  return Math.round(ms) + 'ms';
}

function MetricsBar({ metrics }: { metrics: CaptainChatMetrics }) {
  const { t } = useLocale();
  const items: Array<[string, string]> = [
    [t('time to first token'), fmtMs(metrics.timeToFirstTokenMs)],
    [t('streaming'), fmtMs(metrics.streamingMs)],
    [t('tokens/sec'), metrics.tokensPerSecond != null ? metrics.tokensPerSecond.toFixed(1) : '-'],
    [t('tokens'), metrics.completionTokens != null ? String(metrics.completionTokens) : '-'],
    [t('total'), fmtMs(metrics.totalMs)],
  ];
  return (
    <div className="chat-metrics text-dim">
      {items.map(([label, value]) => (
        <span key={label} className="chat-metric"><span className="chat-metric-value">{value}</span> {label}</span>
      ))}
    </div>
  );
}

export default function AskArmada() {
  const { t } = useLocale();
  const [captains, setCaptains] = useState<Captain[]>([]);
  const [captainId, setCaptainId] = useState('');
  const [turns, setTurns] = useState<ChatTurn[]>([]);
  const [input, setInput] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const endRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    listCaptains({ pageSize: 200 })
      .then((result) => {
        const chattable = result.objects.filter(isChattable);
        setCaptains(chattable);
        if (chattable.length > 0) setCaptainId((current) => current || chattable[0].id);
      })
      .catch((err: unknown) => setError(err instanceof Error ? err.message : t('Failed to load captains.')));
  }, [t]);

  const selectedCaptain = captains.find((c) => c.id === captainId) || null;

  async function send(message: string) {
    const text = message.trim();
    if (!text || busy || !captainId) return;
    setInput('');

    const history: CaptainChatMessage[] = turns.map((turn) => ({ role: turn.role, content: turn.text }));
    setTurns((current) => [...current, { role: 'user', text }]);
    setBusy(true);
    try {
      const response = await chatWithCaptain(captainId, { message: text, history });
      if (!response.success) {
        setError(response.error || t('The captain could not respond.'));
        setTurns((current) => current.slice(0, -1)); // drop the unanswered user turn
      } else {
        setTurns((current) => [...current, { role: 'assistant', text: response.reply, metrics: response.metrics, model: response.model }]);
      }
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Chat failed.'));
      setTurns((current) => current.slice(0, -1));
    } finally {
      setBusy(false);
      requestAnimationFrame(() => endRef.current?.scrollIntoView({ behavior: 'smooth' }));
    }
  }

  return (
    <div>
      <div className="view-header">
        <div>
          <h2>{t('Ask Armada')}</h2>
          <p className="text-dim view-subtitle">{t('Chat directly with a captain’s model. Each reply shows its timing and token statistics.')}</p>
        </div>
        <div>
          <label className="text-dim" style={{ display: 'flex', flexDirection: 'column', gap: '0.25rem', fontSize: '0.8rem' }}>
            {t('Captain')}
            <select value={captainId} onChange={(e) => setCaptainId(e.target.value)} disabled={busy}>
              {captains.length === 0 && <option value="">{t('No chattable captains')}</option>}
              {captains.map((c) => (
                <option key={c.id} value={c.id}>{c.name} ({c.model || c.runtime})</option>
              ))}
            </select>
          </label>
        </div>
      </div>

      <ErrorModal error={error} onClose={() => setError('')} />

      {captains.length === 0 ? (
        <div className="card" style={{ padding: '1.5rem', textAlign: 'center' }}>
          <p className="text-dim">{t('No captains with a chat endpoint are configured. Create a Mux captain with a model endpoint to chat here.')}</p>
        </div>
      ) : (
        <>
          <div className="card" style={{ padding: '1rem', minHeight: '340px', display: 'flex', flexDirection: 'column', gap: '0.75rem' }}>
            {turns.length === 0 ? (
              <div className="text-dim" style={{ margin: 'auto', textAlign: 'center' }}>
                <p>{selectedCaptain ? t('Chatting with {{name}}', { name: selectedCaptain.name }) : t('Select a captain to begin.')}</p>
              </div>
            ) : (
              turns.map((turn, i) => (
                <div key={i} style={{ alignSelf: turn.role === 'user' ? 'flex-end' : 'flex-start', maxWidth: '85%' }}>
                  <div className="card" style={{ padding: '0.6rem 0.85rem', background: turn.role === 'user' ? 'var(--accent-soft, rgba(80,120,255,0.12))' : undefined }}>
                    <div className="text-dim" style={{ fontSize: '0.7rem', marginBottom: '0.2rem' }}>
                      {turn.role === 'user' ? t('You') : (turn.model || (selectedCaptain?.name ?? t('Captain')))}
                    </div>
                    {turn.role === 'assistant'
                      ? <Markdown>{turn.text}</Markdown>
                      : <div style={{ whiteSpace: 'pre-wrap' }}>{turn.text}</div>}
                    {turn.role === 'assistant' && turn.metrics && <MetricsBar metrics={turn.metrics} />}
                  </div>
                </div>
              ))
            )}
            {busy && <div className="text-dim" style={{ alignSelf: 'flex-start' }}>{t('Thinking...')}</div>}
            <div ref={endRef} />
          </div>

          <form
            className="card"
            style={{ padding: '0.75rem', marginTop: '1rem', display: 'flex', gap: '0.5rem' }}
            onSubmit={(e) => { e.preventDefault(); send(input); }}
          >
            <input
              type="text"
              value={input}
              onChange={(e) => setInput(e.target.value)}
              placeholder={t('Message the captain...')}
              style={{ flex: 1 }}
              disabled={busy || !captainId}
            />
            <button type="submit" className="btn btn-primary" disabled={busy || !captainId || input.trim().length === 0}>
              {busy ? t('...') : t('Send')}
            </button>
          </form>
        </>
      )}
    </div>
  );
}
