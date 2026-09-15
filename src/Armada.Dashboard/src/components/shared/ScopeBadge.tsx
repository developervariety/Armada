import { useLocale } from '../../context/LocaleContext';
import type { ScopeEnum } from '../../types/models';

interface ScopeBadgeProps {
  scope: ScopeEnum;
  className?: string;
}

/**
 * Small pill showing a record's ownership scope: "Personal" (user-specific) or "Tenant-wide".
 * Values match the server OwnershipScopeEnum, ScopeEnum, and MemoryScopeEnum.
 */
export default function ScopeBadge({ scope, className = '' }: ScopeBadgeProps) {
  const { t } = useLocale();
  const isPersonal = scope === 'UserSpecific';
  const label = isPersonal ? t('Personal') : t('Tenant-wide');
  const tooltip = isPersonal
    ? t('Owned by you. Only you (or an administrator) can edit or delete it.')
    : t('Shared across the tenant. Only a tenant administrator can edit or delete it.');
  return (
    <span
      className={`tag ${isPersonal ? 'userspecific' : 'tenantwide'} ${className}`.trim()}
      title={tooltip}
    >
      {label}
    </span>
  );
}
