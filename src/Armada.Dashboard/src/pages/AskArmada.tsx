import { useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { askArmada } from '../api/client';
import type { AskLink } from '../types/models';
import { useLocale } from '../context/LocaleContext';
import ErrorModal from '../components/shared/ErrorModal';

interface ChatTurn {
  role: 'user' | 'assistant';
  text: string;
  links?: AskLink[];
}

const SUGGESTIONS = ['status', 'how many captains?', 'any failures?', 'anything stalled?', 'missions', 'docks'];

export default function AskArmada() {
  const navigate = useNavigate();
  const { t } = useLocale();
  const [turns, setTurns] = useState<ChatTurn[]>([]);
  const [input, setInput] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const endRef = useRef<HTMLDivElement | null>(null);

  async function send(message: string) {
    const text = message.trim();
    if (!text || busy) return;
    setInput('');
    setTurns((current) => [...current, { role: 'user', text }]);
    setBusy(true);
    try {
      const response = await askArmada(text);
      setTurns((current) => [...current, { role: 'assistant', text: response.reply, links: response.links }]);
      setError('');
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Ask failed.'));
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
          <p className="text-dim view-subtitle">{t('Ask about fleet state in plain language. Read-only for now.')}</p>
        </div>
      </div>

      <ErrorModal error={error} onClose={() => setError('')} />

      <div className="card" style={{ padding: '1rem', minHeight: '340px', display: 'flex', flexDirection: 'column', gap: '0.75rem' }}>
        {turns.length === 0 ? (
          <div className="text-dim" style={{ margin: 'auto', textAlign: 'center' }}>
            <p>{t('Try asking:')}</p>
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: '0.5rem', justifyContent: 'center' }}>
              {SUGGESTIONS.map((s) => (
                <button key={s} className="btn" onClick={() => send(s)}>{s}</button>
              ))}
            </div>
          </div>
        ) : (
          turns.map((turn, i) => (
            <div key={i} style={{ alignSelf: turn.role === 'user' ? 'flex-end' : 'flex-start', maxWidth: '80%' }}>
              <div
                className="card"
                style={{
                  padding: '0.6rem 0.85rem',
                  background: turn.role === 'user' ? 'var(--accent-soft, rgba(80,120,255,0.12))' : undefined,
                }}
              >
                <div className="text-dim" style={{ fontSize: '0.7rem', marginBottom: '0.2rem' }}>
                  {turn.role === 'user' ? t('You') : t('Armada')}
                </div>
                <div style={{ whiteSpace: 'pre-wrap' }}>{turn.text}</div>
                {turn.links && turn.links.length > 0 && (
                  <div style={{ display: 'flex', flexWrap: 'wrap', gap: '0.4rem', marginTop: '0.5rem' }}>
                    {turn.links.map((link, j) => (
                      <button key={j} className="btn btn-sm" onClick={() => navigate(link.href)}>{link.label} {'→'}</button>
                    ))}
                  </div>
                )}
              </div>
            </div>
          ))
        )}
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
          placeholder={t('Ask about status, captains, missions, failures, docks...')}
          style={{ flex: 1 }}
          disabled={busy}
        />
        <button type="submit" className="btn btn-primary" disabled={busy || input.trim().length === 0}>
          {busy ? t('...') : t('Ask')}
        </button>
      </form>
    </div>
  );
}
