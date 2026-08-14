import { useEffect, useRef, useState } from 'react';
import type { CaptainChatMetrics } from '../../types/models';
import { useLocale } from '../../context/LocaleContext';

function fmtMs(ms: number | null | undefined): string {
  if (ms == null) return '-';
  if (ms >= 1000) return (ms / 1000).toFixed(2) + 's';
  return Math.round(ms) + 'ms';
}

/**
 * Per-turn statistics shown behind an (i) affordance rather than a strip under every reply.
 * Time to first token, streaming time, tokens/sec, token count, and total time appear in a small
 * popover on click. Token count prefers completion tokens and falls back to the runtime's estimate.
 */
export default function ChatMetricsInfo({ metrics }: { metrics: CaptainChatMetrics }) {
  const { t } = useLocale();
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLSpanElement>(null);

  useEffect(() => {
    if (!open) return undefined;
    function onDown(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    }
    function onKey(e: globalThis.KeyboardEvent) {
      if (e.key === 'Escape') setOpen(false);
    }
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  const tokens = metrics.completionTokens ?? metrics.totalTokens;
  const rows: Array<[string, string]> = [
    [t('time to first token'), fmtMs(metrics.timeToFirstTokenMs)],
    [t('streaming'), fmtMs(metrics.streamingMs)],
    [t('tokens/sec'), metrics.tokensPerSecond != null ? metrics.tokensPerSecond.toFixed(1) : '-'],
    [t('tokens'), tokens != null ? String(tokens) : '-'],
    [t('total'), fmtMs(metrics.totalMs)],
  ];

  return (
    <span className="chat-metrics-info" ref={ref}>
      <button
        type="button"
        className="chat-metrics-info-btn"
        onClick={() => setOpen((v) => !v)}
        title={t('Turn statistics')}
        aria-label={t('Turn statistics')}
        aria-expanded={open}
      >
        <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
          <circle cx="12" cy="12" r="10" />
          <path d="M12 16v-4" />
          <path d="M12 8h.01" />
        </svg>
      </button>
      {open && (
        <span className="chat-metrics-popover" role="dialog" aria-label={t('Turn statistics')}>
          {rows.map(([label, value]) => (
            <span key={label} className="chat-metrics-row">
              <span className="chat-metrics-row-label">{label}</span>
              <span className="chat-metrics-row-value">{value}</span>
            </span>
          ))}
        </span>
      )}
    </span>
  );
}
