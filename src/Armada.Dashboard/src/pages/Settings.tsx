import { useEffect, useState, useCallback } from 'react';
import { getSettings, updateSettings, getHealth } from '../api/client';
import RefreshButton from '../components/shared/RefreshButton';
import AutoRefreshSelect from '../components/shared/AutoRefreshSelect';
import { useAutoRefresh } from '../lib/useAutoRefresh';
import PageHeader from '../components/shared/PageHeader';
import ErrorModal from '../components/shared/ErrorModal';
import { useLocale } from '../context/LocaleContext';
import { useProxySessionContext } from '../lib/useProxySessionContext';

interface ServerSettings {
  admiralPort: number;
  mcpPort: number;
  maxCaptains: number;
  heartbeatIntervalSeconds: number;
  stallThresholdMinutes: number;
  idleCaptainTimeoutSeconds: number;
  autoCreatePr: boolean;
  dataDirectory: string;
  databasePath: string;
  logDirectory: string;
  docksDirectory: string;
  reposDirectory: string;
}

interface RoutingDraft {
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
  modelProvidersHotReload: boolean;
  additionalAssetsHotReload: boolean;
}

function asRecord(value: unknown): Record<string, unknown> {
  if (value && typeof value === 'object' && !Array.isArray(value)) {
    return value as Record<string, unknown>;
  }
  return {};
}

function listToLines(value: unknown): string {
  if (!Array.isArray(value)) return '';
  return value.map((item) => String(item)).join('\n');
}

function linesToList(text: string): string[] {
  return text
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line.length > 0);
}

function prettyJson(value: unknown, fallback: string): string {
  try {
    return JSON.stringify(value ?? JSON.parse(fallback), null, 2);
  } catch {
    return fallback;
  }
}

function emptyRoutingDraft(): RoutingDraft {
  return {
    midTierModels: '',
    highTierModels: '',
    specialistPersonas: '',
    reservedHighTierSlots: 0,
    preferNonNativeFirst: false,
    withinTierStrategy: 'Random',
    withinTierPreferenceOrder: '{}',
    familyClassificationRules: '[]',
    rejectStagePersonaTitlePrefixes: false,
    stagePersonaTitlePrefixes: '',
    modelProviders: '{\n  "providers": {}\n}',
    additionalPromptTemplates: '[]',
    additionalPersonas: '[]',
    additionalPipelines: '[]',
    modelProvidersHotReload: false,
    additionalAssetsHotReload: false,
  };
}

function routingFromSettings(raw: Record<string, unknown>): RoutingDraft {
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
    modelProvidersHotReload: Boolean(raw.modelProvidersHotReload),
    additionalAssetsHotReload: Boolean(raw.additionalAssetsHotReload),
  };
}

interface HealthInfo {
  status: string;
  uptime: string;
  version: string;
}

export default function Settings() {
  const { t } = useLocale();
  const proxyContext = useProxySessionContext();
  const [settings, setSettings] = useState<ServerSettings | null>(null);
  const [routing, setRouting] = useState<RoutingDraft>(emptyRoutingDraft());
  const [health, setHealth] = useState<HealthInfo | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [toast, setToast] = useState('');
  const [saving, setSaving] = useState(false);
  const remoteProxyMode = Boolean(proxyContext?.selectedInstanceId);

  const showToast = (msg: string) => {
    setToast(msg);
    setTimeout(() => setToast(''), 4000);
  };

  const loadData = useCallback(async () => {
    try {
      const [s, h] = await Promise.all([
        getSettings().catch(() => null),
        getHealth().catch(() => null),
      ]);
      if (s) {
        const raw = s as Record<string, unknown>;
        setSettings(s as unknown as ServerSettings);
        setRouting(routingFromSettings(raw));
      }
      if (h) setHealth(h as unknown as HealthInfo);
      if (!s) setError(t('Failed to load settings.'));
    } catch {
      setError(t('Failed to load settings.'));
    } finally {
      setLoading(false);
    }
  }, [t]);

  useEffect(() => {
    loadData();
  }, [loadData]);

  const { seconds: refreshSeconds, setSeconds: setRefreshSeconds } = useAutoRefresh('settings', loadData);

  const handleSaveAll = async () => {
    if (!settings) return;
    setSaving(true);
    try {
      let familyClassificationRules: unknown;
      let withinTierPreferenceOrder: unknown;
      let modelProviders: unknown;
      let additionalPromptTemplates: unknown;
      let additionalPersonas: unknown;
      let additionalPipelines: unknown;
      try {
        familyClassificationRules = JSON.parse(routing.familyClassificationRules || '[]');
        withinTierPreferenceOrder = JSON.parse(routing.withinTierPreferenceOrder || '{}');
        modelProviders = JSON.parse(routing.modelProviders || '{"providers":{}}');
        additionalPromptTemplates = JSON.parse(routing.additionalPromptTemplates || '[]');
        additionalPersonas = JSON.parse(routing.additionalPersonas || '[]');
        additionalPipelines = JSON.parse(routing.additionalPipelines || '[]');
      } catch {
        showToast(t('Routing JSON is not valid. Fix the highlighted fields and save again.'));
        return;
      }

      const updated = await updateSettings({
        admiralPort: settings.admiralPort,
        mcpPort: settings.mcpPort,
        maxCaptains: settings.maxCaptains,
        heartbeatIntervalSeconds: settings.heartbeatIntervalSeconds,
        stallThresholdMinutes: settings.stallThresholdMinutes,
        idleCaptainTimeoutSeconds: settings.idleCaptainTimeoutSeconds,
        autoCreatePr: settings.autoCreatePr,
        modelTier: {
          midTierModels: linesToList(routing.midTierModels),
          highTierModels: linesToList(routing.highTierModels),
          specialistPersonas: linesToList(routing.specialistPersonas),
          reservedHighTierSlots: routing.reservedHighTierSlots,
          preferNonNativeFirst: routing.preferNonNativeFirst,
          withinTierStrategy: routing.withinTierStrategy,
          withinTierPreferenceOrder,
          familyClassificationRules,
        },
        voyageDispatch: {
          rejectStagePersonaTitlePrefixes: routing.rejectStagePersonaTitlePrefixes,
          stagePersonaTitlePrefixes: linesToList(routing.stagePersonaTitlePrefixes),
        },
        modelProviders,
        additionalPromptTemplates,
        additionalPersonas,
        additionalPipelines,
      });
      const raw = updated as Record<string, unknown>;
      setSettings(updated as unknown as ServerSettings);
      setRouting(routingFromSettings(raw));
      showToast(t('Settings saved successfully'));
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : t('Unknown error');
      showToast(t('Failed to save settings: {{message}}', { message: msg }));
    } finally {
      setSaving(false);
    }
  };

  if (loading) {
    return (
      <div>
        <div className="page-header">
          <h2>{t('Settings')}</h2>
        </div>
        <p className="text-muted">{t('Loading settings...')}</p>
      </div>
    );
  }

  return (
    <div>
      <PageHeader
        title={t('Settings')}
        subtitle={t('View and modify server configuration.')}
        actions={(
          <>
            <AutoRefreshSelect seconds={refreshSeconds} onChange={setRefreshSeconds} />
            <RefreshButton onRefresh={loadData} title={t('Refresh settings')} />
          </>
        )}
      />

      <ErrorModal error={error} onClose={() => setError('')} />
      {toast && (
        <div className="alert alert-success" style={{ marginBottom: '1rem' }}>
          {toast}
        </div>
      )}
      {remoteProxyMode && (
        <div className="alert alert-warning" style={{ marginBottom: '1rem' }}>
          {t(
            'This page is connected through Armada.Proxy for {{instanceId}}. Editing general settings is blocked in remote mode.',
            { instanceId: proxyContext?.selectedInstanceId ?? t('the selected deployment') },
          )}
        </div>
      )}

      {/* Server Info */}
      {health && (
        <div className="card" style={{ marginBottom: '1.5rem' }}>
          <h3>{t('Server Info')}</h3>
          <div className="detail-grid">
            <div className="detail-field">
              <span className="detail-label">{t('Version')}</span>
              <span className="mono">{health.version || '-'}</span>
            </div>
            <div className="detail-field">
              <span className="detail-label">{t('Status')}</span>
              <span className={`status ${health.status === 'healthy' ? 'status-active' : 'status-stopped'}`}>
                {t(health.status)}
              </span>
            </div>
            <div className="detail-field">
              <span className="detail-label">{t('Uptime')}</span>
              <span className="mono">{health.uptime || '-'}</span>
            </div>
          </div>
        </div>
      )}

      {settings && (
        <>
          <fieldset disabled={remoteProxyMode} style={{ border: 'none', margin: 0, padding: 0 }}>
          {/* Server Configuration */}
          <div className="settings-section">
            <h3>{t('Server Configuration')}</h3>
            <div className="settings-grid">
              <div className="form-group">
                <label>{t('Admiral Port')}</label>
                <input
                  type="number"
                  value={settings.admiralPort}
                  onChange={(e) =>
                    setSettings({ ...settings, admiralPort: parseInt(e.target.value) || 0 })
                  }
                  min={1}
                  max={65535}
                  title={t('REST API port (1-65535)')}
                />
              </div>
              <div className="form-group">
                <label>{t('MCP Port')}</label>
                <input
                  type="number"
                  value={settings.mcpPort}
                  onChange={(e) =>
                    setSettings({ ...settings, mcpPort: parseInt(e.target.value) || 0 })
                  }
                  min={1}
                  max={65535}
                  title={t('MCP server port (1-65535)')}
                />
              </div>
              <div className="form-group">
                <label>{t('Max Captains')}</label>
                <input
                  type="number"
                  value={settings.maxCaptains}
                  onChange={(e) =>
                    setSettings({ ...settings, maxCaptains: parseInt(e.target.value) || 0 })
                  }
                  min={0}
                  title={t('Maximum captains (0 = unlimited)')}
                />
              </div>
            </div>
          </div>

          {/* Agent Settings */}
          <div className="settings-section" style={{ marginTop: '1.5rem' }}>
            <h3>{t('Agent Settings')}</h3>
            <div className="settings-grid">
              <div className="form-group">
                <label>{t('Heartbeat Interval (seconds)')}</label>
                <input
                  type="number"
                  value={settings.heartbeatIntervalSeconds}
                  onChange={(e) =>
                    setSettings({
                      ...settings,
                      heartbeatIntervalSeconds: parseInt(e.target.value) || 5,
                    })
                  }
                  min={5}
                  title={t('Health check interval, minimum 5 seconds')}
                />
              </div>
              <div className="form-group">
                <label>{t('Stall Threshold (minutes)')}</label>
                <input
                  type="number"
                  value={settings.stallThresholdMinutes}
                  onChange={(e) =>
                    setSettings({
                      ...settings,
                      stallThresholdMinutes: parseInt(e.target.value) || 1,
                    })
                  }
                  min={1}
                  title={t('Minutes before a captain is considered stalled')}
                />
              </div>
              <div className="form-group">
                <label>{t('Idle Captain Timeout (seconds)')}</label>
                <input
                  type="number"
                  value={settings.idleCaptainTimeoutSeconds}
                  onChange={(e) =>
                    setSettings({
                      ...settings,
                      idleCaptainTimeoutSeconds: parseInt(e.target.value) || 0,
                    })
                  }
                  min={0}
                  title={t('Auto-remove idle captains after this many seconds (0 = disabled)')}
                />
              </div>
              <div className="form-group">
                <label className="settings-checkbox-label">
                  <input
                    type="checkbox"
                    checked={settings.autoCreatePr}
                    onChange={(e) => setSettings({ ...settings, autoCreatePr: e.target.checked })}
                  />
                  <span>{t('Auto-Create Pull Requests')}</span>
                </label>
              </div>
            </div>
          </div>
          </fieldset>

          <fieldset disabled={remoteProxyMode} style={{ border: 'none', margin: 0, padding: 0 }}>
          <div className="settings-section" style={{ marginTop: '1.5rem' }}>
            <h3>{t('Model routing')}</h3>
            <p className="text-muted">
              {t('Tier lists, family rules, and routing policy hot-reload. Empty lists are the product default: random assignment among idle captains, with no model-family or specialist assumption.')}
            </p>
            <div className="settings-grid">
              <div className="form-group">
                <label>{t('Mid-tier models (one per line)')}</label>
                <textarea
                  rows={5}
                  value={routing.midTierModels}
                  onChange={(e) => setRouting({ ...routing, midTierModels: e.target.value })}
                  title={t('Concrete model ids that classify as mid')}
                />
              </div>
              <div className="form-group">
                <label>{t('High-tier models (one per line)')}</label>
                <textarea
                  rows={5}
                  value={routing.highTierModels}
                  onChange={(e) => setRouting({ ...routing, highTierModels: e.target.value })}
                  title={t('Concrete model ids that classify as high')}
                />
              </div>
              <div className="form-group">
                <label>{t('Specialist personas (one per line)')}</label>
                <textarea
                  rows={5}
                  value={routing.specialistPersonas}
                  onChange={(e) => setRouting({ ...routing, specialistPersonas: e.target.value })}
                  title={t('Personas reserved for high-tier captains')}
                />
              </div>
              <div className="form-group">
                <label>{t('Reserved high-tier slots')}</label>
                <input
                  type="number"
                  min={0}
                  max={10}
                  value={routing.reservedHighTierSlots}
                  onChange={(e) =>
                    setRouting({ ...routing, reservedHighTierSlots: parseInt(e.target.value) || 0 })
                  }
                  title={t('Idle high-tier slots held for specialist work (0 disables)')}
                />
              </div>
              <div className="form-group">
                <label>{t('Within-tier strategy')}</label>
                <select
                  value={routing.withinTierStrategy}
                  onChange={(e) => setRouting({ ...routing, withinTierStrategy: e.target.value })}
                  title={t('Random is the product default')}
                >
                  <option value="Random">{t('Random')}</option>
                  <option value="PreferenceOrderThenRandom">{t('Preference order, then random')}</option>
                </select>
              </div>
              <div className="form-group">
                <label className="settings-checkbox-label">
                  <input
                    type="checkbox"
                    checked={routing.preferNonNativeFirst}
                    onChange={(e) =>
                      setRouting({ ...routing, preferNonNativeFirst: e.target.checked })
                    }
                  />
                  <span>{t('Prefer non-native captains first')}</span>
                </label>
              </div>
              <div className="form-group" style={{ gridColumn: '1 / -1' }}>
                <label>{t('Within-tier preference order (JSON object)')}</label>
                <textarea
                  rows={6}
                  className="mono"
                  value={routing.withinTierPreferenceOrder}
                  onChange={(e) =>
                    setRouting({ ...routing, withinTierPreferenceOrder: e.target.value })
                  }
                  title={t('Used only when the strategy is Preference order, then random')}
                />
              </div>
              <div className="form-group" style={{ gridColumn: '1 / -1' }}>
                <label>{t('Family classification rules (JSON array of {pattern, tier})')}</label>
                <textarea
                  rows={6}
                  className="mono"
                  value={routing.familyClassificationRules}
                  onChange={(e) =>
                    setRouting({ ...routing, familyClassificationRules: e.target.value })
                  }
                  title={t('Regex patterns applied when a model is not in a tier list')}
                />
              </div>
            </div>
          </div>

          <div className="settings-section" style={{ marginTop: '1.5rem' }}>
            <h3>{t('Voyage dispatch guard')}</h3>
            <p className="text-muted">
              {t('Off by default. When on, a mission title that opens with a listed stage-persona prefix is rejected.')}
            </p>
            <div className="settings-grid">
              <div className="form-group">
                <label className="settings-checkbox-label">
                  <input
                    type="checkbox"
                    checked={routing.rejectStagePersonaTitlePrefixes}
                    onChange={(e) =>
                      setRouting({
                        ...routing,
                        rejectStagePersonaTitlePrefixes: e.target.checked,
                      })
                    }
                  />
                  <span>{t('Reject stage-persona title prefixes')}</span>
                </label>
              </div>
              <div className="form-group" style={{ gridColumn: '1 / -1' }}>
                <label>{t('Stage-persona prefixes (one per line, include trailing space)')}</label>
                <textarea
                  rows={5}
                  value={routing.stagePersonaTitlePrefixes}
                  onChange={(e) =>
                    setRouting({ ...routing, stagePersonaTitlePrefixes: e.target.value })
                  }
                  title={t('Example: [Worker] ')}
                />
              </div>
            </div>
          </div>

          <div className="settings-section" style={{ marginTop: '1.5rem' }}>
            <h3>{t('Model providers')}</h3>
            <p className="text-muted">
              {routing.modelProvidersHotReload
                ? t('Model providers hot-reload on save.')
                : t('Model providers load at startup. Restart the Admiral after you save this block.')}
            </p>
            <div className="form-group">
              <label>{t('modelProviders JSON')}</label>
              <textarea
                rows={8}
                className="mono"
                value={routing.modelProviders}
                onChange={(e) => setRouting({ ...routing, modelProviders: e.target.value })}
              />
            </div>
          </div>

          <div className="settings-section" style={{ marginTop: '1.5rem' }}>
            <h3>{t('Additional personas, pipelines, and templates')}</h3>
            <p className="text-muted">
              {routing.additionalAssetsHotReload
                ? t('Additional assets hot-reload on save.')
                : t('Additional prompt templates, personas, and pipelines seed at startup. Restart the Admiral after you save these blocks.')}
            </p>
            <div className="form-group">
              <label>{t('additionalPromptTemplates JSON array')}</label>
              <textarea
                rows={8}
                className="mono"
                value={routing.additionalPromptTemplates}
                onChange={(e) =>
                  setRouting({ ...routing, additionalPromptTemplates: e.target.value })
                }
              />
            </div>
            <div className="form-group">
              <label>{t('additionalPersonas JSON array')}</label>
              <textarea
                rows={6}
                className="mono"
                value={routing.additionalPersonas}
                onChange={(e) => setRouting({ ...routing, additionalPersonas: e.target.value })}
              />
            </div>
            <div className="form-group">
              <label>{t('additionalPipelines JSON array')}</label>
              <textarea
                rows={8}
                className="mono"
                value={routing.additionalPipelines}
                onChange={(e) => setRouting({ ...routing, additionalPipelines: e.target.value })}
              />
            </div>
          </div>
          </fieldset>

          {/* System Paths (read-only) */}
          <div className="settings-section" style={{ marginTop: '1.5rem' }}>
            <h3>{t('System Paths')}</h3>
            <div className="detail-grid">
              <div className="detail-field">
                <span className="detail-label">{t('Data Directory')}</span>
                <span className="mono">{settings.dataDirectory || '-'}</span>
              </div>
              <div className="detail-field">
                <span className="detail-label">{t('Database Path')}</span>
                <span className="mono">{settings.databasePath || '-'}</span>
              </div>
              <div className="detail-field">
                <span className="detail-label">{t('Log Directory')}</span>
                <span className="mono">{settings.logDirectory || '-'}</span>
              </div>
              <div className="detail-field">
                <span className="detail-label">{t('Docks Directory')}</span>
                <span className="mono">{settings.docksDirectory || '-'}</span>
              </div>
              <div className="detail-field">
                <span className="detail-label">{t('Repos Directory')}</span>
                <span className="mono">{settings.reposDirectory || '-'}</span>
              </div>
            </div>
          </div>

          {/* Save Button */}
          <div style={{ marginTop: '1.5rem' }}>
            <button
              className="btn-primary"
              onClick={handleSaveAll}
              disabled={saving || remoteProxyMode}
              title={remoteProxyMode ? t('Settings are blocked in proxy mode') : t('Save all settings')}
            >
              {saving ? t('Saving...') : t('Save All Settings')}
            </button>
          </div>
        </>
      )}
    </div>
  );
}
