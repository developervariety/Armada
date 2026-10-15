import type { CaptainTier } from '../../types/models';
import { useLocale } from '../../context/LocaleContext';

const TIERS: CaptainTier[] = ['Economy', 'Standard', 'Premium'];

interface FallbackTierSelectProps {
  value: CaptainTier | null | undefined;
  onChange: (tier: CaptainTier | null) => void;
  disabled?: boolean;
  id?: string;
  ariaLabel?: string;
  className?: string;
  /** Hover text (already localized); defaults to an explanation of the fallback tier. */
  title?: string;
}

/**
 * Dropdown for the fallback capability tier used when a step's preferred captain is busy. "Auto" leaves the
 * fallback to the preferred captain's own tier (or normal routing when no preferred captain is set).
 */
export default function FallbackTierSelect({ value, onChange, disabled, id, ariaLabel, className, title }: FallbackTierSelectProps) {
  const { t } = useLocale();

  return (
    <select
      id={id}
      className={`fallback-tier-select${className ? ` ${className}` : ''}`}
      value={value ?? ''}
      disabled={disabled}
      aria-label={ariaLabel ?? t('Fallback tier')}
      title={title ?? t('Tier to fall back to when the preferred captain is busy, so another captain of that tier can take the work. Auto uses the preferred captain\'s own tier.')}
      onChange={(event) => onChange(event.target.value ? (event.target.value as CaptainTier) : null)}
    >
      <option value="">{t('Auto')}</option>
      {TIERS.map((tier) => (
        <option key={tier} value={tier}>{t(tier)}</option>
      ))}
    </select>
  );
}
