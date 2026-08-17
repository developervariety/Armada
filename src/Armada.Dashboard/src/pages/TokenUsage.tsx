import { useEffect, useMemo, useState } from 'react';
import { getTokenUsage } from '../api/client';
import type { TokenUsageSummaryResult } from '../types/models';
import { useLocale } from '../context/LocaleContext';

const TIME_RANGES = [
  { label: 'Last Hour', value: 'hour', hours: 1, stepMinutes: 1 },
  { label: 'Last Day', value: 'day', hours: 24, stepMinutes: 15 },
  { label: 'Last Week', value: 'week', hours: 168, stepMinutes: 60 },
  { label: 'Last Month', value: 'month', hours: 720, stepMinutes: 360 },
] as const;

type TimeRangeValue = typeof TIME_RANGES[number]['value'];
type Metric = 'total' | 'byType';
type Shape = 'bars' | 'lines';

// Per-model palette: CSS variables first, then hex fallbacks so a large model set still gets distinct colors.
const MODEL_COLORS = [
  'var(--accent)',
  'var(--green)',
  'var(--red)',
  '#a855f7',
  '#06b6d4',
  '#ec4899',
  'var(--text-dim)',
  '#14b8a6',
];

// Token-type colors (input / output / cached) share one legend across both charts.
const TYPE_COLORS = {
  input: 'var(--accent)',
  output: 'var(--green)',
  cached: 'var(--orange)',
} as const;

interface SeriesDef {
  key: string;
  label: string;
  color: string;
}

function computeYTicks(max: number): number[] {
  if (max <= 0) return [0];
  const rawStep = max / 4;
  const magnitude = Math.pow(10, Math.floor(Math.log10(rawStep)));
  const normalized = rawStep / magnitude;
  let niceNormalized: number;
  if (normalized <= 1) niceNormalized = 1;
  else if (normalized <= 2) niceNormalized = 2;
  else if (normalized <= 5) niceNormalized = 5;
  else niceNormalized = 10;
  const step = niceNormalized * magnitude;
  const ticks: number[] = [];
  for (let value = 0; value <= max; value += step) ticks.push(value);
  if (ticks[ticks.length - 1] < max) ticks.push(ticks[ticks.length - 1] + step);
  return ticks;
}

function formatTokens(value: number): string {
  const abs = Math.abs(value);
  if (abs >= 1e9) return trimZero(value / 1e9) + 'B';
  if (abs >= 1e6) return trimZero(value / 1e6) + 'M';
  if (abs >= 1e3) return trimZero(value / 1e3) + 'K';
  return String(Math.round(value));
}

function trimZero(value: number): string {
  const fixed = value.toFixed(1);
  return fixed.endsWith('.0') ? fixed.slice(0, -2) : fixed;
}

function formatBucketLabel(ts: number, stepMinutes: number, hours: number): string {
  const d = new Date(ts);
  if (stepMinutes <= 15) return d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
  if (hours > 48) return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' }) + ' ' + d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
  return d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
}

function formatTooltipTime(ts: number): string {
  const d = new Date(ts);
  return d.toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
}

export default function TokenUsage() {
  const { t } = useLocale();
  const [timeRange, setTimeRange] = useState<TimeRangeValue>('day');
  const [metric, setMetric] = useState<Metric>('total');
  const [shape, setShape] = useState<Shape>('bars');
  const [hoveredBar, setHoveredBar] = useState<number | null>(null);
  const [hoveredModel, setHoveredModel] = useState<number | null>(null);
  const [data, setData] = useState<TokenUsageSummaryResult | null>(null);
  const [loading, setLoading] = useState(true);

  const range = TIME_RANGES.find(r => r.value === timeRange)!;

  useEffect(() => {
    let cancelled = false;
    const end = new Date();
    const start = new Date(end.getTime() - range.hours * 3600000);

    setLoading(true);
    getTokenUsage({
      fromUtc: start.toISOString(),
      toUtc: end.toISOString(),
      bucketMinutes: range.stepMinutes,
    })
      .then((result) => {
        if (!cancelled) setData(result);
      })
      .catch(() => {
        if (!cancelled) setData(null);
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [range.hours, range.stepMinutes, timeRange]);

  const bucketTimestamps = useMemo<number[]>(
    () => (data?.buckets || []).map(b => new Date(b.bucketStartUtc).getTime()),
    [data],
  );

  // Series definitions and per-bucket values for chart 1, driven by the metric toggle.
  const series = useMemo<SeriesDef[]>(() => {
    if (metric === 'byType') {
      return [
        { key: 'input', label: t('Input'), color: TYPE_COLORS.input },
        { key: 'output', label: t('Output'), color: TYPE_COLORS.output },
        { key: 'cached', label: t('Cached'), color: TYPE_COLORS.cached },
      ];
    }
    return (data?.byModel || []).map((m, i) => ({
      key: m.model,
      label: m.model,
      color: MODEL_COLORS[i % MODEL_COLORS.length],
    }));
  }, [data, metric, t]);

  const bucketValues = useMemo<number[][]>(() => {
    const buckets = data?.buckets || [];
    if (metric === 'byType') {
      return buckets.map(b => [b.inputTokens, b.outputTokens, b.cachedTokens]);
    }
    return buckets.map(b => series.map(s => {
      const entry = b.models.find(m => m.model === s.key);
      return entry ? entry.totalTokens : 0;
    }));
  }, [data, metric, series]);

  const stacked = shape === 'bars';
  const maxVal = useMemo(() => {
    if (bucketValues.length === 0) return 1;
    if (stacked) return Math.max(1, ...bucketValues.map(row => row.reduce((a, b) => a + b, 0)));
    return Math.max(1, ...bucketValues.flat());
  }, [bucketValues, stacked]);

  const yTicks = computeYTicks(maxVal);
  const yMax = yTicks[yTicks.length - 1] || 1;

  const chartHeight = 200;
  const padTop = 20, padBot = 40, padLeft = 56, padRight = 16;
  const barAreaHeight = chartHeight - padTop - padBot;
  const barAreaWidth = 800 - padLeft - padRight;

  const totalInput = data?.inputTokens ?? 0;
  const totalOutput = data?.outputTokens ?? 0;
  const totalCached = data?.cachedTokens ?? 0;
  const totalTokens = data?.totalTokens ?? 0;
  const byModel = data?.byModel || [];
  const maxModelTotal = Math.max(1, ...byModel.map(m => m.totalTokens));

  const hasData = bucketTimestamps.length > 0 && series.length > 0;

  return (
    <div className="mission-history-section">
      <div className="mission-history-header">
        <span className="mission-history-title">{t('Token Usage')}</span>
        <div className="mission-history-controls">
          <div className="mission-history-time-tabs">
            {TIME_RANGES.map(r => (
              <button
                key={r.value}
                className={'mission-history-time-tab' + (timeRange === r.value ? ' active' : '')}
                onClick={() => setTimeRange(r.value)}
              >
                {t(r.label)}
              </button>
            ))}
          </div>
        </div>
      </div>

      <div className="token-usage-toggles">
        <div className="mission-history-time-tabs">
          <button className={'mission-history-time-tab' + (metric === 'total' ? ' active' : '')} onClick={() => setMetric('total')}>{t('Total')}</button>
          <button className={'mission-history-time-tab' + (metric === 'byType' ? ' active' : '')} onClick={() => setMetric('byType')}>{t('By token type')}</button>
        </div>
        <div className="mission-history-time-tabs">
          <button className={'mission-history-time-tab' + (shape === 'bars' ? ' active' : '')} onClick={() => setShape('bars')}>{t('Stacked bars')}</button>
          <button className={'mission-history-time-tab' + (shape === 'lines' ? ' active' : '')} onClick={() => setShape('lines')}>{t('Lines')}</button>
        </div>
      </div>

      <div className="mission-history-stats">
        <span><span className="mission-history-stat-value">{formatTokens(totalTokens)}</span> {t('Total')}</span>
        <span><span className="mission-history-stat-value" style={{ color: TYPE_COLORS.input }}>{formatTokens(totalInput)}</span> {t('Input')}</span>
        <span><span className="mission-history-stat-value" style={{ color: TYPE_COLORS.output }}>{formatTokens(totalOutput)}</span> {t('Output')}</span>
        <span><span className="mission-history-stat-value" style={{ color: TYPE_COLORS.cached }}>{formatTokens(totalCached)}</span> {t('Cached')}</span>
        {(data?.estimatedCount ?? 0) > 0 && (
          <span className="token-usage-estimated-note">
            {t('{{estimated}} of {{total}} records estimated', { estimated: data!.estimatedCount, total: data!.recordCount })}
          </span>
        )}
      </div>

      {loading ? (
        <div className="mission-history-empty">{t('Loading token usage...')}</div>
      ) : !hasData ? (
        <div className="mission-history-empty">{t('No token usage for this time range')}</div>
      ) : (
        <>
          <div className="token-usage-chart-title">{t('Usage over time')}</div>
          <div className="mission-history-chart-container">
            <svg width="100%" viewBox={`0 0 800 ${chartHeight}`} preserveAspectRatio="xMidYMid meet" style={{ display: 'block' }}>
              {yTicks.map(tick => {
                const y = padTop + barAreaHeight - (tick / yMax) * barAreaHeight;
                return (
                  <g key={tick}>
                    <line x1={padLeft} y1={y} x2={800 - padRight} y2={y} stroke="var(--border)" strokeDasharray={tick === 0 ? 'none' : '4,4'} strokeWidth={0.5} />
                    <text x={padLeft - 8} y={y + 3} textAnchor="end" fontSize="6.5" fill="var(--text-dim)">{formatTokens(tick)}</text>
                  </g>
                );
              })}
              {(() => {
                const barGroupWidth = barAreaWidth / bucketTimestamps.length;
                const barWidth = Math.max(2, Math.min(40, barGroupWidth * 0.7));
                const isLongLabel = range.hours > 48;
                const estLabelPx = isLongLabel ? 110 : 70;
                const maxLabels = Math.max(1, Math.floor(barAreaWidth / estLabelPx));
                const labelInterval = Math.max(1, Math.ceil(bucketTimestamps.length / maxLabels));

                const lineElements = !stacked ? series.map((s, si) => {
                  const points = bucketValues.map((row, i) => {
                    const x = padLeft + i * barGroupWidth + barGroupWidth / 2;
                    const y = padTop + barAreaHeight - (row[si] / yMax) * barAreaHeight;
                    return `${x.toFixed(2)},${y.toFixed(2)}`;
                  }).join(' ');
                  return <polyline key={s.key} points={points} fill="none" stroke={s.color} strokeWidth={1.4} strokeLinejoin="round" strokeLinecap="round" opacity={0.9} />;
                }) : null;

                return (
                  <>
                    {stacked && bucketValues.map((row, i) => {
                      const x = padLeft + i * barGroupWidth + (barGroupWidth - barWidth) / 2;
                      let cursor = padTop + barAreaHeight;
                      const isHovered = hoveredBar === i;
                      return (
                        <g key={i}>
                          {row.map((value, si) => {
                            if (value <= 0) return null;
                            const h = (value / yMax) * barAreaHeight;
                            cursor -= h;
                            return <rect key={series[si].key} x={x} y={cursor} width={barWidth} height={h} rx={1.5} fill={series[si].color} opacity={isHovered ? 1 : 0.85} />;
                          })}
                        </g>
                      );
                    })}
                    {lineElements}
                    {bucketTimestamps.map((ts, i) => {
                      const showLabel = i % labelInterval === 0;
                      return (
                        <g key={'hit' + i} onMouseEnter={() => setHoveredBar(i)} onMouseLeave={() => setHoveredBar(null)} style={{ cursor: 'default' }}>
                          <rect x={padLeft + i * barGroupWidth} y={padTop} width={barGroupWidth} height={barAreaHeight + padBot} fill="transparent" />
                          {showLabel && (
                            <text x={padLeft + i * barGroupWidth + barGroupWidth / 2} y={chartHeight - 8} textAnchor="middle" fontSize="6.5" fill="var(--text-dim)">
                              {formatBucketLabel(ts, range.stepMinutes, range.hours)}
                            </text>
                          )}
                        </g>
                      );
                    })}
                  </>
                );
              })()}
            </svg>
            {hoveredBar !== null && bucketValues[hoveredBar] && (
              <div className="mission-history-tooltip" style={{ left: `${((hoveredBar + 0.5) / bucketTimestamps.length) * 100}%` }}>
                <div style={{ fontWeight: 600, marginBottom: 4 }}>{formatTooltipTime(bucketTimestamps[hoveredBar])}</div>
                {series.map((s, si) => {
                  const value = bucketValues[hoveredBar][si];
                  if (value <= 0) return null;
                  return <div key={s.key}><span style={{ color: s.color }}>{s.label}:</span> {formatTokens(value)}</div>;
                })}
                <div>{t('Total')}: {formatTokens(bucketValues[hoveredBar].reduce((a, b) => a + b, 0))}</div>
              </div>
            )}
          </div>

          <div className="mission-history-legend">
            {series.map(s => (
              <span key={s.key}><span className="mission-history-legend-color" style={{ backgroundColor: s.color }} /> {s.label}</span>
            ))}
          </div>

          <div className="token-usage-chart-title">{t('Usage by model')}</div>
          <div className="token-usage-hbars">
            {byModel.map((m, i) => {
              const isHovered = hoveredModel === i;
              const inputPct = (m.inputTokens / maxModelTotal) * 100;
              const outputPct = (m.outputTokens / maxModelTotal) * 100;
              const cachedPct = (m.cachedTokens / maxModelTotal) * 100;
              const totalPct = (m.totalTokens / maxModelTotal) * 100;
              return (
                <div
                  key={m.model}
                  className="token-usage-hbar-row"
                  onMouseEnter={() => setHoveredModel(i)}
                  onMouseLeave={() => setHoveredModel(null)}
                >
                  <div className="token-usage-hbar-label" title={m.model}>{m.model}</div>
                  <div className="token-usage-hbar-track">
                    {metric === 'byType' ? (
                      <>
                        {m.inputTokens > 0 && <div className="token-usage-hbar-seg" style={{ width: `${inputPct}%`, background: TYPE_COLORS.input, opacity: isHovered ? 1 : 0.85 }} />}
                        {m.outputTokens > 0 && <div className="token-usage-hbar-seg" style={{ width: `${outputPct}%`, background: TYPE_COLORS.output, opacity: isHovered ? 1 : 0.85 }} />}
                        {m.cachedTokens > 0 && <div className="token-usage-hbar-seg" style={{ width: `${cachedPct}%`, background: TYPE_COLORS.cached, opacity: isHovered ? 1 : 0.85 }} />}
                      </>
                    ) : (
                      <div className="token-usage-hbar-seg" style={{ width: `${totalPct}%`, background: 'var(--accent)', opacity: isHovered ? 1 : 0.85 }} />
                    )}
                  </div>
                  <div className="token-usage-hbar-value">{formatTokens(m.totalTokens)}</div>
                  {isHovered && (
                    <div className="token-usage-hbar-tooltip">
                      <div style={{ fontWeight: 600, marginBottom: 4 }}>{m.model}</div>
                      <div><span style={{ color: TYPE_COLORS.input }}>{t('Input')}:</span> {formatTokens(m.inputTokens)}</div>
                      <div><span style={{ color: TYPE_COLORS.output }}>{t('Output')}:</span> {formatTokens(m.outputTokens)}</div>
                      <div><span style={{ color: TYPE_COLORS.cached }}>{t('Cached')}:</span> {formatTokens(m.cachedTokens)}</div>
                      <div>{t('Total')}: {formatTokens(m.totalTokens)}</div>
                    </div>
                  )}
                </div>
              );
            })}
          </div>
          {metric === 'byType' && (
            <div className="mission-history-legend">
              <span><span className="mission-history-legend-color" style={{ backgroundColor: TYPE_COLORS.input }} /> {t('Input')}</span>
              <span><span className="mission-history-legend-color" style={{ backgroundColor: TYPE_COLORS.output }} /> {t('Output')}</span>
              <span><span className="mission-history-legend-color" style={{ backgroundColor: TYPE_COLORS.cached }} /> {t('Cached')}</span>
            </div>
          )}
        </>
      )}
    </div>
  );
}
