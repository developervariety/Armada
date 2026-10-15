import { useLocale } from '../../context/LocaleContext';
import type { ScopeEnum } from '../../types/models';
import { canChooseScope, type ScopeViewer } from '../../lib/scoping';

interface ScopeSelectProps {
  viewer: ScopeViewer;
  value: ScopeEnum;
  onChange: (scope: ScopeEnum) => void;
  /** Label to render; defaults to "Visibility". */
  label?: string;
}

/**
 * Ownership-scope picker for Category B create/edit forms. Admins may choose between tenant-wide and
 * personal; regular users are locked to personal (they can only own user-specific objects), shown as a
 * read-only hint instead of a dropdown.
 */
export default function ScopeSelect({ viewer, value, onChange, label }: ScopeSelectProps) {
  const { t } = useLocale();
  const heading = label ?? t('Visibility');

  if (!canChooseScope(viewer)) {
    return (
      <label>{heading}
        <input type="text" value={t('Personal (only you can see and edit it)')} readOnly disabled title={t('Only an administrator can share items tenant-wide, so what you create here is personal: visible to you and to administrators.')} />
      </label>
    );
  }

  return (
    <label>{heading}
      <select value={value} onChange={(event) => onChange(event.target.value as ScopeEnum)} title={t('Who can use this item. Tenant-wide: everyone in the tenant can use it, and only tenant admins can edit it. Personal: only you (and administrators) can see and edit it.')}>
        <option value="TenantWide">{t('Tenant-wide (everyone in the tenant can use it)')}</option>
        <option value="UserSpecific">{t('Personal (only you can see and edit it)')}</option>
      </select>
    </label>
  );
}
