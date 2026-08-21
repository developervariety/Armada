import { AUTO_REFRESH_OPTIONS } from '../../lib/useAutoRefresh';
import { useLocale } from '../../context/LocaleContext';

interface AutoRefreshSelectProps {
  seconds: number;
  onChange: (seconds: number) => void;
  className?: string;
}

/**
 * Compact dropdown for choosing a table's auto-refresh interval. Pairs with `useAutoRefresh`, which owns the
 * timer and per-table persistence. "None" disables auto-refresh.
 */
export default function AutoRefreshSelect({ seconds, onChange, className }: AutoRefreshSelectProps) {
  const { t } = useLocale();

  return (
    <label className={`auto-refresh-select${className ? ` ${className}` : ''}`} title={t('Auto-refresh interval')}>
      <span className="auto-refresh-select-label">{t('Auto-refresh')}</span>
      <select value={seconds} onChange={(event) => onChange(Number(event.target.value))} aria-label={t('Auto-refresh interval')}>
        {AUTO_REFRESH_OPTIONS.map((option) => (
          <option key={option} value={option}>
            {option === 0 ? t('None') : t('{{seconds}}s', { seconds: option })}
          </option>
        ))}
      </select>
    </label>
  );
}
