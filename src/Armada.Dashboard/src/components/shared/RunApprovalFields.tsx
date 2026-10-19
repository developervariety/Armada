import type { RunApprovalOptions } from '../../types/models';
import { useAuth } from '../../context/AuthContext';
import { useLocale } from '../../context/LocaleContext';

export const DEFAULT_RUN_APPROVALS: RunApprovalOptions = { approveAll: false, timeoutSeconds: 600, timeoutDecision: 'Deny' };

/** Approval choices apply to this run and never modify a saved captain or pipeline. */
export default function RunApprovalFields({ value, onChange, disabled = false }: {
  value: RunApprovalOptions; onChange: (value: RunApprovalOptions) => void; disabled?: boolean;
}) {
  const { t } = useLocale();
  const { isAdmin, isTenantAdmin } = useAuth();
  const canApproveAutomatically = isAdmin || isTenantAdmin;
  return <fieldset className="endpoint-auth-section run-approval-fields" disabled={disabled}>
    <legend>{t('Approvals for this run')}</legend>
    <label className="checkbox-row">
      <input type="checkbox" checked={value.approveAll} disabled={!canApproveAutomatically} onChange={e => onChange({ ...value, approveAll: e.target.checked })} />
      <span>{t('Approve all')}</span>
    </label>
    <p className="text-dim">{t('Run tools and continue past human mission reviews without asking. Automated checks, endpoint restrictions, and merge/deployment policies still apply.')}</p>
    {!canApproveAutomatically && <p className="text-dim">{t('Automatic approval requires an administrator.')}</p>}
    {!value.approveAll && <div className="detail-form-grid">
      <label>{t('Approval timeout (seconds)')}
        <input type="number" required min={10} max={3600} step={1} value={value.timeoutSeconds} onChange={e => onChange({ ...value, timeoutSeconds: Number(e.target.value) })} />
      </label>
      <label>{t('Default answer on timeout')}
        <select value={value.timeoutDecision} onChange={e => onChange({ ...value, timeoutDecision: e.target.value as 'Deny' | 'Approve' })}>
          <option value="Deny">{t('Deny')}</option>
          {canApproveAutomatically && <option value="Approve">{t('Approve')}</option>}
        </select>
      </label>
    </div>}
  </fieldset>;
}
