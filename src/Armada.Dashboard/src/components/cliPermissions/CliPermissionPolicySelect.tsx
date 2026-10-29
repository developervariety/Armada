import { useState } from 'react';
import type { CliPermissionPolicy } from '../../types/models';
import { useLocale } from '../../context/LocaleContext';
import ConfirmDialog from '../shared/ConfirmDialog';
import { bypassWarning, policyLabel } from '../../lib/cliPermissions';

interface CliPermissionPolicySelectProps {
  id?: string;
  /** Current value; null means Inherit. */
  value: CliPermissionPolicy | null;
  onChange: (value: CliPermissionPolicy | null) => void;
  /** Offer Inherit (captains and threads); server defaults have no Inherit. */
  allowInherit?: boolean;
  /** Offer Bypass (admins only). A current Bypass value is always shown so the select reflects the stored state. */
  allowBypass: boolean;
  disabled?: boolean;
  /** Label for Inherit, e.g. "Inherit (Approve in Armada)". */
  inheritLabel?: string;
  ariaLabel?: string;
  ariaDescribedBy?: string;
  className?: string;
  /** Hover text for the select; defaults to a summary of what each policy does. */
  title?: string;
}

/**
 * Picker for a CLI tool permission policy (Inherit / Refuse / Approve in Armada / Bypass). Choosing Bypass first shows
 * a strong warning that must be confirmed; cancelling keeps the previous value.
 */
export default function CliPermissionPolicySelect({
  id, value, onChange, allowInherit = true, allowBypass, disabled, inheritLabel, ariaLabel, ariaDescribedBy, className, title,
}: CliPermissionPolicySelectProps) {
  const { t } = useLocale();
  const [confirmBypass, setConfirmBypass] = useState(false);
  const showBypass = allowBypass || value === 'Bypass';

  function select(raw: string) {
    const next = raw ? (raw as CliPermissionPolicy) : null;
    if (next === value) return;
    if (next === 'Bypass') {
      if (!allowBypass) return;
      setConfirmBypass(true);
      return;
    }
    onChange(next);
  }

  return (
    <>
      <select
        id={id}
        className={className}
        value={value ?? ''}
        disabled={disabled}
        onChange={(e) => select(e.target.value)}
        aria-label={ariaLabel}
        aria-describedby={ariaDescribedBy}
        title={title ?? t('What happens when the CLI wants to run a command, edit a file, or fetch a URL that needs permission. Bypass runs everything without asking and needs confirmation.')}
      >
        {allowInherit && <option value="" title={t('Use the policy set at the next level up (captain, then the server default).')}>{inheritLabel ?? t('Inherit')}</option>}
        <option value="Refuse" title={t('The CLI declines any call that needs permission; the captain is told it was refused.')}>{policyLabel(t, 'Refuse')}</option>
        <option value="ApproveInArmada" title={t('Each call that needs permission waits for an approver to allow or deny it in Armada. Claude Code, Mux 1.5 or later, pi, and API endpoints; other runtimes fall back to Refuse.')}>{policyLabel(t, 'ApproveInArmada')}</option>
        {showBypass && <option value="Bypass" disabled={!allowBypass} title={allowBypass ? t('Every call runs immediately with no approval and no rules applied. Use only for fully trusted work.') : t('Only admins can choose Bypass.')}>{policyLabel(t, 'Bypass')}</option>}
      </select>
      <ConfirmDialog
        open={confirmBypass}
        title={t('Allow every CLI tool call without asking?')}
        message={bypassWarning(t)}
        confirmLabel={t('Use Bypass')}
        cancelLabel={t('Cancel')}
        danger
        onConfirm={() => { setConfirmBypass(false); onChange('Bypass'); }}
        onCancel={() => setConfirmBypass(false)}
      />
    </>
  );
}
