import { useEffect, useState } from 'react';
import { getSettings, updateSettings } from '../api/client';
import UsageRoutingEditor, { emptyUsageRouting } from '../components/UsageRoutingEditor';
import { useLocale } from '../context/LocaleContext';
import { useProxySessionContext } from '../lib/useProxySessionContext';

/** Admin policy editor hosted by the active Server settings hub. */
export default function RoutingSettings() {
  const { t } = useLocale();
  const proxyContext = useProxySessionContext();
  const remoteProxyMode = Boolean(proxyContext?.selectedInstanceId);
  const [policy, setPolicy] = useState(JSON.stringify(emptyUsageRouting, null, 2));
  const [statuses, setStatuses] = useState<Array<Record<string, unknown>>>([]);
  const [loading, setLoading] = useState(true);
  const [loaded, setLoaded] = useState(false);
  const [loadAttempt, setLoadAttempt] = useState(0);
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');
  const apply = (data: unknown) => {
    const raw = data as Record<string, unknown>;
    const modelTier = raw.modelTier as Record<string, unknown> | undefined;
    setPolicy(JSON.stringify(modelTier?.usageRouting ?? emptyUsageRouting, null, 2));
    setStatuses(Array.isArray(raw.providerUsage) ? raw.providerUsage as Array<Record<string, unknown>> : []);
  };
  useEffect(() => {
    let active = true;
    setLoading(true); setError('');
    getSettings().then(data => { if (active) { apply(data); setLoaded(true); } })
      .catch(e => { if (active) setError(String(e)); })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [loadAttempt]);
  const save = async () => {
    setSaving(true); setMessage(''); setError('');
    try {
      const usageRouting: unknown = JSON.parse(policy);
      if (!usageRouting || typeof usageRouting !== 'object' || Array.isArray(usageRouting)) throw new Error(t('Policy must be a JSON object.'));
      apply(await updateSettings({ modelTier: { usageRouting } }));
      setMessage(t('Routing V2 settings saved.'));
    } catch (e) { setError(e instanceof Error ? e.message : String(e)); }
    finally { setSaving(false); }
  };
  const refreshUsage = async () => {
    setError('');
    try {
      const raw = await getSettings() as unknown as Record<string, unknown>;
      setStatuses(Array.isArray(raw.providerUsage) ? raw.providerUsage as Array<Record<string, unknown>> : []);
    } catch (e) { setError(String(e)); }
  };
  return <div>
    <h2>{t('Routing V2')}</h2>
    {loading ? <p>{t('Loading settings...')}</p> : !loaded ?
      <button className="btn btn-secondary" type="button" onClick={() => setLoadAttempt(attempt => attempt + 1)}>{t('Retry loading settings')}</button> : <>
      <p className="text-muted">{t('V2 replaces legacy model and provider preferences when enabled. Tier membership and captain persona restrictions still apply. Configure every persona, or use a * default route, before enabling.')}</p>
      {remoteProxyMode && <p>{t('Edit routing on the Admiral directly. Settings changes are disabled in remote proxy mode.')}</p>}
      <fieldset disabled={remoteProxyMode || saving} style={{ border: 0, padding: 0, minWidth: 0 }}>
        <UsageRoutingEditor value={policy} onChange={value => { setPolicy(value); setMessage(''); }} statuses={statuses} />
        <div style={{ display: 'flex', gap: '0.75rem', marginTop: '1rem' }}>
          <button className="btn btn-primary" type="button" onClick={save}>{saving ? t('Saving...') : t('Save routing policy')}</button>
          <button className="btn btn-secondary" type="button" onClick={refreshUsage}>{t('Refresh saved usage')}</button>
        </div>
      </fieldset>
    </>}
    {message && <p role="status">{message}</p>}
    {error && <p role="alert" className="text-danger">{error}</p>}
  </div>;
}
