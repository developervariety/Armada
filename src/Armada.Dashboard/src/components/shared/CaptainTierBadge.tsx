import { useLocale } from '../../context/LocaleContext';

interface CaptainTierBadgeProps {
  tier?: string | null;
  className?: string;
}

/**
 * Small, theme-aware badge for a captain's capability tier (Economy / Standard / Premium). The tier name is
 * always shown as text (not color-only) so it stays legible for color-blind users and under localization.
 */
export default function CaptainTierBadge({ tier, className }: CaptainTierBadgeProps) {
  const { t } = useLocale();
  if (!tier) return null;
  const key = tier.toLowerCase();
  return (
    <span
      className={`captain-tier-badge captain-tier-${key}${className ? ` ${className}` : ''}`}
      title={t('Capability tier: {{tier}}. Tiers rank captains Economy, Standard, Premium; a mission that sets a minimum tier is only given to captains at or above it.', { tier: t(tier) })}
    >
      {t(tier)}
    </span>
  );
}
