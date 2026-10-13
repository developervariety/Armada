import { useRef, useState } from 'react';
import { useLocale } from '../../context/LocaleContext';
import { CAPTAIN_TIERS } from '../../lib/captainForm';
import { PERSONA_PRIORITY_RUNTIMES } from '../../lib/personaPriorities';
import type { PersonaRoutingForm } from '../../lib/personaPriorities';
import type { PersonaCaptainPriority } from '../../types/models';

interface Props { value: PersonaRoutingForm; onChange: (value: PersonaRoutingForm) => void }

export default function PersonaPriorityEditor({ value, onChange }: Props) {
  const { t } = useLocale();
  const [newRuntime, setNewRuntime] = useState('Codex');
  const [dropTarget, setDropTarget] = useState<number | null>(null);
  const [announcement, setAnnouncement] = useState('');
  const dragged = useRef<number | null>(null);
  function update(index: number, patch: Partial<PersonaCaptainPriority>) {
    onChange({ ...value, captainPriorities: value.captainPriorities.map((p, i) => i === index ? { ...p, ...patch } : p) });
  }
  function move(index: number, destination: number) {
    if (destination < 0 || destination >= value.captainPriorities.length || index === destination) return;
    const priorities = [...value.captainPriorities];
    const [priority] = priorities.splice(index, 1);
    priorities.splice(destination, 0, priority);
    onChange({ ...value, captainPriorities: priorities });
    setAnnouncement(t('Priority {{number}}', { number: destination + 1 }));
  }
  return <fieldset className="persona-priority-editor form-fieldset">
    <legend>{t('Captain priorities')}</legend>
    <p className="text-dim">{t('Priorities run from top to bottom. Empty lists use the default captain routing.')}</p>
    <p className="text-dim persona-priority-help">{t('Drag to reorder, or use the arrows or position menu.')}</p>
    <div className="persona-priority-list">
      {value.captainPriorities.map((priority, index) => <fieldset
        className={`persona-priority-class${dropTarget === index ? ' persona-priority-drop-target' : ''}`} key={index}
        onDragOver={e => { if (dragged.current !== null) { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; setDropTarget(index); } }}
        onDrop={e => { e.preventDefault(); if (dragged.current !== null) move(dragged.current, index); dragged.current = null; setDropTarget(null); }}>
        <legend>{t('Priority {{number}}', { number: index + 1 })}</legend>
        <div className="persona-priority-toolbar">
          <button type="button" className="btn btn-sm persona-priority-drag" draggable
            aria-label={t('Drag to reorder')} title={t('Drag to reorder')}
            onDragStart={e => { dragged.current = index; e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', String(index)); }}
            onDragEnd={() => { dragged.current = null; setDropTarget(null); }}
            onKeyDown={e => { if (e.altKey && (e.key === 'ArrowUp' || e.key === 'ArrowDown')) { e.preventDefault(); move(index, index + (e.key === 'ArrowUp' ? -1 : 1)); } }}>
            <span aria-hidden="true">⠿</span>
          </button>
          <label className="persona-priority-position">{t('Position')}<select aria-label={t('Position')} value={index} onChange={e => move(index, Number(e.target.value))}>
            {value.captainPriorities.map((_, position) => <option key={position} value={position}>{position + 1}</option>)}
          </select></label>
          <div className="persona-priority-actions">
            <button type="button" className="btn btn-sm" disabled={index === 0} aria-label={t('Move up')} title={t('Move up')} onClick={() => move(index, index - 1)}><span aria-hidden="true">↑</span></button>
            <button type="button" className="btn btn-sm" disabled={index === value.captainPriorities.length - 1} aria-label={t('Move down')} title={t('Move down')} onClick={() => move(index, index + 1)}><span aria-hidden="true">↓</span></button>
            <button type="button" className="btn btn-sm" aria-label={t('Remove priority')} title={t('Remove priority')} onClick={() => onChange({ ...value, captainPriorities: value.captainPriorities.filter((_, i) => i !== index) })}><span aria-hidden="true">×</span></button>
          </div>
        </div>
        <div className="persona-priority-fields">
          <label>{t('Runtime')}<select aria-label={t('Runtime')} value={priority.runtime ?? ''} onChange={e => update(index, { runtime: e.target.value || null, endpoint: e.target.value === 'Mux' ? priority.endpoint : null })}>
            <option value="">{t('Any')}</option>{PERSONA_PRIORITY_RUNTIMES.map(r => <option key={r.value} value={r.value}>{r.label}</option>)}
          </select></label>
          <label>{t('Minimum tier')}<select aria-label={t('Minimum tier')} value={priority.minimumTier ?? ''} onChange={e => update(index, { minimumTier: e.target.value || null })}>
            <option value="">{t('Any')}</option>{CAPTAIN_TIERS.map(tier => <option key={tier} value={tier}>{t(tier)}</option>)}
          </select></label>
          <label>{t('Model')}<input maxLength={1024} placeholder={t('Any')} value={priority.model ?? ''} onChange={e => update(index, { model: e.target.value || null })} /></label>
          <label>{t('Endpoint')}<input maxLength={1024} disabled={priority.runtime !== 'Mux'} placeholder={priority.runtime === 'Mux' ? t('Any') : t('Mux only')} value={priority.endpoint ?? ''} onChange={e => update(index, { endpoint: e.target.value || null })} /></label>
        </div>
      </fieldset>)}
    </div>
    <div className="persona-priority-add">
      <label>{t('Runtime for new priority')}<select value={newRuntime} onChange={e => setNewRuntime(e.target.value)}>
        {PERSONA_PRIORITY_RUNTIMES.map(r => <option key={r.value} value={r.value}>{r.label}</option>)}
      </select></label>
      <button type="button" className="btn" onClick={() => onChange({ ...value, captainPriorities: [...value.captainPriorities, { runtime: newRuntime }] })}>{t('Add priority')}</button>
    </div>
    <div className="persona-priority-policies">
      <label>{t('When a priority is unavailable')}<select value={value.priorityAvailability} onChange={e => onChange({ ...value, priorityAvailability: e.target.value as PersonaRoutingForm['priorityAvailability'] })}>
        <option value="FallThrough">{t('Try lower priorities')}</option><option value="WaitForFirstPriority">{t('Wait for the first priority')}</option>
      </select></label>
      <label>{t('When all priorities are unavailable')}<select disabled={value.priorityAvailability === 'WaitForFirstPriority'} value={value.priorityExhaustion} onChange={e => onChange({ ...value, priorityExhaustion: e.target.value as PersonaRoutingForm['priorityExhaustion'] })}>
        <option value="Wait">{t('Wait')}</option><option value="UseEligibleCaptain">{t('Use any eligible captain')}</option>
      </select></label>
    </div>
    <span className="sr-only" aria-live="polite">{announcement}</span>
  </fieldset>;
}
