import { useLocale } from '../../context/LocaleContext';

export const EVALUATION_BADGE_TITLE = 'Evaluation work (fixture battery or model evaluation). Its stage outcomes never count as production data.';

/** Small "Evaluation" tag for missions and voyages flagged IsEvaluation; renders nothing otherwise. */
export default function EvaluationBadge({ isEvaluation }: { isEvaluation?: boolean | null }) {
  const { t } = useLocale();
  if (!isEvaluation) return null;
  return <span className="tag evaluation-badge" title={t(EVALUATION_BADGE_TITLE)}>{t('Evaluation')}</span>;
}
