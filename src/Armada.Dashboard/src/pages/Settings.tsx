import { useEffect, useState, useCallback } from 'react';
import { getSettings, updateSettings, getHealth } from '../api/client';
import RefreshButton from '../components/shared/RefreshButton';
import ErrorModal from '../components/shared/ErrorModal';
import { useLocale } from '../context/LocaleContext';

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

interface HealthInfo {
  status: string;
  uptime: string;
  version: string;
}

export default function Settings() {
  const { t } = useLocale();
  const [settings, setSettings] = useState<ServerSettings | null>(null);
  const [health, setHealth] = useState<HealthInfo | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [toast, setToast] = useState('');
  const [saving, setSaving] = useState(false);

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
      if (s) setSettings(s as unknown as ServerSettings);
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

  const handleSaveAll = async () => {
    if (!settings) return;
    setSaving(true);
    try {
      const updated = await updateSettings({
        admiralPort: settings.admiralPort,
        mcpPort: settings.mcpPort,
        maxCaptains: settings.maxCaptains,
        heartbeatIntervalSeconds: settings.heartbeatIntervalSeconds,
        stallThresholdMinutes: settings.stallThresholdMinutes,
        idleCaptainTimeoutSeconds: settings.idleCaptainTimeoutSeconds,
        autoCreatePr: settings.autoCreatePr,
      });
      setSettings(updated as unknown as ServerSettings);
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
      <div className="page-header">
        <div>
          <h2>{t('Settings')}</h2>
          <p className="text-muted">{t('View and modify server configuration.')}</p>
        </div>
        <div className="page-actions">
          <RefreshButton onRefresh={loadData} title={t('Refresh settings')} />
        </div>
      </div>

      <ErrorModal error={error} onClose={() => setError('')} />
      {toast && (
        <div className="alert alert-success" style={{ marginBottom: '1rem' }}>
          {toast}
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
              disabled={saving}
              title={t('Save all settings')}
            >
              {saving ? t('Saving...') : t('Save All Settings')}
            </button>
          </div>
        </>
      )}
    </div>
  );
}
