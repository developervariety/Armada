import { useEffect, useRef, useState } from 'react';
import { updateSettings } from '../api/client';
import { useLocale } from '../context/LocaleContext';
import { baseAfterSave, changedKeys, isDirty, mergeDraft } from '../lib/settingsDraft';

/** Editable text form of the model routing policy. */
export interface RoutingPolicyDraft {
  reservedHighTierSlots: number;
  preferNonNativeFirst: boolean;
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
    reservedHighTierSlots: Number(modelTier.reservedHighTierSlots ?? 0),
    preferNonNativeFirst: Boolean(modelTier.preferNonNativeFirst),
    rejectStagePersonaTitlePrefixes: Boolean(voyageDispatch.rejectStagePersonaTitlePrefixes),
    stagePersonaTitlePrefixes: listToLines(voyageDispatch.stagePersonaTitlePrefixes),
    modelProviders: prettyJson(raw.modelProviders, '{\n  "providers": {}\n}'),
    additionalPromptTemplates: prettyJson(raw.additionalPromptTemplates, '[]'),
    additionalPersonas: prettyJson(raw.additionalPersonas, '[]'),
    additionalPipelines: prettyJson(raw.additionalPipelines, '[]'),
  };
}

const MODEL_TIER_VALUES = ['reservedHighTierSlots', 'preferNonNativeFirst'] as const;
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

  for (const field of MODEL_TIER_VALUES) if (changed.has(field)) { modelTier[field] = draft[field]; mark(field); }
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
 * Fleet-wide Legacy Routing policy (reserved Premium slots and the non-native preference), the voyage dispatch
 * guard, model providers, and additional assets. Each captain's tier and preference rank are edited on the
 * captain, and a persona's minimum tier on the persona. A reload keeps unsaved edits, and a save sends only
 * the changed fields.
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
          {t('Each captain carries a capability tier (Economy, Standard, Premium) and preference rank. Missions can request a tier floor, and each persona can set its own minimum tier. The lowest eligible tier goes first, then higher preference rank. This policy hot-reloads.')}
        </p>
        {usageRoutingEnabled && (
          <p className="text-muted" role="note">
            {t('Smart Routing is on. It starts from this Legacy Routing order, then applies the usage filter and persona model lists.')}
          </p>
        )}
        <div className="settings-grid">
          <div className="form-group">
            <label htmlFor="routing-reserved-slots">{t('Reserved Premium slots')}</label>
            <input id="routing-reserved-slots" type="number" min={0} max={10} value={draft.reservedHighTierSlots} onChange={(e) => edit({ reservedHighTierSlots: parseInt(e.target.value) || 0 })} title={t('Idle Premium slots held for downstream work (0 disables)')} />
          </div>
          <div className="form-group">
            <label className="settings-checkbox-label">
              <input type="checkbox" checked={draft.preferNonNativeFirst} onChange={(e) => edit({ preferNonNativeFirst: e.target.checked })} />
              <span>{t('Prefer non-native captains first')}</span>
            </label>
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
