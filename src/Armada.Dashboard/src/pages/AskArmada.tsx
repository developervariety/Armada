import { useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { chatWithCaptain, getCaptainTools, listCaptains } from '../api/client';
import type { Captain, CaptainChatMessage, CaptainChatMetrics, CaptainToolAccessResult } from '../types/models';
import { useLocale } from '../context/LocaleContext';
import ErrorModal from '../components/shared/ErrorModal';
import Markdown from '../components/shared/Markdown';
import ChatMetricsInfo from '../components/shared/ChatMetricsInfo';

interface ChatTurn {
  role: 'user' | 'assistant';
  text: string;
  metrics?: CaptainChatMetrics;
  model?: string | null;
}

// Ask Armada is available with any captain.
function isChattable(_captain: Captain): boolean {
  return true;
}

export default function AskArmada() {
  const { t } = useLocale();
  const navigate = useNavigate();
  const [captains, setCaptains] = useState<Captain[]>([]);
  const [captainId, setCaptainId] = useState('');
  const [turns, setTurns] = useState<ChatTurn[]>([]);
  const [input, setInput] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [tools, setTools] = useState<CaptainToolAccessResult | null>(null);
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

  // Detect whether the selected captain is connected to Armada over MCP so we can warn when it is not.
  useEffect(() => {
    if (!captainId) { setTools(null); return; }
    let active = true;
    setTools(null);
    getCaptainTools(captainId)
      .then((result) => { if (active) setTools(result); })
      .catch(() => { if (active) setTools(null); });
    return () => { active = false; };
  }, [captainId]);

  const selectedCaptain = captains.find((c) => c.id === captainId) || null;
  const armadaMcpMissing = tools != null && tools.armadaToolCount <= 0;

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
          <p className="text-dim view-subtitle">{t('Chat directly with a captain.')}</p>
        </div>
        <div>
          <label className="text-dim" style={{ display: 'flex', flexDirection: 'column', gap: '0.25rem', fontSize: '0.8rem' }}>
            {t('Captain')}
            <select value={captainId} onChange={(e) => setCaptainId(e.target.value)} disabled={busy}>
              {captains.length === 0 && <option value="">{t('No captains available')}</option>}
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
          <p className="text-dim">{t('No captains are configured yet. Create a captain to Ask Armada.')}</p>
        </div>
      ) : (
        <>
          {armadaMcpMissing && (
            <div className="mcp-warning-banner">
              <span className="mcp-warning-icon" aria-hidden="true">
                <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                  <path d="M10.29 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z" />
                  <path d="M12 9v4" />
                  <path d="M12 17h.01" />
                </svg>
              </span>
              <div className="mcp-warning-body">
                <strong>{t('This captain is not connected to Armada over MCP.')}</strong>
                <span className="text-dim">
                  {t('It cannot call Armada tools (fleet, missions, voyages, and more). Add the Armada MCP server to this captain’s runtime config to connect it.')}
                </span>
              </div>
              <div className="mcp-warning-actions">
                <button className="btn btn-sm" onClick={() => navigate('/captains/' + captainId)}>{t('View captain')}</button>
                <a className="btn btn-sm" href="https://github.com/jchristn/Armada/blob/main/docs/MCP_API.md" target="_blank" rel="noopener noreferrer">{t('How to connect')}</a>
              </div>
            </div>
          )}

          <div className="card" style={{ padding: '1rem', minHeight: '340px', display: 'flex', flexDirection: 'column', gap: '0.75rem' }}>
            {turns.length === 0 ? (
              <div className="text-dim" style={{ margin: 'auto', textAlign: 'center' }}>
                <p>{selectedCaptain ? t('Chatting with {{name}}', { name: selectedCaptain.name }) : t('Select a captain to begin.')}</p>
              </div>
            ) : (
              turns.map((turn, i) => (
                <div key={i} style={{ alignSelf: turn.role === 'user' ? 'flex-end' : 'flex-start', maxWidth: '85%' }}>
                  <div className="card" style={{ padding: '0.6rem 0.85rem', background: turn.role === 'user' ? 'var(--accent-soft, rgba(80,120,255,0.12))' : undefined }}>
                    <div className="text-dim chat-turn-header" style={{ fontSize: '0.7rem', marginBottom: '0.2rem' }}>
                      <span>{turn.role === 'user' ? t('You') : (turn.model || (selectedCaptain?.name ?? t('Captain')))}</span>
                      {turn.role === 'assistant' && turn.metrics && <ChatMetricsInfo metrics={turn.metrics} />}
                    </div>
                    {turn.role === 'assistant'
                      ? <Markdown>{turn.text}</Markdown>
                      : <div style={{ whiteSpace: 'pre-wrap' }}>{turn.text}</div>}
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
