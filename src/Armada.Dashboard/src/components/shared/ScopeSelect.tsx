import { useLocale } from '../../context/LocaleContext';
import type { ScopeEnum } from '../../types/models';
import { canChooseScope, type ScopeViewer } from '../../lib/scoping';

interface ScopeSelectProps {
  viewer: ScopeViewer;
  value: ScopeEnum;
  onChange: (scope: ScopeEnum) => void;
  /** Label to render; defaults to "Visibility". */
  label?: string;
  /**
   * True when the scope of an existing record is shown. The server fixes ownership scope at
   * creation, so the value is displayed read-only.
   */
  locked?: boolean;
}

/**
 * Ownership-scope picker for create forms. Administrators choose between tenant-wide and personal;
 * regular users are locked to personal, shown as a read-only hint. An existing record's scope is
 * always read-only.
 */
export default function ScopeSelect({ viewer, value, onChange, label, locked = false }: ScopeSelectProps) {
  const { t } = useLocale();
  const heading = label ?? t('Visibility');
  const personal = t('Personal (only you can see and edit it)');
  const tenantWide = t('Tenant-wide (everyone in the tenant can use it)');

  if (locked) {
    return (
      <label>{heading}
        <input type="text" value={value === 'UserSpecific' ? personal : tenantWide} readOnly disabled title={t('Visibility is set when the record is created.')} />
      </label>
    );
  }

  if (!canChooseScope(viewer)) {
    return (
      <label>{heading}
        <input type="text" value={personal} readOnly disabled />
      </label>
    );
  }

  return (
    <label>{heading}
      <select value={value} onChange={(event) => onChange(event.target.value as ScopeEnum)}>
        <option value="TenantWide">{tenantWide}</option>
        <option value="UserSpecific">{personal}</option>
      </select>
    </label>
  );
}
