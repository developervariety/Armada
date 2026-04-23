import { useLocale } from '../context/LocaleContext';

export default function AdminBadge() {
  const { t } = useLocale();
  return <span className="badge badge-admin">{t('Admin')}</span>;
}
