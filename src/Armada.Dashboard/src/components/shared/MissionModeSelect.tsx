import type { MissionMode } from '../../types/models';
import { useLocale } from '../../context/LocaleContext';

interface MissionModeSelectProps {
  value: MissionMode;
  onChange: (mode: MissionMode) => void;
}

const MODES: Array<{ value: MissionMode; label: string }> = [
  { value: 'Implementation', label: 'Implementation' },
  { value: 'Audit', label: 'Audit (read-only)' },
  { value: 'Research', label: 'Research (read-only)' },
];

/**
 * Mission mode picker for create forms. The values are the server's mission modes. Audit and Research are
 * read-only: the server keeps its own completion and no-landing rules for them.
 */
export default function MissionModeSelect({ value, onChange }: MissionModeSelectProps) {
  const { t } = useLocale();
  const readOnly = value !== 'Implementation';

  return (
    <label>
      {t('Mode')}
      <select value={value} onChange={(event) => onChange(event.target.value as MissionMode)}>
        {MODES.map((mode) => (
          <option key={mode.value} value={mode.value}>{t(mode.label)}</option>
        ))}
      </select>
      {readOnly && (
        <span className="text-dim" style={{ display: 'block', fontSize: '0.8rem', marginTop: '0.25rem' }}>
          {t('Read-only: the captain reports findings and nothing is landed.')}
        </span>
      )}
    </label>
  );
}
