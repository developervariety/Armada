import type { CaptainChatMetrics } from '../../types/models';
import { useLocale } from '../../context/LocaleContext';

function fmtMs(ms: number | null | undefined): string {
  if (ms == null) return '-';
  if (ms >= 1000) return (ms / 1000).toFixed(2) + 's';
  return Math.round(ms) + 'ms';
}

/**
 * Compact per-turn statistics strip for a chat reply: time to first token, streaming time, tokens
 * per second, output token count, and total time. Shared by the Ask Armada and Planning chats.
 */
export default function ChatMetricsBar({ metrics }: { metrics: CaptainChatMetrics }) {
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
