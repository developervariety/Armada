import { useLocale } from '../context/LocaleContext';

export default function AdminBadge() {
  const { t } = useLocale();
  return <span className="badge badge-admin" title={t('System administrator: can manage every tenant, user, credential and server setting, not only this tenant\'s.')}>{t('Admin')}</span>;
}
