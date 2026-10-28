import { Link } from 'react-router-dom';
import { useLocale } from '../context/LocaleContext';

interface PasswordChangeBannerProps {
  passwordChangeRequired?: boolean;
}

/**
 * Informational notice that the signed-in account still has the default password. It links to the password
 * change and never blocks the session.
 */
export default function PasswordChangeBanner({ passwordChangeRequired }: PasswordChangeBannerProps) {
  const { t } = useLocale();
  if (!passwordChangeRequired) return null;

  return (
    <div className="alert alert-warning" role="status" style={{ marginTop: 0, marginBottom: '1rem' }}>
      {t('This account still uses the default password. Change it to protect this installation.')}{' '}
      <Link to="/server?tab=users&changePassword=1">{t('Change password')}</Link>
    </div>
  );
}
