import { useLocale } from '../../context/LocaleContext';
import type { ModelAvailability } from '../../lib/smartRouting';

interface Props {
  /** Accessible name of the list, for example "Worker default models". */
  label: string;
  value: string[];
  options: string[];
  availability: Record<string, ModelAvailability>;
  onChange: (value: string[]) => void;
  disabled?: boolean;
  /** Models in this list that no eligible captain for the persona can satisfy. */
  deadModels?: string[];
}

/** Captain count and usable-account state shown beside a model. */
export function ModelAvailabilityTags({ info }: { info: ModelAvailability | undefined }) {
  const { t } = useLocale();
  const captains = info?.captains ?? 0;
  return <>
    <span className="tag" title={t('Captains that run this model')}>
      {captains === 1 ? t('1 captain') : t('{{count}} captains', { count: captains })}
    </span>
    {info?.allExhausted && <span className="tag warn" role="note">{t('All accounts exhausted')}</span>}
    {info && info.exhausted > 0 && !info.allExhausted && <span className="tag">{t('{{count}} exhausted', { count: info.exhausted })}</span>}
  </>;
}

/** A multi-select of model ids shown as removable chips, with an add picker for the unselected options. */
export default function ModelChips({ label, value, options, availability, onChange, disabled, deadModels = [] }: Props) {
  const { t } = useLocale();
  const remaining = options.filter((m) => !value.includes(m));
  return <div className="model-chips" role="group" aria-label={label}>
    {value.map((model) => (
      <span key={model} className="model-chip">
        <span className="mono model-chip-name">{model}</span>
        <ModelAvailabilityTags info={availability[model]} />
        {deadModels.includes(model) && <span className="tag warn" role="note">{t('No eligible captain')}</span>}
        <button type="button" className="model-chip-remove" disabled={disabled}
          aria-label={t('Remove {{model}} from {{list}}', { model, list: label })}
          onClick={() => onChange(value.filter((m) => m !== model))}>×</button>
      </span>
    ))}
    {remaining.length > 0 && (
      <select aria-label={t('Add model to {{list}}', { list: label })} value="" disabled={disabled}
        onChange={(e) => { if (e.target.value) onChange([...value, e.target.value]); }}>
        <option value="">{t('Add model…')}</option>
        {remaining.map((model) => {
          const info = availability[model];
          const suffix = info?.allExhausted ? ` — ${t('all accounts exhausted')}` : '';
          return <option key={model} value={model}>{`${model} (${info?.captains ?? 0})${suffix}`}</option>;
        })}
      </select>
    )}
  </div>;
}
