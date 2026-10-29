import { useEffect, useState } from 'react';
import { View } from 'react-native';
import { CAPTAIN_TIERS } from '@dashboard/lib/captainForm';
import { personaRoutingForm, validatePersonaPriorities, personaPriorityTargets, priorityTarget, priorityTargetPatch, type PersonaRoutingForm } from '@dashboard/lib/personaPriorities';
import { enumerateModelEndpoints } from '@dashboard/api/client';
import { listAll } from '@dashboard/api/paging';
import type { ModelEndpoint, Persona, PersonaCaptainPriority } from '@dashboard/types/models';
import { BottomSheet } from '../../components/ui/BottomSheet';
import { AppText } from '../../components/ui/AppText';
import { Button } from '../../components/ui/Button';
import { SelectField } from '../../components/ui/SelectSheet';
import { TextField } from '../../components/ui/TextField';
import { useLocale } from '../../i18n/LocaleContext';
import { errorText } from '../../resource/useLoad';

interface Props { persona: Partial<Persona>; onClose: () => void; onSave: (routing: PersonaRoutingForm) => Promise<void> }

/** Structured ordered matcher editor; state is discarded when the sheet is closed. */
export function PersonaPrioritySheet({ persona, onClose, onSave }: Props) {
  const { t } = useLocale();
  const [routing, setRouting] = useState(() => personaRoutingForm(persona));
  const [error, setError] = useState('');
  const [endpoints, setEndpoints] = useState<ModelEndpoint[]>([]);
  useEffect(() => {
    let active = true;
    Promise.resolve().then(() => listAll(enumerateModelEndpoints)).then(page => { if (active) setEndpoints(page?.objects ?? []); })
      .catch(err => { if (active) setError(errorText(err, t('Failed to load endpoints.'))); });
    return () => { active = false; };
  }, [t]);
  const targets = personaPriorityTargets(endpoints);
  function choices(priority: PersonaCaptainPriority) {
    const selected = priorityTarget(priority);
    return selected && !targets.some(option => option.value === selected)
      ? [...targets, { value: selected, label: priority.runtime === 'ApiEndpoint' ? `API Endpoint: ${priority.endpoint ?? t('Any')}` : selected }]
      : targets;
  }
  const [saving, setSaving] = useState(false);
  function update(index: number, patch: Partial<PersonaCaptainPriority>) {
    setRouting(r => ({ ...r, captainPriorities: r.captainPriorities.map((p, i) => i === index ? { ...p, ...patch } : p) }));
  }
  function move(index: number, offset: number) {
    setRouting(r => {
      const priorities = [...r.captainPriorities];
      [priorities[index], priorities[index + offset]] = [priorities[index + offset], priorities[index]];
      return { ...r, captainPriorities: priorities };
    });
  }
  async function save() {
    const validation = validatePersonaPriorities(routing.captainPriorities);
    if (validation) { setError(t(validation)); return; }
    setSaving(true);
    try { await onSave(routing); onClose(); }
    catch (err) { setError(errorText(err, t('Save failed.'))); }
    finally { setSaving(false); }
  }
  const any = [{ value: '', label: t('Any') }];
  return <BottomSheet open title={t('Captain priorities')} onClose={saving ? () => {} : onClose} closeLabel={t('Close')} testID="persona-priorities"
    footer={<View><Button label={t('Save')} onPress={() => { void save(); }} busy={saving} /><Button label={t('Cancel')} variant="secondary" onPress={onClose} disabled={saving} /></View>}>
    <AppText>{t('Priorities run from top to bottom. Empty lists use the default captain routing.')}</AppText>
    {routing.captainPriorities.map((priority, index) => <View key={index}>
      <AppText variant="label">{t('Priority {{number}}', { number: index + 1 })}</AppText>
      <SelectField label={t('Runtime')} value={priorityTarget(priority)} closeLabel={t('Close')} options={[...any, ...choices(priority)]} onChange={target => update(index, priorityTargetPatch(target, priority))} />
      <TextField label={t('Model')} value={priority.model ?? ''} onChangeText={model => update(index, { model: model || null })} autoCapitalize="none" />
      {priority.runtime === 'ApiEndpoint'
        ? <SelectField label={t('Endpoint')} value={priority.endpoint ?? ''} closeLabel={t('Close')}
          options={[...any, ...choices(priority).filter(option => option.value.startsWith('endpoint:')).map(option => ({ ...option, value: option.value.slice('endpoint:'.length) }))]}
          onChange={endpoint => update(index, { endpoint: endpoint || null })} />
        : <TextField label={t('Endpoint')} value={priority.endpoint ?? ''} editable={priority.runtime === 'Mux'} onChangeText={endpoint => update(index, { endpoint: endpoint || null })} autoCapitalize="none" />}
      <SelectField label={t('Minimum tier')} value={priority.minimumTier ?? ''} closeLabel={t('Close')} options={[...any, ...CAPTAIN_TIERS.map(tier => ({ value: tier, label: t(tier) }))]} onChange={minimumTier => update(index, { minimumTier: minimumTier || null })} />
      <Button label={t('Move up')} onPress={() => move(index, -1)} disabled={index === 0} variant="secondary" />
      <Button label={t('Move down')} onPress={() => move(index, 1)} disabled={index === routing.captainPriorities.length - 1} variant="secondary" />
      <Button label={t('Remove priority')} onPress={() => setRouting(r => ({ ...r, captainPriorities: r.captainPriorities.filter((_, i) => i !== index) }))} variant="danger" />
    </View>)}
    {/* Choosing a runtime adds the priority at once: a separate pick-then-press step let a choice that was never added
        be saved as an empty list. */}
    <SelectField label={t('Add priority')} value="" placeholder={t('Choose a runtime to add')} closeLabel={t('Close')} options={targets}
      onChange={(target) => { if (target) setRouting(r => ({ ...r, captainPriorities: [...r.captainPriorities, priorityTargetPatch(target)] })); }} />
    <SelectField label={t('When a priority is unavailable')} closeLabel={t('Close')} value={routing.priorityAvailability} options={[{ value: 'FallThrough', label: t('Try lower priorities') }, { value: 'WaitForFirstPriority', label: t('Wait for the first priority') }]} onChange={priorityAvailability => setRouting(r => ({ ...r, priorityAvailability }))} />
    <SelectField label={t('When all priorities are unavailable')} closeLabel={t('Close')} value={routing.priorityExhaustion} disabled={routing.priorityAvailability === 'WaitForFirstPriority'} options={[{ value: 'Wait', label: t('Wait') }, { value: 'UseEligibleCaptain', label: t('Use any eligible captain') }]} onChange={priorityExhaustion => setRouting(r => ({ ...r, priorityExhaustion }))} />
    {error ? <AppText accessibilityRole="alert" color="danger">{error}</AppText> : null}
  </BottomSheet>;
}
