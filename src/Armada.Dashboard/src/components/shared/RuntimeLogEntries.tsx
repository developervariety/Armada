import type { FormattedLogEntry, LogEntryKind } from '../../types/models';
import { useLocale } from '../../context/LocaleContext';

interface RuntimeLogEntriesProps {
  entries: FormattedLogEntry[];
  entriesTruncated: boolean;
}

/**
 * Readable runtime log. Each entry keeps its observed kind, so thinking, tool calls, tool results and status
 * events stay visually distinct from answer text. Kinds are runtime observations, not mission outcomes.
 */
export default function RuntimeLogEntries({ entries, entriesTruncated }: RuntimeLogEntriesProps) {
  const { t } = useLocale();
  const visible = entries.filter((entry) => !entry.dropped);

  const kindLabel = (entry: FormattedLogEntry): string | null => {
    const kind: LogEntryKind = entry.kind;
    const tool = entry.toolName ? `: ${entry.toolName}` : '';
    switch (kind) {
      case 'Thinking':
        return t('thinking');
      case 'ToolCall':
        return `${t('tool call')}${tool}`;
      case 'ToolResult':
        return `${t('tool result')}${tool}`;
      case 'Status':
        return t('status');
      case 'Mixed':
        return t('mixed');
      default:
        return entry.isToolCall && entry.toolName ? `${t('tool')}: ${entry.toolName}` : null;
    }
  };

  return (
    <div
      className="runtime-log-entries"
      style={{
        background: '#1a1a2e',
        color: '#e0e0e0',
        padding: 16,
        borderRadius: 'var(--radius)',
        overflow: 'auto',
        fontSize: 12,
        fontFamily: 'var(--mono)',
        maxHeight: '60vh',
      }}
    >
      {visible.length === 0 ? (
        <span className="text-dim">{t('(empty log)')}</span>
      ) : (
        visible.map((entry, index) => {
          const label = kindLabel(entry);
          return (
            <div
              key={index}
              data-log-kind={entry.kind}
              style={{
                whiteSpace: 'pre-wrap',
                wordBreak: 'break-word',
                padding: '1px 0',
                opacity: entry.kind === 'Thinking' || entry.kind === 'Status' ? 0.75 : 1,
              }}
            >
              {label ? (
                <span className="tag" style={{ marginRight: 6 }}>{label}</span>
              ) : null}
              <span>{entry.text}</span>
              {entry.redacted ? <span className="tag stalled" style={{ marginLeft: 6 }}>{t('redacted')}</span> : null}
              {entry.truncated ? <span className="tag" style={{ marginLeft: 6 }}>{t('truncated')}</span> : null}
            </div>
          );
        })
      )}
      {entriesTruncated ? (
        <p className="text-dim" style={{ marginTop: 8 }}>{t('Some entries were omitted at the page limit.')}</p>
      ) : null}
    </div>
  );
}
