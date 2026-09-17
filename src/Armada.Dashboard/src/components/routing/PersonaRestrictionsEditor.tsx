import { useState } from 'react';
import { useLocale } from '../../context/LocaleContext';
import type { Captain } from '../../types/models';
import { captainsAdmitted, normalizePersona, personaRoutes, type ModelAvailability, type PersonaRoute } from '../../lib/smartRouting';
import { policyAccounts, type PolicyRecord } from '../../lib/subscriptionAccounts';
import ModelChips from './ModelChips';

interface Props {
  policy: PolicyRecord | null;
  personas: string[];
  captains: Captain[];
  options: string[];
  availability: Record<string, ModelAvailability>;
  /** Replace `personaRoutes` in the draft policy. */
  onChange: (personaRoutes: Record<string, PersonaRoute[]>) => void;
}

/**
 * Optional persona restrictions (`personaRoutes`): only the named accounts, and the listed models when a route lists
 * any, may take the persona. A persona without a restriction is not restricted, and route order has no effect.
 */
export default function PersonaRestrictionsEditor({ policy, personas, captains, options, availability, onChange }: Props) {
  const { t } = useLocale();
  const [newPersona, setNewPersona] = useState('');
  const routes = personaRoutes(policy);
  const accountIds = policyAccounts(policy).map((a) => String(a.id));
  const restricted = Object.keys(routes);
  const count = restricted.length;
  const taken = new Set(restricted.map(normalizePersona));
  const available = [...personas, '*'].filter((p) => !taken.has(normalizePersona(p)));

  const setRoutes = (persona: string, list: PersonaRoute[]) => {
    const next = { ...routes };
    if (list.length === 0) delete next[persona];
    else next[persona] = list;
    onChange(next);
  };
  const addRestriction = () => {
    if (!newPersona || !accountIds.length) return;
    onChange({ ...routes, [newPersona]: [{ accountId: accountIds[0], models: [] }] });
    setNewPersona('');
  };

  return <details className="persona-restrictions">
    <summary>{count === 0 ? t('Persona restrictions (optional, none set)') : t('Persona restrictions (optional, {{count}} set)', { count })}</summary>
    <p className="text-muted">{t('A restriction means only these accounts and models may take this persona. A persona without one is not restricted. * applies to every persona without its own restriction.')}</p>
    {!policy ? <p className="text-muted">{t('Fix the policy JSON to edit restrictions.')}</p> : <>
      {accountIds.length === 0 && <p className="text-muted">{t('Add a subscription account before adding a restriction.')}</p>}
      {restricted.map((persona) => {
        const list = routes[persona];
        const admitted = captainsAdmitted(list, captains, policy);
        return <div key={persona} className="persona-restriction" data-testid={`restriction-${persona}`}>
          <div className="persona-restriction-head">
            <strong>{persona === '*' ? t('* (every persona without its own restriction)') : persona}</strong>
            <button type="button" className="btn btn-secondary btn-sm" aria-label={t('Remove restriction for {{persona}}', { persona })}
              onClick={() => setRoutes(persona, [])}>{t('Remove restriction')}</button>
          </div>
          {admitted.length === 0 && <p className="text-danger" role="note">
            {t('This restriction excludes every captain. Missions for this persona will wait.')}
          </p>}
          {list.map((route, index) => <div key={index} className="persona-restriction-route">
            <label>
              <span>{t('Account')}</span>
              <select value={route.accountId} aria-label={t('Account for {{persona}} route {{n}}', { persona, n: index + 1 })}
                onChange={(e) => setRoutes(persona, list.map((r, i) => i === index ? { ...r, accountId: e.target.value } : r))}>
                {!accountIds.includes(route.accountId) && <option value={route.accountId}>{route.accountId || t('(none)')}</option>}
                {accountIds.map((id) => <option key={id} value={id}>{id}</option>)}
              </select>
            </label>
            <div>
              <span className="text-muted">{route.models.length === 0 ? t('Models: every model on this account') : t('Models')}</span>
              <ModelChips label={`${persona} ${t('route')} ${index + 1}`} value={route.models} options={options} availability={availability}
                onChange={(models) => setRoutes(persona, list.map((r, i) => i === index ? { ...r, models } : r))} />
            </div>
            <button type="button" className="btn btn-secondary btn-sm" onClick={() => setRoutes(persona, list.filter((_, i) => i !== index))}>
              {t('Remove account')}
            </button>
          </div>)}
          {accountIds.length > 0 && <button type="button" className="btn btn-secondary btn-sm"
            onClick={() => setRoutes(persona, [...list, { accountId: accountIds[0], models: [] }])}>{t('Add account')}</button>}
        </div>;
      })}
      {accountIds.length > 0 && available.length > 0 && <div className="persona-models-add">
        <label htmlFor="restriction-new">{t('Restrict persona')}</label>
        <select id="restriction-new" value={newPersona} onChange={(e) => setNewPersona(e.target.value)}>
          <option value="">{t('Choose a persona…')}</option>
          {available.map((p) => <option key={p} value={p}>{p}</option>)}
        </select>
        <button type="button" className="btn btn-secondary btn-sm" onClick={addRestriction} disabled={!newPersona}>{t('Add restriction')}</button>
      </div>}
    </>}
  </details>;
}
