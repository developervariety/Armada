import { useEffect, useRef, useState } from 'react';
import { updateSettings } from '../api/client';
import { useLocale } from '../context/LocaleContext';
import { baseAfterSave, changedKeys, isDirty, mergeDraft } from '../lib/settingsDraft';

/** Editable text form of the model routing policy. */
export interface RoutingPolicyDraft {
  midTierModels: string;
  highTierModels: string;
  specialistPersonas: string;
  reservedHighTierSlots: number;
  preferNonNativeFirst: boolean;
  withinTierStrategy: string;
  withinTierPreferenceOrder: string;
  familyClassificationRules: string;
  rejectStagePersonaTitlePrefixes: boolean;
  stagePersonaTitlePrefixes: string;
  modelProviders: string;
  additionalPromptTemplates: string;
  additionalPersonas: string;
  additionalPipelines: string;
}

function asRecord(value: unknown): Record<string, unknown> {
  if (value && typeof value === 'object' && !Array.isArray(value)) return value as Record<string, unknown>;
  return {};
}

function listToLines(value: unknown): string {
  if (!Array.isArray(value)) return '';
  return value.map((item) => String(item)).join('\n');
}

function linesToList(text: string): string[] {
  return text.split('\n').map((line) => line.trim()).filter((line) => line.length > 0);
}

function prettyJson(value: unknown, fallback: string): string {
  try {
    return JSON.stringify(value ?? JSON.parse(fallback), null, 2);
  } catch {
    return fallback;
  }
}

/** The routing policy draft for a settings response. Smart Routing is never part of it. */
export function routingPolicyFromSettings(raw: Record<string, unknown>): RoutingPolicyDraft {
  const modelTier = asRecord(raw.modelTier);
  const voyageDispatch = asRecord(raw.voyageDispatch);
  return {
    midTierModels: listToLines(modelTier.midTierModels),
    highTierModels: listToLines(modelTier.highTierModels),
    specialistPersonas: listToLines(modelTier.specialistPersonas),
    reservedHighTierSlots: Number(modelTier.reservedHighTierSlots ?? 0),
    preferNonNativeFirst: Boolean(modelTier.preferNonNativeFirst),
    withinTierStrategy: String(modelTier.withinTierStrategy ?? 'Random'),
    withinTierPreferenceOrder: prettyJson(modelTier.withinTierPreferenceOrder, '{}'),
    familyClassificationRules: prettyJson(modelTier.familyClassificationRules, '[]'),
    rejectStagePersonaTitlePrefixes: Boolean(voyageDispatch.rejectStagePersonaTitlePrefixes),
    stagePersonaTitlePrefixes: listToLines(voyageDispatch.stagePersonaTitlePrefixes),
    modelProviders: prettyJson(raw.modelProviders, '{\n  "providers": {}\n}'),
    additionalPromptTemplates: prettyJson(raw.additionalPromptTemplates, '[]'),
    additionalPersonas: prettyJson(raw.additionalPersonas, '[]'),
    additionalPipelines: prettyJson(raw.additionalPipelines, '[]'),
  };
}

const MODEL_TIER_LISTS = ['midTierModels', 'highTierModels', 'specialistPersonas'] as const;
const MODEL_TIER_VALUES = ['reservedHighTierSlots', 'preferNonNativeFirst', 'withinTierStrategy'] as const;
const MODEL_TIER_JSON = ['withinTierPreferenceOrder', 'familyClassificationRules'] as const;
const TOP_LEVEL_JSON = ['modelProviders', 'additionalPromptTemplates', 'additionalPersonas', 'additionalPipelines'] as const;

/** A JSON field that does not parse. */
export class RoutingJsonError extends Error {
  readonly field: keyof RoutingPolicyDraft;

  constructor(field: keyof RoutingPolicyDraft) {
    super(`${field} is not valid JSON.`);
    this.field = field;
  }
}

function parseJson(draft: RoutingPolicyDraft, field: keyof RoutingPolicyDraft): unknown {
  try {
    return JSON.parse(String(draft[field]));
  } catch {
    throw new RoutingJsonError(field);
  }
}

/**
 * The settings update for the fields that changed, and nothing else. The server applies each
 * `modelTier` and `voyageDispatch` field separately and replaces `modelProviders` and each additional
 * asset list whole, so an unchanged field must be absent or it would overwrite a newer stored value.
 * `modelTier.usageRouting` is owned by the Smart Routing section and is never sent from here.
 */
export function buildRoutingPolicyUpdate(base: RoutingPolicyDraft, draft: RoutingPolicyDraft): {
  payload: Record<string, unknown>;
  sent: Partial<RoutingPolicyDraft>;
} {
  const changed = new Set(changedKeys(base, draft));
  const payload: Record<string, unknown> = {};
  const sent: Partial<RoutingPolicyDraft> = {};
  const modelTier: Record<string, unknown> = {};
  const voyageDispatch: Record<string, unknown> = {};
  const mark = <K extends keyof RoutingPolicyDraft>(field: K) => { sent[field] = draft[field]; };

  for (const field of MODEL_TIER_LISTS) if (changed.has(field)) { modelTier[field] = linesToList(draft[field]); mark(field); }
  for (const field of MODEL_TIER_VALUES) if (changed.has(field)) { modelTier[field] = draft[field]; mark(field); }
  for (const field of MODEL_TIER_JSON) if (changed.has(field)) { modelTier[field] = parseJson(draft, field); mark(field); }
  if (changed.has('rejectStagePersonaTitlePrefixes')) { voyageDispatch.rejectStagePersonaTitlePrefixes = draft.rejectStagePersonaTitlePrefixes; mark('rejectStagePersonaTitlePrefixes'); }
  if (changed.has('stagePersonaTitlePrefixes')) { voyageDispatch.stagePersonaTitlePrefixes = linesToList(draft.stagePersonaTitlePrefixes); mark('stagePersonaTitlePrefixes'); }
  for (const field of TOP_LEVEL_JSON) if (changed.has(field)) { payload[field] = parseJson(draft, field); mark(field); }

  if (Object.keys(modelTier).length > 0) payload.modelTier = modelTier;
  if (Object.keys(voyageDispatch).length > 0) payload.voyageDispatch = voyageDispatch;
  return { payload, sent };
}

interface RoutingPolicyEditorProps {
  /** Latest server settings, from a load or a save. */
  saved: Record<string, unknown>;
  disabled?: boolean;
  /** Called with the server response after a save. */
  onSaved: (settings: Record<string, unknown>) => void;
}

/**
 * Tier lists, specialist personas, reserved slots, strategy, preference order, family rules, the voyage
 * dispatch guard, model providers, and additional assets. A reload keeps unsaved edits, and a save sends
 * only the changed fields.
 */
export default function RoutingPolicyEditor({ saved, disabled = false, onSaved }: RoutingPolicyEditorProps) {
  const { t } = useLocale();
  const baseRef = useRef<RoutingPolicyDraft>(routingPolicyFromSettings(saved));
  const [draft, setDraft] = useState<RoutingPolicyDraft>(baseRef.current);
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');

  useEffect(() => {
    const next = routingPolicyFromSettings(saved);
    const base = baseRef.current;
    baseRef.current = next;
    setDraft((current) => mergeDraft(base, current, next));
  }, [saved]);

  const usageRoutingEnabled = Boolean(asRecord(asRecord(saved.modelTier).usageRouting).enabled);
  const modelProvidersHotReload = Boolean(saved.modelProvidersHotReload);
  const additionalAssetsHotReload = Boolean(saved.additionalAssetsHotReload);
  const dirty = isDirty(baseRef.current, draft);
  const edit = (patch: Partial<RoutingPolicyDraft>) => { setDraft((current) => ({ ...current, ...patch })); setMessage(''); };

  const save = async () => {
    setError(''); setMessage('');
    let update: ReturnType<typeof buildRoutingPolicyUpdate>;
    try {
      update = buildRoutingPolicyUpdate(baseRef.current, draft);
    } catch (e) {
      setError(e instanceof RoutingJsonError ? t('{{field}} is not valid JSON. Fix it and save again.', { field: e.field }) : String(e));
      return;
    }
    if (Object.keys(update.payload).length === 0) return;
    setSaving(true);
    try {
      const response = await updateSettings(update.payload) as Record<string, unknown>;
      baseRef.current = baseAfterSave(baseRef.current, update.sent);
      onSaved(response);
      const restart = ('modelProviders' in update.payload && !modelProvidersHotReload)
        || (['additionalPromptTemplates', 'additionalPersonas', 'additionalPipelines'].some((key) => key in update.payload) && !additionalAssetsHotReload);
      setMessage(restart
        ? t('Routing policy saved. Restart the Admiral to load model providers and additional assets.')
        : t('Routing policy saved.'));
    } catch (e) {
      setError(t('Failed to save routing policy: {{message}}', { message: e instanceof Error ? e.message : String(e) }));
    } finally {
      setSaving(false);
    }
  };

  return (
    <fieldset disabled={disabled || saving} style={{ border: 'none', margin: 0, padding: 0, minWidth: 0 }}>
      <div className="settings-section">
        <h3>{t('Legacy Routing')}</h3>
        <p className="text-muted">
          {t('Tier lists, family rules, and routing policy hot-reload. Empty lists are the product default: random assignment among idle captains, with no model-family or specialist assumption.')}
        </p>
        {usageRoutingEnabled && (
          <p className="text-muted" role="note">
            {t('Smart Routing is on. It starts from this Legacy Routing order, then applies the usage filter and persona model lists.')}
          </p>
        )}
        <div className="settings-grid">
          <div className="form-group">
            <label>{t('Mid-tier models (one per line)')}</label>
            <textarea rows={5} value={draft.midTierModels} onChange={(e) => edit({ midTierModels: e.target.value })} title={t('Concrete model ids that classify as mid')} />
          </div>
          <div className="form-group">
            <label>{t('High-tier models (one per line)')}</label>
            <textarea rows={5} value={draft.highTierModels} onChange={(e) => edit({ highTierModels: e.target.value })} title={t('Concrete model ids that classify as high')} />
          </div>
          <div className="form-group">
            <label>{t('Specialist personas (one per line)')}</label>
            <textarea rows={5} value={draft.specialistPersonas} onChange={(e) => edit({ specialistPersonas: e.target.value })} title={t('Personas reserved for high-tier captains')} />
          </div>
          <div className="form-group">
            <label>{t('Reserved high-tier slots')}</label>
            <input type="number" min={0} max={10} value={draft.reservedHighTierSlots} onChange={(e) => edit({ reservedHighTierSlots: parseInt(e.target.value) || 0 })} title={t('Idle high-tier slots held for specialist work (0 disables)')} />
          </div>
          <div className="form-group">
            <label>{t('Within-tier strategy')}</label>
            <select value={draft.withinTierStrategy} onChange={(e) => edit({ withinTierStrategy: e.target.value })} title={t('Random is the product default')}>
              <option value="Random">{t('Random')}</option>
              <option value="PreferenceOrderThenRandom">{t('Preference order, then random')}</option>
            </select>
          </div>
          <div className="form-group">
            <label className="settings-checkbox-label">
              <input type="checkbox" checked={draft.preferNonNativeFirst} onChange={(e) => edit({ preferNonNativeFirst: e.target.checked })} />
              <span>{t('Prefer non-native captains first')}</span>
            </label>
          </div>
          <div className="form-group" style={{ gridColumn: '1 / -1' }}>
            <label>{t('Within-tier preference order (JSON object)')}</label>
            <textarea rows={6} className="mono" value={draft.withinTierPreferenceOrder} onChange={(e) => edit({ withinTierPreferenceOrder: e.target.value })} title={t('Used only when the strategy is Preference order, then random')} />
          </div>
          <div className="form-group" style={{ gridColumn: '1 / -1' }}>
            <label>{t('Family classification rules (JSON array of {pattern, tier})')}</label>
            <textarea rows={6} className="mono" value={draft.familyClassificationRules} onChange={(e) => edit({ familyClassificationRules: e.target.value })} title={t('Regex patterns applied when a model is not in a tier list')} />
          </div>
        </div>
      </div>

      <div className="settings-section" style={{ marginTop: '1.5rem' }}>
        <h3>{t('Voyage dispatch guard')}</h3>
        <p className="text-muted">{t('Off by default. When on, a mission title that opens with a listed stage-persona prefix is rejected.')}</p>
        <div className="settings-grid">
          <div className="form-group">
            <label className="settings-checkbox-label">
              <input type="checkbox" checked={draft.rejectStagePersonaTitlePrefixes} onChange={(e) => edit({ rejectStagePersonaTitlePrefixes: e.target.checked })} />
              <span>{t('Reject stage-persona title prefixes')}</span>
            </label>
          </div>
          <div className="form-group" style={{ gridColumn: '1 / -1' }}>
            <label>{t('Stage-persona prefixes (one per line, include trailing space)')}</label>
            <textarea rows={5} value={draft.stagePersonaTitlePrefixes} onChange={(e) => edit({ stagePersonaTitlePrefixes: e.target.value })} title={t('Example: [Worker] ')} />
          </div>
        </div>
      </div>

      <div className="settings-section" style={{ marginTop: '1.5rem' }}>
        <h3>{t('Model providers')}</h3>
        <p className="text-muted">
          {modelProvidersHotReload ? t('Model providers hot-reload on save.') : t('Model providers load at startup. Restart the Admiral after you save this block.')}
        </p>
        <div className="form-group">
          <label>{t('modelProviders JSON')}</label>
          <textarea rows={8} className="mono" value={draft.modelProviders} onChange={(e) => edit({ modelProviders: e.target.value })} title={t('Model provider definitions')} />
        </div>
      </div>

      <div className="settings-section" style={{ marginTop: '1.5rem' }}>
        <h3>{t('Additional personas, pipelines, and templates')}</h3>
        <p className="text-muted">
          {additionalAssetsHotReload
            ? t('Additional assets hot-reload on save.')
            : t('Additional prompt templates, personas, and pipelines seed at startup. Restart the Admiral after you save these blocks.')}
        </p>
        <div className="form-group">
          <label>{t('additionalPromptTemplates JSON array')}</label>
          <textarea rows={8} className="mono" value={draft.additionalPromptTemplates} onChange={(e) => edit({ additionalPromptTemplates: e.target.value })} title={t('Additional prompt templates')} />
        </div>
        <div className="form-group">
          <label>{t('additionalPersonas JSON array')}</label>
          <textarea rows={6} className="mono" value={draft.additionalPersonas} onChange={(e) => edit({ additionalPersonas: e.target.value })} title={t('Additional personas')} />
        </div>
        <div className="form-group">
          <label>{t('additionalPipelines JSON array')}</label>
          <textarea rows={8} className="mono" value={draft.additionalPipelines} onChange={(e) => edit({ additionalPipelines: e.target.value })} title={t('Additional pipelines')} />
        </div>
      </div>

      <div style={{ display: 'flex', gap: '0.75rem', marginTop: '1rem', alignItems: 'center' }}>
        <button className="btn btn-primary" type="button" onClick={save} disabled={!dirty}>
          {saving ? t('Saving...') : t('Save model routing')}
        </button>
        <button className="btn btn-secondary" type="button" onClick={() => { setDraft(baseRef.current); setError(''); }} disabled={!dirty}>
          {t('Discard changes')}
        </button>
        {dirty && <span className="text-muted">{t('Unsaved changes. A refresh keeps them.')}</span>}
      </div>
      {message && <p role="status">{message}</p>}
      {error && <p role="alert" className="text-danger">{error}</p>}
    </fieldset>
  );
}
