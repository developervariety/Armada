import CopyButton from './CopyButton';
import Markdown from './Markdown';
import { useLocale } from '../../context/LocaleContext';

interface MissionDescriptionCardProps {
  description: string;
}

/**
 * Mission description rendered as GitHub-flavored markdown. Raw HTML in the description is not rendered as
 * elements. The copy button copies the raw markdown source, so what an operator pastes matches the stored brief.
 */
export default function MissionDescriptionCard({ description }: MissionDescriptionCardProps) {
  const { t } = useLocale();

  return (
    <div style={{ marginTop: '1rem' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: '0.5rem' }}>
        <h3 style={{ margin: 0 }}>{t('Description')}</h3>
        <CopyButton text={description} title={t('Copy raw markdown')} />
      </div>
      <div className="card" style={{ padding: '1rem', marginTop: '0.5rem' }}>
        <Markdown>{description}</Markdown>
      </div>
    </div>
  );
}
