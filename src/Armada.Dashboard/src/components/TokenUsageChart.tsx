import { useEffect, useMemo, useState } from 'react';
import { getTokenUsage } from '../api/client';
import type { TokenUsageSummary } from '../types/models';
import { useLocale } from '../context/LocaleContext';

const RANGES = [
  { label: '7 Days', days: 7 },
  { label: '30 Days', days: 30 },
  { label: '90 Days', days: 90 },
  { label: 'All', days: 3650 },
];

function formatTokens(value: number): string {
  return new Intl.NumberFormat(undefined, { notation: 'compact', maximumFractionDigits: 1 }).format(value);
}

export default function TokenUsageChart() {
  const { t } = useLocale();
  const [days, setDays] = useState(30);
  const [usage, setUsage] = useState<TokenUsageSummary | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    getTokenUsage(days)
      .then(result => {
        if (!cancelled) setUsage(result);
      })
      .catch(() => {
        if (!cancelled) setUsage(null);
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [days]);

  const maximum = useMemo(
    () => Math.max(1, ...(usage?.models.map(model => model.totalTokens) ?? [])),
    [usage],
  );

  return (
    <section className="token-usage-section">
      <div className="token-usage-header">
        <div>
          <span className="token-usage-title">{t('Token Usage')}</span>
          <span className="token-usage-accuracy">{t('Provider reported')}</span>
        </div>
        <div className="mission-history-time-tabs">
          {RANGES.map(range => (
            <button
              key={range.days}
              className={'mission-history-time-tab' + (days === range.days ? ' active' : '')}
              onClick={() => setDays(range.days)}
            >
              {t(range.label)}
            </button>
          ))}
        </div>
      </div>

      {loading ? (
        <div className="mission-history-empty">{t('Loading token usage...')}</div>
      ) : !usage || usage.models.length === 0 ? (
        <div className="mission-history-empty">
          {t('No authoritative token usage has been recorded for this time range.')}
        </div>
      ) : (
        <>
          <div className="token-usage-stats">
            <span><strong>{formatTokens(usage.totalTokens)}</strong> {t('total')}</span>
            <span><strong>{formatTokens(usage.inputTokens)}</strong> {t('input')}</span>
            <span><strong>{formatTokens(usage.outputTokens)}</strong> {t('output')}</span>
            <span><strong>{formatTokens(usage.reportedMissionCount)}</strong> {t('missions')}</span>
          </div>
          <div className="token-usage-chart" role="img" aria-label={t('Token usage by runtime and model')}>
            {usage.models.map(model => {
              const width = (model.totalTokens / maximum) * 100;
              const inputWidth = model.totalTokens > 0 ? (model.inputTokens / model.totalTokens) * 100 : 0;
              const outputWidth = model.totalTokens > 0 ? (model.outputTokens / model.totalTokens) * 100 : 0;
              return (
                <div className="token-usage-row" key={`${model.runtime}:${model.model}`}>
                  <div className="token-usage-label">
                    <strong>{model.model}</strong>
                    <span>{model.runtime} · {model.missionCount} {t('missions')}</span>
                  </div>
                  <div className="token-usage-bar-track">
                    <div className="token-usage-bar" style={{ width: `${width}%` }}>
                      <span className="token-usage-input" style={{ width: `${inputWidth}%` }} />
                      <span className="token-usage-output" style={{ width: `${outputWidth}%` }} />
                    </div>
                  </div>
                  <div className="token-usage-value" title={`${model.totalTokens.toLocaleString()} ${t('tokens')}`}>
                    {formatTokens(model.totalTokens)}
                  </div>
                  <div className="token-usage-detail">
                    {t('input')} {formatTokens(model.inputTokens)} · {t('output')} {formatTokens(model.outputTokens)}
                    {model.reasoningTokens > 0 ? ` · ${t('reasoning')} ${formatTokens(model.reasoningTokens)}` : ''}
                    {model.cacheReadTokens > 0 ? ` · ${t('cache read')} ${formatTokens(model.cacheReadTokens)}` : ''}
                    {model.cacheWriteTokens > 0 ? ` · ${t('cache write')} ${formatTokens(model.cacheWriteTokens)}` : ''}
                  </div>
                </div>
              );
            })}
          </div>
          <div className="token-usage-legend">
            <span><i className="token-usage-input" /> {t('Input')}</span>
            <span><i className="token-usage-output" /> {t('Output')}</span>
            <span title={usage.coverageNote}>{usage.coverageNote}</span>
          </div>
        </>
      )}
    </section>
  );
}
