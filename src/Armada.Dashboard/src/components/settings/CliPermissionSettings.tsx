import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { updateSettings } from '../../api/client';
import type { CliPermissionPolicy, CliPermissionSettingsData } from '../../types/models';
import { useLocale } from '../../context/LocaleContext';
import CliPermissionPolicySelect from '../cliPermissions/CliPermissionPolicySelect';
import { PROMPT_TIMEOUT_RANGE } from '../../lib/cliPermissions';
import { CLI_PERMISSION_DEFAULTS, validPromptTimeout } from '../../lib/settingsRanges';
import { cliPermissionsHref } from '../../lib/navTargets';

interface CliPermissionSettingsProps {
  permissions: CliPermissionSettingsData | null | undefined;
  /** Disable editing (remote proxy mode). */
  locked: boolean;
  /** Called with the full settings payload the server returned after a save. */
  onSaved: (updated: Record<string, unknown>) => void;
  /** Toast helper from the host page. */
  notify: (severity: 'success' | 'error', message: string) => void;
}

export { CLI_PERMISSION_DEFAULTS, validPromptTimeout } from '../../lib/settingsRanges';

interface Draft {
  askDefaultPolicy: CliPermissionPolicy;
  missionDefaultPolicy: CliPermissionPolicy;
  allowOwnerApproval: boolean;
  promptTimeoutSeconds: string;
  timeoutDecision: 'Deny' | 'Approve';
}

function toDraft(s: CliPermissionSettingsData | null | undefined): Draft {
  const v = { ...CLI_PERMISSION_DEFAULTS, ...(s ?? {}) };
  return {
    askDefaultPolicy: v.askDefaultPolicy,
    missionDefaultPolicy: v.missionDefaultPolicy,
    allowOwnerApproval: !!v.allowOwnerApproval,
    promptTimeoutSeconds: String(v.promptTimeoutSeconds),
    timeoutDecision: v.timeoutDecision ?? 'Deny',
  };
}

/**
 * Settings page section for CLI tool permissions (`Permissions`, global admin only): the default policy of Ask
 * conversations and of missions, whether conversation and mission owners may approve their own requests, and how long
 * a request waits for a decision. Choosing Bypass needs the strong warning confirmation.
 */
export default function CliPermissionSettings({ permissions, locked, onSaved, notify }: CliPermissionSettingsProps) {
  const { t } = useLocale();
  const [draft, setDraft] = useState<Draft>(() => toDraft(permissions));
  const [dirty, setDirty] = useState(false);
  const [saving, setSaving] = useState(false);

  useEffect(() => { if (!dirty) setDraft(toDraft(permissions)); }, [permissions, dirty]);

  const timeoutValid = validPromptTimeout(draft.promptTimeoutSeconds);

  function set<K extends keyof Draft>(field: K, value: Draft[K]) {
    setDraft((d) => ({ ...d, [field]: value }));
    setDirty(true);
  }

  async function save() {
    if (!timeoutValid) return;
    setSaving(true);
    try {
      const updated = await updateSettings({
        permissions: {
          askDefaultPolicy: draft.askDefaultPolicy,
          missionDefaultPolicy: draft.missionDefaultPolicy,
          allowOwnerApproval: draft.allowOwnerApproval,
          promptTimeoutSeconds: Number(draft.promptTimeoutSeconds),
          timeoutDecision: draft.timeoutDecision,
        },
      });
      setDirty(false);
      onSaved(updated as Record<string, unknown>);
      notify('success', t('CLI tool permission settings saved.'));
    } catch (e: unknown) {
      notify('error', t('Failed: {{message}}', { message: e instanceof Error ? e.message : t('Unknown error') }));
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="settings-section" style={{ marginTop: '1.5rem' }}>
      <h3>{t('CLI Tool Permissions')}</h3>
      <p className="text-muted" style={{ marginBottom: '0.75rem' }}>
        {t('How CLI captains handle shell commands, file edits, and fetches that need permission when neither the conversation nor the captain sets a policy. Refuse lets the CLI refuse them; Approve in Armada asks an approver here (Claude Code, Mux 1.5 or later, pi, and API endpoints; other runtimes fall back to Refuse); Bypass runs them without asking.')}
        {' '}
        <Link to={cliPermissionsHref()} title={t('Pending CLI tool requests and the allow and deny rules live on the CLI Tool Permissions tab of Settings.')}>{t('Requests and rules')}</Link>
      </p>
      <fieldset disabled={locked} className="settings-fieldset">
        <div className="settings-grid">
          <div className="form-group">
            <label htmlFor="cli-perm-ask-default" title={t('Policy for Ask conversation turns when neither the conversation nor its captain sets one (default Approve in Armada). Narrations and summaries refuse instead of asking.')}>{t('Ask conversation default')}</label>
            <CliPermissionPolicySelect
              id="cli-perm-ask-default"
              value={draft.askDefaultPolicy}
              onChange={(v) => set('askDefaultPolicy', v ?? CLI_PERMISSION_DEFAULTS.askDefaultPolicy)}
              allowInherit={false}
              allowBypass
              disabled={locked}
              ariaDescribedBy="cli-perm-ask-default-help"
              title={t('Policy for Ask conversation turns when neither the conversation nor its captain sets one (default Approve in Armada). Narrations and summaries refuse instead of asking.')}
            />
            <span id="cli-perm-ask-default-help" className="text-dim form-help">{t('Used by Ask conversation turns (default Approve in Armada). Narrations and summaries never ask; they refuse instead.')}</span>
          </div>
          <div className="form-group">
            <label htmlFor="cli-perm-mission-default" title={t('Policy for missions when neither the vessel auto-approve option nor the captain sets one (default Bypass, the behavior before CLI permissions).')}>{t('Mission default')}</label>
            <CliPermissionPolicySelect
              id="cli-perm-mission-default"
              value={draft.missionDefaultPolicy}
              onChange={(v) => set('missionDefaultPolicy', v ?? CLI_PERMISSION_DEFAULTS.missionDefaultPolicy)}
              allowInherit={false}
              allowBypass
              disabled={locked}
              ariaDescribedBy="cli-perm-mission-default-help"
              title={t('Policy for missions when neither the vessel auto-approve option nor the captain sets one (default Bypass, the behavior before CLI permissions).')}
            />
            <span id="cli-perm-mission-default-help" className="text-dim form-help">{t('Used by missions (default Bypass, the previous behavior). A vessel with auto-approve off caps missions at Refuse.')}</span>
          </div>
          <div className="form-group">
            <label htmlFor="cli-perm-timeout" title={t('Seconds a permission request waits for a decision before its default answer is applied (10-3600, default 600). Applies to new requests.')}>{t('Prompt timeout (seconds)')}</label>
            <input
              id="cli-perm-timeout"
              type="number"
              min={PROMPT_TIMEOUT_RANGE.min}
              max={PROMPT_TIMEOUT_RANGE.max}
              value={draft.promptTimeoutSeconds}
              onChange={(e) => set('promptTimeoutSeconds', e.target.value)}
              aria-invalid={!timeoutValid}
              aria-describedby="cli-perm-timeout-help"
              title={t('Seconds a permission request waits for a decision before its default answer is applied (10-3600, default 600). Applies to new requests.')}
            />
            <span id="cli-perm-timeout-help" className={timeoutValid ? 'text-dim form-help' : 'field-error'}>
              {timeoutValid
                ? t('How long a request waits for a decision before its default answer is applied (10-3600, default 600).')
                : t('Must be a whole number from {{min}} to {{max}}.', { min: PROMPT_TIMEOUT_RANGE.min, max: PROMPT_TIMEOUT_RANGE.max.toLocaleString() })}
            </span>
          </div>
          <div className="form-group">
            <label htmlFor="cli-perm-timeout-answer">{t('Default answer on timeout')}</label>
            <select id="cli-perm-timeout-answer" value={draft.timeoutDecision} onChange={e => set('timeoutDecision', e.target.value as 'Deny' | 'Approve')}>
              <option value="Deny">{t('Deny')}</option>
              <option value="Approve">{t('Approve')}</option>
            </select>
          </div>
          <div className="form-group">
            <label className="settings-checkbox" title={t('When on, the owner of a conversation or mission may allow once or deny its own requests. Admins can always decide; only admins can save rules.')}>
              <input type="checkbox" title={t('When on, the owner of a conversation or mission may allow once or deny its own requests. Admins can always decide; only admins can save rules.')} checked={draft.allowOwnerApproval} onChange={(e) => set('allowOwnerApproval', e.target.checked)} />
              {' '}{t('Let owners approve their own requests')}
            </label>
            <span className="text-dim form-help">{t('When on, the owner of a conversation or mission may allow or deny its requests. Admins and tenant admins can always decide; only they can create rules.')}</span>
          </div>
        </div>
        <div className="settings-actions">
          <button type="button" className="btn-primary btn-sm" onClick={() => void save()} disabled={!timeoutValid || saving || !dirty} title={t('Save this section to the Admiral. It applies immediately; other sections are saved separately.')}>
            {saving ? t('Saving...') : t('Save CLI Tool Permissions')}
          </button>
          {dirty && <button type="button" className="btn btn-sm" onClick={() => { setDirty(false); setDraft(toDraft(permissions)); }} title={t('Throw away the unsaved edits in this section and show the saved values again.')}>{t('Discard changes')}</button>}
          {dirty && <span className="text-dim">{t('Unsaved changes')}</span>}
        </div>
      </fieldset>
    </div>
  );
}
