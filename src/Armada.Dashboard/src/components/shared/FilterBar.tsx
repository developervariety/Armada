import { useLocale } from '../../context/LocaleContext';

interface FilterDef {
  key: string;
  label: string;
  type?: 'text' | 'select';
  options?: { value: string; label: string }[];
  placeholder?: string;
}

interface FilterBarProps {
  filters: FilterDef[];
  values: Record<string, string>;
  onChange: (key: string, value: string) => void;
  onClear?: () => void;
  /** Global search input */
  searchValue?: string;
  onSearchChange?: (value: string) => void;
}

export default function FilterBar({ filters, values, onChange, onClear, searchValue, onSearchChange }: FilterBarProps) {
  const { t } = useLocale();
  const hasValues = Object.values(values).some(v => v !== '');

  return (
    <div className="filter-bar">
      {onSearchChange !== undefined && (
        <input
          type="text"
          className="filter-input"
          placeholder={t('Search...')}
          aria-label={t('Search')}
          title={t('Show only rows that contain this text in any column.')}
          value={searchValue || ''}
          onChange={e => onSearchChange(e.target.value)}
          style={{ width: 180 }}
        />
      )}
      {filters.map(f => {
        if (f.type === 'select') {
          return (
            <select aria-label={f.label}
              key={f.key}
              className="filter-select"
              title={t('{{label}}: pick a value to show only matching rows. The first option shows all.', { label: f.label })}
              value={values[f.key] || ''}
              onChange={e => onChange(f.key, e.target.value)}
            >
              <option value="">{f.label}</option>
              {f.options?.map(opt => (
                <option key={opt.value} value={opt.value}>{opt.label}</option>
              ))}
            </select>
          );
        }
        return (
          <input
            key={f.key}
            type="text"
            className="filter-input"
            placeholder={f.placeholder || f.label}
            aria-label={f.label}
            title={t('{{label}}: show only rows whose value contains this text.', { label: f.label })}
            value={values[f.key] || ''}
            onChange={e => onChange(f.key, e.target.value)}
          />
        );
      })}
      {hasValues && onClear && (
        <button className="btn btn-sm" onClick={onClear} title={t('Clear every filter and the search, and show all rows again.')}>{t('Clear')}</button>
      )}
    </div>
  );
}
