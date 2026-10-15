import { Link } from 'react-router-dom';
import type { Captain } from '../../types/models';
import { useLocale } from '../../context/LocaleContext';
import CaptainTierBadge from './CaptainTierBadge';

interface CaptainRefProps {
  captainId: string | null | undefined;
  captains: Captain[];
  /** Text shown when no captain is set (default "Auto (default routing)"). */
  autoLabel?: string;
  /** Show the captain's tier badge alongside the name (default true). */
  showTier?: boolean;
}

/**
 * Renders a captain reference the same way everywhere: a link to the captain by name with an optional tier
 * badge, the raw id in mono when the captain is not in the provided list, or a dimmed "auto" label when no
 * captain is set. Keeps the preferred/actual/default captain displays consistent across detail pages.
 */
export default function CaptainRef({ captainId, captains, autoLabel, showTier = true }: CaptainRefProps) {
  const { t } = useLocale();
  if (!captainId) return <span className="text-dim" title={t('No captain is pinned. Armada picks an available captain using persona and tier routing when the work is assigned.')}>{autoLabel ?? t('Auto (default routing)')}</span>;

  const captain = captains.find((c) => c.id === captainId);
  if (!captain) return <span className="mono" title={t('ID of a captain that is not in the loaded list. It may have been deleted, or belong to another user.')}>{captainId}</span>;

  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: '0.4rem', flexWrap: 'wrap' }}>
      <Link to={`/captains/${captain.id}`} title={t('Open captain {{name}} ({{runtime}}) to see its state, missions and settings.', { name: captain.name, runtime: captain.runtime })}>{captain.name}</Link>
      {showTier && captain.tier ? <CaptainTierBadge tier={captain.tier} /> : null}
    </span>
  );
}
