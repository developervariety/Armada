import type { CaptainCredentialFormFields } from '../../lib/captainCredential';

interface ProviderCredentialFieldsProps {
  form: CaptainCredentialFormFields;
  onChange: (patch: Partial<CaptainCredentialFormFields>) => void;
  t: (key: string, vars?: Record<string, string | number | null | undefined>) => string;
  compact?: boolean;
}

/**
 * Per-captain Zyloo provider credential fields. The key wins over the host-level
 * ZYLOO_KEY environment variable, so captains on separate subscriptions can run
 * side by side on one Admiral.
 */
export default function ProviderCredentialFields({ form, onChange, t, compact = false }: ProviderCredentialFieldsProps) {
  return (
    <div style={{ marginTop: compact ? '0.5rem' : '0.75rem' }}>
      <details>
        <summary>{t('Provider Credential (Zyloo)')}</summary>
        <div className="wizard-form-grid" style={{ marginTop: '0.75rem' }}>
          <div className="form-group">
            <label title={t('Per-captain Zyloo credential used when the model is a zyloo/ model. Leave blank to use the host-level ZYLOO_KEY environment variable.')}>
              {t('Provider API Key')}
            </label>
            <input
              type="password"
              value={form.apiKey}
              onChange={(event) => onChange({ apiKey: event.target.value })}
              placeholder={t('Optional; overrides the host-level ZYLOO_KEY')}
              autoComplete="new-password"
            />
          </div>
          <div className="form-group">
            <label title={t('Optional provider base URL override for Zyloo-served models.')}>{t('Provider Base URL')}</label>
            <input
              value={form.apiBaseUrl}
              onChange={(event) => onChange({ apiBaseUrl: event.target.value })}
              placeholder={t('Optional, e.g. https://api.zyloo.io')}
            />
          </div>
        </div>
      </details>
    </div>
  );
}
