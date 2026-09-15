import { useCallback, useEffect, useRef, useState } from 'react';
import { getSettings, updateSettings } from '../api/client';
import UsageRoutingEditor, { emptyUsageRouting } from '../components/UsageRoutingEditor';
import RoutingPolicyEditor from '../components/RoutingPolicyEditor';
import PageHeader from '../components/shared/PageHeader';
import AutoRefreshSelect from '../components/shared/AutoRefreshSelect';
import RefreshButton from '../components/shared/RefreshButton';
import { useAutoRefresh } from '../lib/useAutoRefresh';
import { useLocale } from '../context/LocaleContext';
import { useProxySessionContext } from '../lib/useProxySessionContext';
import { mergeDraft } from '../lib/settingsDraft';

type SettingsRecord = Record<string, unknown>;

function usageRoutingText(settings: SettingsRecord): string {
  const modelTier = settings.modelTier as SettingsRecord | undefined;
  return JSON.stringify(modelTier?.usageRouting ?? emptyUsageRouting, null, 2);
}

function providerUsage(settings: SettingsRecord): Array<SettingsRecord> {
  return Array.isArray(settings.providerUsage) ? settings.providerUsage as Array<SettingsRecord> : [];
}

/**
 * One routing configuration surface with two independent parts. The model routing policy edits tier
 * lists, specialist personas, reserved slots, strategy, preference order, family rules, the voyage
 * dispatch guard, model providers, and additional assets. Routing V2 edits `modelTier.usageRouting`
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
  const [statuses, setStatuses] = useState<Array<SettingsRecord>>([]);
  const [loading, setLoading] = useState(true);
  const [loadAttempt, setLoadAttempt] = useState(0);
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');

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
      applySaved(await updateSettings({ modelTier: { usageRouting } }) as SettingsRecord, sent);
      setMessage(t('Routing V2 settings saved.'));
    } catch (e) { setError(e instanceof Error ? e.message : String(e)); }
    finally { setSaving(false); }
  };

  const usageDirty = policy !== policyBaseRef.current;

  return <div>
    <PageHeader
      title={t('Routing')}
      subtitle={t('Model routing policy and Routing V2 usage-aware routing. Each part saves only its own settings.')}
      actions={saved ? (
        <>
          <AutoRefreshSelect seconds={refreshSeconds} onChange={setRefreshSeconds} />
          <RefreshButton onRefresh={load} title={t('Refresh routing settings')} />
        </>
      ) : undefined}
    />
    {loading && !saved ? <p>{t('Loading settings...')}</p> : !saved ?
      <button className="btn btn-secondary" type="button" onClick={() => setLoadAttempt(attempt => attempt + 1)}>{t('Retry loading settings')}</button> : <>
      {remoteProxyMode && <p>{t('Edit routing on the Admiral directly. Settings changes are disabled in remote proxy mode.')}</p>}
      <RoutingPolicyEditor saved={saved} disabled={remoteProxyMode} onSaved={(settings) => applySaved(settings)} />
      <p className="text-muted" style={{ marginTop: '1.5rem' }}>{t('V2 replaces legacy model and provider preferences when enabled. Tier membership and captain persona restrictions still apply. Configure every persona, or use a * default route, before enabling.')}</p>
      <fieldset disabled={remoteProxyMode || saving} style={{ border: 0, padding: 0, minWidth: 0 }}>
        <UsageRoutingEditor value={policy} onChange={value => { setPolicy(value); setMessage(''); }} statuses={statuses} />
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
