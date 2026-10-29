import { Link } from 'react-router-dom';
import type { Mission } from '../../types/models';
import { useLocale } from '../../context/LocaleContext';

/** Statuses whose failure reason the setup wizard explains. */
export const FAILED_MISSION_STATUSES: ReadonlySet<string> = new Set(['Failed', 'LandingFailed']);

interface MissionFailureDetailsProps {
  mission: Mission;
}

/**
 * Shows why a mission failed and a link to its page. Used where the operator meets the mission
 * outside that page, such as the setup wizard.
 */
export default function MissionFailureDetails({ mission }: MissionFailureDetailsProps) {
  const { t } = useLocale();
  const reason = mission.failureReason?.trim();

  return (
    <div className="wizard-result wizard-result-error" role="alert">
      <strong>{mission.status === 'LandingFailed' ? t('The mission ran, but its work could not land.') : t('The mission failed.')}</strong>
      <div style={{ marginTop: '0.35rem' }}>
        <span className="text-dim">{t('Reason')}: </span>
        {reason || t('No reason was recorded.')}
      </div>
      <div style={{ marginTop: '0.6rem' }}>
        <Link to={`/missions/${mission.id}`}>{t('Open Mission')}</Link>
      </div>
    </div>
  );
}
