import { useState } from 'react';
import { useLocale } from '../../context/LocaleContext';
import {
  MODEL_LISTS, personaModels, personaRows, type ModelAvailability, type ModelListName, type PersonaModelEntry,
} from '../../lib/smartRouting';
import type { PolicyRecord } from '../../lib/subscriptionAccounts';
import ModelChips from './ModelChips';

interface Props {
  policy: PolicyRecord | null;
  personas: string[];
  options: string[];
  availability: Record<string, ModelAvailability>;
  /** Replace `personaModels` in the draft policy. */
  onChange: (personaModels: Record<string, PersonaModelEntry>) => void;
}

const EMPTY: PersonaModelEntry = { default: [], lighter: [], stronger: [] };

/**
 * One row per persona with Default, Lighter, and Stronger model lists. An entry exists only while one of its lists
 * has a model, so a persona without lists keeps the Legacy Routing order.
 */
export default function PersonaModelsEditor({ policy, personas, options, availability, onChange }: Props) {
  const { t } = useLocale();
  const [extras, setExtras] = useState<string[]>([]);
  const [newPersona, setNewPersona] = useState('');
  const entries = personaModels(policy);
  const rows = personaRows(personas, Object.keys(entries), extras);
  const listLabels: Record<ModelListName, string> = { default: t('Default'), lighter: t('Lighter'), stronger: t('Stronger') };

  const setList = (persona: string, list: ModelListName, models: string[]) => {
    const next = { ...entries };
    const entry = { ...(next[persona] ?? EMPTY), [list]: models };
    if (entry.default.length + entry.lighter.length + entry.stronger.length === 0) delete next[persona];
    else next[persona] = entry;
    onChange(next);
  };
  const removeRow = (persona: string) => {
    setExtras((current) => current.filter((p) => p !== persona));
    if (entries[persona]) {
      const next = { ...entries };
      delete next[persona];
      onChange(next);
    }
  };
  const addRow = () => {
    const name = newPersona.trim();
    if (!name || name === '*') return;
    setExtras((current) => [...current, name]);
    setNewPersona('');
  };

  return <div className="persona-models">
    <h4>{t('Persona model lists')}</h4>
    <p className="text-muted">{t('Captains running a Default model go first. The capacity decision can move the Lighter or Stronger list first for one mission. Captains on no list still go last.')}</p>
    {!policy ? <p className="text-muted">{t('Fix the policy JSON to edit persona model lists.')}</p> : <>
      <div className="table-wrap"><table className="data-table persona-models-table">
        <thead><tr>
          <th>{t('Persona')}</th>
          {MODEL_LISTS.map((list) => <th key={list}>{listLabels[list]}</th>)}
          <th><span className="sr-only">{t('Actions')}</span></th>
        </tr></thead>
        <tbody>{rows.map((persona) => {
          const entry = entries[persona];
          const catalogued = personas.includes(persona);
          return <tr key={persona} data-testid={`persona-models-${persona}`}>
            <td>
              <strong>{persona}</strong>
              {!entry && <div className="text-muted">{t('Legacy order only')}</div>}
              {entry && entry.default.length === 0 && <div className="text-danger" role="note">{t('Default needs at least one model.')}</div>}
            </td>
            {MODEL_LISTS.map((list) => <td key={list}>
              <ModelChips label={`${persona} ${listLabels[list]}`} value={entry?.[list] ?? []} options={options}
                availability={availability} onChange={(models) => setList(persona, list, models)} />
            </td>)}
            <td>{(entry || !catalogued) && (
              <button type="button" className="btn btn-secondary btn-sm" aria-label={t('Remove model lists for {{persona}}', { persona })}
                onClick={() => removeRow(persona)}>{t('Remove')}</button>
            )}</td>
          </tr>;
        })}</tbody>
      </table></div>
      <div className="persona-models-add">
        <label htmlFor="persona-models-new">{t('Add persona')}</label>
        <input id="persona-models-new" value={newPersona} placeholder={t('Persona name')} onChange={(e) => setNewPersona(e.target.value)} />
        <button type="button" className="btn btn-secondary btn-sm" onClick={addRow} disabled={!newPersona.trim()}>{t('Add row')}</button>
      </div>
    </>}
  </div>;
}
