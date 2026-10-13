import { useLocale } from '../../context/LocaleContext';
import { CAPTAIN_TIERS } from '../../lib/captainForm';
import { PERSONA_PRIORITY_RUNTIMES } from '../../lib/personaPriorities';
import type { PersonaRoutingForm } from '../../lib/personaPriorities';
import type { PersonaCaptainPriority } from '../../types/models';

interface Props { value: PersonaRoutingForm; onChange: (value: PersonaRoutingForm) => void }

export default function PersonaPriorityEditor({ value, onChange }: Props) {
  const { t } = useLocale();
  function update(index: number, patch: Partial<PersonaCaptainPriority>) {
    onChange({ ...value, captainPriorities: value.captainPriorities.map((p, i) => i === index ? { ...p, ...patch } : p) });
  }
  function move(index: number, offset: number) {
    const priorities = [...value.captainPriorities];
    [priorities[index], priorities[index + offset]] = [priorities[index + offset], priorities[index]];
    onChange({ ...value, captainPriorities: priorities });
  }
  return <fieldset className="persona-priority-editor form-fieldset">
    <legend>{t('Captain priorities')}</legend>
    <p className="text-dim">{t('Priorities run from top to bottom. Empty lists use the default captain routing.')}</p>
    {value.captainPriorities.map((priority, index) => <fieldset className="persona-priority-class" key={index}>
      <legend>{t('Priority {{number}}', { number: index + 1 })}</legend>
      <label>{t('Runtime')}<select value={priority.runtime ?? ''} onChange={e => update(index, { runtime: e.target.value || null, endpoint: e.target.value === 'Mux' ? priority.endpoint : null })}>
        <option value="">{t('Any')}</option>{PERSONA_PRIORITY_RUNTIMES.map(r => <option key={r.value} value={r.value}>{r.label}</option>)}
      </select></label>
      <label>{t('Model')}<input value={priority.model ?? ''} onChange={e => update(index, { model: e.target.value || null })} /></label>
      <label>{t('Endpoint')}<input disabled={priority.runtime !== 'Mux'} value={priority.endpoint ?? ''} onChange={e => update(index, { endpoint: e.target.value || null })} /></label>
      <label>{t('Minimum tier')}<select value={priority.minimumTier ?? ''} onChange={e => update(index, { minimumTier: e.target.value || null })}>
        <option value="">{t('Any')}</option>{CAPTAIN_TIERS.map(tier => <option key={tier} value={tier}>{t(tier)}</option>)}
      </select></label>
      <div className="action-bar">
        <button type="button" disabled={index === 0} onClick={() => move(index, -1)}>{t('Move up')}</button>
        <button type="button" disabled={index === value.captainPriorities.length - 1} onClick={() => move(index, 1)}>{t('Move down')}</button>
        <button type="button" onClick={() => onChange({ ...value, captainPriorities: value.captainPriorities.filter((_, i) => i !== index) })}>{t('Remove priority')}</button>
      </div>
    </fieldset>)}
    <button type="button" onClick={() => onChange({ ...value, captainPriorities: [...value.captainPriorities, { runtime: 'Codex' }] })}>{t('Add priority')}</button>
    <label>{t('When a priority is unavailable')}<select value={value.priorityAvailability} onChange={e => onChange({ ...value, priorityAvailability: e.target.value as PersonaRoutingForm['priorityAvailability'] })}>
      <option value="FallThrough">{t('Try lower priorities')}</option><option value="WaitForFirstPriority">{t('Wait for the first priority')}</option>
    </select></label>
    <label>{t('When all priorities are unavailable')}<select disabled={value.priorityAvailability === 'WaitForFirstPriority'} value={value.priorityExhaustion} onChange={e => onChange({ ...value, priorityExhaustion: e.target.value as PersonaRoutingForm['priorityExhaustion'] })}>
      <option value="Wait">{t('Wait')}</option><option value="UseEligibleCaptain">{t('Use any eligible captain')}</option>
    </select></label>
  </fieldset>;
}
