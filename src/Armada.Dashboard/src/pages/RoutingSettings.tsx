import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { getSettings, listCaptains, listPersonas, updateSettings } from '../api/client';
import type { Captain } from '../types/models';
import UsageRoutingEditor, { emptyUsageRouting } from '../components/UsageRoutingEditor';
import RoutingPolicyEditor from '../components/RoutingPolicyEditor';
import SubscriptionAccountsPanel from '../components/SubscriptionAccountsPanel';
import type { PolicyRecord } from '../lib/subscriptionAccounts';
import PageHeader from '../components/shared/PageHeader';
import AutoRefreshSelect from '../components/shared/AutoRefreshSelect';
import RefreshButton from '../components/shared/RefreshButton';
import { useAutoRefresh } from '../lib/useAutoRefresh';
import { useLocale } from '../context/LocaleContext';
import { useProxySessionContext } from '../lib/useProxySessionContext';
import { mergeDraft } from '../lib/settingsDraft';
import type { DeadPersonaModelEntry } from '../lib/smartRouting';

type SettingsRecord = Record<string, unknown>;

function usageRoutingText(settings: SettingsRecord): string {
  const modelTier = settings.modelTier as SettingsRecord | undefined;
  return JSON.stringify(modelTier?.usageRouting ?? emptyUsageRouting, null, 2);
}

function providerUsage(settings: SettingsRecord): Array<SettingsRecord> {
  return Array.isArray(settings.providerUsage) ? settings.providerUsage as Array<SettingsRecord> : [];
}

function deadEntries(settings: SettingsRecord | null): DeadPersonaModelEntry[] {
  const health = settings?.personaModelHealth as Record<string, unknown> | undefined;
  const raw = health?.dead;
  if (!Array.isArray(raw)) return [];
  return raw.map((entry) => {
    const row = (entry && typeof entry === 'object' ? entry : {}) as Record<string, unknown>;
    return {
      persona: String(row.persona ?? ''),
      list: String(row.list ?? ''),
      model: String(row.model ?? ''),
    };
  }).filter((entry) => entry.persona && entry.list && entry.model);
}

function healthSummary(settings: SettingsRecord | null): string {
  const health = settings?.personaModelHealth as Record<string, unknown> | undefined;
  return typeof health?.summary === 'string' ? health.summary : '';
}

/**
 * One routing configuration surface with two independent parts. The model routing policy edits tier
 * lists, specialist personas, reserved slots, strategy, preference order, family rules, the voyage
 * dispatch guard, model providers, and additional assets. Smart Routing edits `modelTier.usageRouting`
 * only. Each part keeps its own draft and saves only its own fields, so saving one never overwrites
 * the other, and a reload keeps both parts' unsaved edits.
 */
export default function RoutingSettings() {
  const { t } = useLocale();
  const proxyContext = useProxySessionContext();
  const remoteProxyMode = Boolean(proxyContext?.selectedInstanceId);
  const [saved, setSaved] = useState<SettingsRecord | null>(null);
  const [policy, setPolicy] = useState(JSON.stringify(emptyUsageRouting, null, 2));
  const policyBaseRef = useRef(policy);
  const policyRef = useRef(policy);
  policyRef.current = policy;
  const [statuses, setStatuses] = useState<Array<SettingsRecord>>([]);
  const [loading, setLoading] = useState(true);
  const [loadAttempt, setLoadAttempt] = useState(0);
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');
  const [captains, setCaptains] = useState<Captain[]>([]);
  const [personas, setPersonas] = useState<string[]>([]);

  const applySaved = useCallback((settings: SettingsRecord, sentUsageRouting?: string) => {
    const next = usageRoutingText(settings);
    const base = sentUsageRouting ?? policyBaseRef.current;
    policyBaseRef.current = next;
    setPolicy((current) => mergeDraft(base, current, next));
    setStatuses(providerUsage(settings));
    setSaved(settings);
  }, []);

  const load = useCallback(async () => {
    setError('');
    try {
      applySaved(await getSettings() as SettingsRecord);
    } catch (e) {
      setError(String(e));
    } finally {
      setLoading(false);
    }
  }, [applySaved]);

  // Captains and personas only feed the model options and pickers; a failure leaves them empty.
  const loadRoster = useCallback(async () => {
    const [captainResult, personaResult] = await Promise.all([
      listCaptains({ pageSize: 9999 }).catch(() => null),
      listPersonas({ pageSize: 9999 }).catch(() => null),
    ]);
    if (captainResult?.objects) setCaptains(captainResult.objects);
    if (personaResult?.objects) setPersonas(personaResult.objects.filter(p => p.active !== false).map(p => p.name));
  }, []);

  useEffect(() => { void loadRoster(); }, [loadRoster]);

  useEffect(() => {
    setLoading(true);
    void load();
  }, [load, loadAttempt]);

  const { seconds: refreshSeconds, setSeconds: setRefreshSeconds } = useAutoRefresh('routing', () => { if (saved) void load(); });

  const saveUsageRouting = async () => {
    setSaving(true); setMessage(''); setError('');
    const sent = policy;
    try {
      const usageRouting: unknown = JSON.parse(sent);
      if (!usageRouting || typeof usageRouting !== 'object' || Array.isArray(usageRouting)) throw new Error(t('Policy must be a JSON object.'));
      const result = await updateSettings({ modelTier: { usageRouting } }) as SettingsRecord;
      applySaved(result, sent);
      const summary = healthSummary(result);
      setMessage(deadEntries(result).length > 0 && summary
        ? t('Smart Routing settings saved.') + ' ' + summary
        : t('Smart Routing settings saved.'));
    } catch (e) { setError(e instanceof Error ? e.message : String(e)); }
    finally { setSaving(false); }
  };

  /**
   * Save one account change made by the guided panel. The change is applied to the saved policy, so unsaved JSON
   * edits are never sent with it; an untouched draft adopts the result, and an edited draft receives the same change.
   */
  const saveAccountChange = useCallback(async (mutate: (policy: PolicyRecord) => PolicyRecord) => {
    const usageRouting = mutate(JSON.parse(policyBaseRef.current) as PolicyRecord);
    const draft = policyRef.current;
    const draftDirty = draft !== policyBaseRef.current;
    let draftNext = draft;
    if (draftDirty) {
      try { draftNext = JSON.stringify(mutate(JSON.parse(draft) as PolicyRecord), null, 2); }
      catch { /* Invalid draft text stays as typed. */ }
    }
    const result = await updateSettings({ modelTier: { usageRouting } }) as SettingsRecord;
    const next = usageRoutingText(result);
    policyBaseRef.current = next;
    setPolicy(current => current !== draft ? current : draftDirty ? draftNext : next);
    setStatuses(providerUsage(result));
    setSaved(result);
    setMessage(t('Subscription account saved.'));
  }, [t]);

  const usageDirty = policy !== policyBaseRef.current;
  let savedPolicy: PolicyRecord | null = null;
  try { savedPolicy = JSON.parse(policyBaseRef.current) as PolicyRecord; } catch { /* The saved text is always server JSON. */ }

  return <div>
    <PageHeader
      title={t('Routing')}
      subtitle={t('Legacy Routing picks captains by capability tier and preference rank. Smart Routing adds usage, persona model lists, and the capacity decision. Each part saves only its own settings.')}
      actions={saved ? (
        <>
          <AutoRefreshSelect seconds={refreshSeconds} onChange={setRefreshSeconds} />
          <RefreshButton onRefresh={() => { void loadRoster(); return load(); }} title={t('Refresh routing settings')} />
        </>
      ) : undefined}
    />
    {loading && !saved ? <p>{t('Loading settings...')}</p> : !saved ?
      <button className="btn btn-secondary" type="button" onClick={() => setLoadAttempt(attempt => attempt + 1)}>{t('Retry loading settings')}</button> : <>
      {remoteProxyMode && <p>{t('Edit routing on the Admiral directly. Settings changes are disabled in remote proxy mode.')}</p>}
      <RoutingPolicyEditor saved={saved} disabled={remoteProxyMode} onSaved={(settings) => applySaved(settings)} />
      <SubscriptionAccountsPanel savedPolicy={savedPolicy} statuses={statuses} disabled={remoteProxyMode || saving}
        onSavePolicy={saveAccountChange} onRefresh={load} />
      <fieldset disabled={remoteProxyMode || saving} style={{ border: 0, padding: 0, minWidth: 0 }}>
        <UsageRoutingEditor value={policy} onChange={value => { setPolicy(value); setMessage(''); }} statuses={statuses}
          personas={personas} captains={captains} deadEntries={deadEntries(saved)} />
        <div style={{ display: 'flex', gap: '0.75rem', marginTop: '1rem', alignItems: 'center' }}>
          <button className="btn btn-primary" type="button" onClick={saveUsageRouting}>{saving ? t('Saving...') : t('Save routing policy')}</button>
          <button className="btn btn-secondary" type="button" onClick={() => setPolicy(policyBaseRef.current)} disabled={!usageDirty}>{t('Discard changes')}</button>
          {usageDirty && <span className="text-muted">{t('Unsaved changes. A refresh keeps them.')}</span>}
        </div>
      </fieldset>
    </>}
    {message && <p role="status">{message}</p>}
    {error && <p role="alert" className="text-danger">{error}</p>}
  </div>;
}
