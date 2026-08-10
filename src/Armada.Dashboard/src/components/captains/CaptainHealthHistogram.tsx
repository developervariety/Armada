import type { CaptainHealthResponse } from '../../types/models';
import { useLocale } from '../../context/LocaleContext';

interface CaptainHealthHistogramProps {
  data?: CaptainHealthResponse | null;
  bars?: number;
  onClick?: (e: React.MouseEvent) => void;
}

/**
 * Compact clickable histogram of a captain's recent endpoint health checks. Each bar is one check,
 * oldest on the left; green means healthy, red means unhealthy. Renders a muted placeholder when no
 * checks have been recorded yet. Clicking does not propagate to the surrounding row.
 */
export default function CaptainHealthHistogram({ data, bars = 12, onClick }: CaptainHealthHistogramProps) {
  const { t } = useLocale();
  const results = data?.results ?? [];
  const recent = results.slice(Math.max(0, results.length - bars));

  function handleClick(e: React.MouseEvent) {
    e.stopPropagation();
    onClick?.(e);
  }

  if (recent.length === 0) {
    return (
      <button type="button" className="captain-health-histogram is-empty" onClick={handleClick} title={t('No health checks yet -- click for details')}>
        <span className="text-dim">{t('No checks')}</span>
      </button>
    );
  }

  const healthy = recent.filter((r) => r.healthy).length;
  return (
    <button
      type="button"
      className="captain-health-histogram"
      onClick={handleClick}
      title={t('{{healthy}}/{{total}} recent checks healthy -- click for details', { healthy, total: recent.length })}
    >
      {recent.map((r, index) => (
        <span
          key={`${r.checkedUtc}-${index}`}
          className={`captain-health-bar ${r.healthy ? 'ok' : 'bad'}`}
        />
      ))}
    </button>
  );
}
