import type { RunApprovalOptions } from '@dashboard/types/models';
import { useAuth } from '../../auth/AuthContext';
import { useLocale } from '../../i18n/LocaleContext';
import { AppText, SelectField, TextField } from '../ui';
import { SwitchField } from '../ui/SwitchField';

export const DEFAULT_RUN_APPROVALS: RunApprovalOptions = { approveAll: false, timeoutSeconds: 600, timeoutDecision: 'Deny' };
export function validRunApprovalOptions(value: RunApprovalOptions): boolean {
  return Number.isInteger(value.timeoutSeconds) && value.timeoutSeconds >= 10 && value.timeoutSeconds <= 3600;
}

/** Per-run choices; saved captain and pipeline policies remain untouched. */
export function RunApprovalFields({ value, onChange, disabled = false }: {
  value: RunApprovalOptions; onChange: (value: RunApprovalOptions) => void; disabled?: boolean;
}) {
  const { t } = useLocale();
  const { isAdmin, isTenantAdmin } = useAuth();
  const canApprove = isAdmin || isTenantAdmin;
  return <>
    <AppText variant="label">{t('Approvals for this run')}</AppText>
    <SwitchField label={t('Approve all')} value={value.approveAll} disabled={disabled || !canApprove}
      onChange={(approveAll) => { if (canApprove && !disabled) onChange({ ...value, approveAll }); }} testID="run-approve-all"
      hint={t('Run tools and continue past human mission reviews without asking. Automated checks, endpoint restrictions, and merge/deployment policies still apply.')} />
    {!canApprove ? <AppText muted>{t('Automatic approval requires an administrator.')}</AppText> : null}
    {!value.approveAll ? <>
      <TextField label={t('Approval timeout (seconds)')} value={String(value.timeoutSeconds)} keyboardType="number-pad" editable={!disabled}
        onChangeText={(text) => onChange({ ...value, timeoutSeconds: text === '' ? 0 : Number(text) })} testID="run-approval-timeout"
        error={validRunApprovalOptions(value) ? null : t('Approval timeout must be a whole number from 10 to 3600 seconds.')} />
      <SelectField label={t('Default answer on timeout')} value={value.timeoutDecision} disabled={disabled}
        placeholder={t('Default answer on timeout')} closeLabel={t('Close')} options={[{ value: 'Deny', label: t('Deny') }, ...(canApprove ? [{ value: 'Approve', label: t('Approve') }] : [])]}
        onChange={(timeoutDecision) => onChange({ ...value, timeoutDecision: timeoutDecision as 'Deny' | 'Approve' })} testID="run-approval-decision" />
    </> : null}
  </>;
}
