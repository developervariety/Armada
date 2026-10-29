import { useCallback, useEffect, useRef, useState } from 'react';
import { decideAskToolPermission, deleteAskToolPermissionRule, listAskToolPermissionRules, listAskToolPermissions } from '../../api/client';
import type { AskToolPermissionRequest, AskToolPermissionRule } from '../../types/models';

interface Props { threadId: string; busy: boolean; t: (value: string) => string; }

export default function AskToolPermissionCards({ threadId, busy, t }: Props) {
  const [requests, setRequests] = useState<AskToolPermissionRequest[]>([]);
  const [rules, setRules] = useState<AskToolPermissionRule[]>([]);
  const [error, setError] = useState('');
  const [pendingId, setPendingId] = useState('');
  const [remember, setRemember] = useState(false);
  const generation = useRef(0);

  const load = useCallback(async () => {
    const current = generation.current;
    try {
      const [nextRequests, nextRules] = await Promise.all([listAskToolPermissions(threadId), listAskToolPermissionRules(threadId)]);
      if (generation.current !== current) return;
      setRequests(nextRequests);
      setRules(nextRules);
    } catch (e) {
      if (generation.current === current) setError(e instanceof Error ? e.message : t('Could not load tool permissions.'));
    }
  }, [threadId, t]);

  useEffect(() => {
    generation.current += 1;
    setRequests([]); setRules([]); setError(''); setRemember(false);
    void load();
    return () => { generation.current += 1; };
  }, [threadId, load]);

  const waiting = requests.filter((item) => item.state === 'Pending' && Date.parse(item.expiresUtc) > Date.now());
  useEffect(() => {
    if (!busy && waiting.length === 0) return undefined;
    const id = window.setInterval(() => { void load(); }, 2000);
    return () => window.clearInterval(id);
  }, [busy, waiting.length, load]);

  async function decide(request: AskToolPermissionRequest, decision: 'allow' | 'deny') {
    if (pendingId) return;
    setPendingId(request.id);
    try {
      await decideAskToolPermission(threadId, request.id, decision, remember);
      setError('');
      setRemember(false);
    } catch (e) {
      setError(e instanceof Error ? e.message : t('The tool decision failed.'));
    } finally {
      setPendingId('');
      void load();
    }
  }

  async function forget(rule: AskToolPermissionRule) {
    try {
      await deleteAskToolPermissionRule(threadId, rule.id);
      setError('');
    } catch (e) {
      setError(e instanceof Error ? e.message : t('Could not remove the rule.'));
    } finally { void load(); }
  }

  if (waiting.length === 0 && rules.length === 0 && !error) return null;
  return <section className="ask-action-section" aria-label={t('Tool permissions')}>
    <div className="ask-action-heading"><h3>{t('Tool permissions')}</h3></div>
    {error && <p className="ask-action-error" role="alert">{error}</p>}
    {waiting.map((request) => <article className="card ask-action-card" key={request.id}>
      <div className="ask-action-card-title"><strong>{request.toolName}</strong><span className="badge">{t('Waiting for your decision')}</span></div>
      <pre className="ask-action-json">{request.inputJson}</pre>
      <p className="text-dim">{t('The tool is denied if you do not decide before')} {new Date(request.expiresUtc).toLocaleTimeString()}.</p>
      <label className="text-dim"><input type="checkbox" checked={remember} onChange={(e) => setRemember(e.target.checked)} /> {t('Remember this choice for this conversation')}</label>
      <div className="ask-action-buttons">
        <button className="btn btn-primary btn-sm" disabled={!!pendingId} onClick={() => void decide(request, 'allow')}>{t('Allow tool')}</button>
        <button className="btn btn-sm" disabled={!!pendingId} onClick={() => void decide(request, 'deny')}>{t('Deny tool')}</button>
      </div>
    </article>)}
    {rules.length > 0 && <ul className="ask-action-rules" aria-label={t('Remembered tool rules')}>
      {rules.map((rule) => <li key={rule.id}>
        <span className="badge">{rule.action === 'Allow' ? t('Always allow') : t('Always deny')}</span> {rule.toolName}{' '}
        <button className="btn btn-sm" onClick={() => void forget(rule)} aria-label={`${t('Forget rule')} ${rule.toolName}`}>{t('Forget')}</button>
      </li>)}
    </ul>}
  </section>;
}
