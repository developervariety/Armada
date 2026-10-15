import '../vessels/history/vesselHistory.css';
import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { getTokenUsage } from '../../api/client';
import type { TokenUsageSummaryResult } from '../../types/models';
import { useLocale } from '../../context/LocaleContext';
import { addDays, buildWeeks, heatLevel, tokenHeatmapDays, tokenHeatmapStats, tokenHeatmapWindow, TOKEN_HEATMAP_METRICS, type TokenHeatmapMetric } from '../../lib/tokenHeatmap';
import { buildMonthLabels, formatIsoDay } from '../../lib/vesselHistory';
import { formatTokens } from '../../lib/tokenUsage';

/** Daily UTC token activity, alongside the existing monitoring charts. */
export default function TokenActivityHeatmap({ harborId = '' }: { harborId?: string }) {
  const { t, locale } = useLocale();
  const [range, setRange] = useState(365);
  const [metric, setMetric] = useState<TokenHeatmapMetric>('total');
  const [data, setData] = useState<TokenUsageSummaryResult | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);
  const [selected, setSelected] = useState('');
  const [refresh, setRefresh] = useState(0);
  const grid = useRef<HTMLDivElement>(null);
  const scroll = useRef<HTMLDivElement>(null);
  const window = useMemo(() => tokenHeatmapWindow(range), [range, refresh]);
  useEffect(() => {
    let active = true;
    setLoading(true); setError(false); setData(null);
    getTokenUsage({ fromUtc: window.fromUtc, toUtc: window.toUtc, bucketMinutes: 1440, harborId: harborId || undefined })
      .then(value => { if (active) setData(value); })
      .catch(() => { if (active) setError(true); })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [window, harborId]);
  const days = useMemo(() => tokenHeatmapDays(data, window.from, window.to, metric), [data, window, metric]);
  const weeks = buildWeeks(days);
  const months = buildMonthLabels(weeks);
  const stats = tokenHeatmapStats(days);
  const activeDate = selected >= window.from && selected <= window.to ? selected : window.to;
  const day = days.find(d => d.date === activeDate)!;
  useEffect(() => {
    const host = scroll.current;
    const cell = grid.current?.querySelector<HTMLElement>(`[data-date="${activeDate}"]`);
    if (!loading && host && cell) {
      const x = cell.getBoundingClientRect().left - host.getBoundingClientRect().left + host.scrollLeft;
      if (x < host.scrollLeft) host.scrollLeft = x;
      if (x + 20 > host.scrollLeft + host.clientWidth) host.scrollLeft = x + 20 - host.clientWidth;
    }
  }, [activeDate, loading, window]);
  const describe = (date: string, count: number) => `${formatIsoDay(locale, date, { dateStyle: 'full' })} (UTC): ${count.toLocaleString(locale)} ${t('Tokens')}`;
  function focusDate(date: string) {
    if (date < window.from || date > window.to) return;
    setSelected(date);
    grid.current?.querySelector<HTMLElement>(`[data-date="${date}"]`)?.focus();
  }
  function keyDown(e: KeyboardEvent<HTMLDivElement>) {
    const date = (e.target as HTMLElement).dataset.date;
    if (!date) return;
    const offsets: Record<string, number> = { ArrowLeft: -7, ArrowRight: 7, ArrowUp: -1, ArrowDown: 1 };
    if (e.key in offsets) { e.preventDefault(); focusDate(addDays(date, offsets[e.key])); }
    if (e.key === 'Home' || e.key === 'End') { e.preventDefault(); focusDate(e.key === 'Home' ? window.from : window.to); }
  }
  return <section className="token-usage-chart-card token-activity-card vhist-heatmap" aria-label={t('Token activity')}>
    <div className="token-activity-header"><div><h3 title={t('One square per UTC day; darker squares mean more tokens that day. Click or use arrow keys to read a day\'s total.')}>{t('Token activity')}</h3><p className="text-dim">{t('Daily token totals (UTC)')}</p></div>
      <div className="token-activity-controls">
        <label title={t('How many days of history the heatmap covers, ending today (UTC). Independent of the time window above.')}>{t('Heatmap range')}<select title={t('How many days of history the heatmap covers, ending today (UTC). Independent of the time window above.')} value={range} onChange={e => setRange(Number(e.target.value))}>
          <option value={31}>{t('Last Month')}</option><option value={90}>{t('Last 3 months')}</option><option value={365}>{t('Last 12 months')}</option>
        </select></label>
        <label title={t('Which tokens each day\'s color counts: total, input (prompt), output (completion), or cached (served from the prompt cache).')}>{t('Heatmap metric')}<select title={t('Which tokens each day\'s color counts: total, input (prompt), output (completion), or cached (served from the prompt cache).')} value={metric} onChange={e => setMetric(e.target.value as TokenHeatmapMetric)}>
          {TOKEN_HEATMAP_METRICS.map(m => <option key={m} value={m}>{t(m[0].toUpperCase() + m.slice(1))}</option>)}
        </select></label>
        <button className="btn btn-sm" type="button" onClick={() => setRefresh(r => r + 1)} title={t('Reload the daily token totals now.')}>{t('Refresh token activity')}</button>
      </div>
    </div>
    {loading ? <p role="status">{t('Loading token usage...')}</p> : error ? <p role="alert">{t('Unable to load token activity.')}</p> : <>
      <div className="token-activity-stats"><span title={t('Sum of the chosen metric over the heatmap range.')}>{t('Total')}: <strong>{formatTokens(stats.total)}</strong></span><span title={t('Highest single-day count in the range. It sets the darkest color.')}>{t('Peak day')}: <strong>{formatTokens(stats.peak)}</strong></span><span title={t('Days in the range with any token usage.')}>{t('Active days')}: <strong>{stats.active}</strong></span></div>
      <div ref={scroll} className="vhist-heatmap-scroll token-activity-scroll">
        <div className="vhist-heatmap-months" style={{ '--heat-weeks': weeks.length } as React.CSSProperties} aria-hidden="true">
          <span className="vhist-heatmap-corner" />{months.map(m => <span className="vhist-heatmap-month" key={`${m.year}-${m.month}`} style={{ gridColumn: `${m.column + 2} / span 3` }}>{formatIsoDay(locale, `${m.year}-${String(m.month).padStart(2, '0')}-01`, { month: 'short' })}</span>)}
        </div>
        <div ref={grid} className="vhist-heatmap-grid" style={{ '--heat-weeks': weeks.length } as React.CSSProperties} role="grid" aria-label={t('Token activity')} onKeyDown={keyDown}>
          {Array.from({ length: 7 }, (_, weekday) => <div role="row" className="vhist-heatmap-row" key={weekday}>
            <span role="rowheader" className="vhist-heatmap-weekday">{formatIsoDay(locale, `2023-01-0${weekday + 1}`, { weekday: 'short' })}</span>
            {weeks.map((week, column) => {
              const cell = week[weekday];
              if (!cell) return <span aria-hidden="true" className="vhist-heat-cell vhist-heat-pad" key={column} />;
              return <button type="button" role="gridcell" key={cell.date} data-date={cell.date} data-level={heatLevel(cell.count, stats.peak)} title={describe(cell.date, cell.count)} aria-label={describe(cell.date, cell.count)} aria-selected={cell.date === activeDate} tabIndex={cell.date === activeDate ? 0 : -1} className={`vhist-heat-cell level-${heatLevel(cell.count, stats.peak)}${cell.date === activeDate ? ' selected' : ''}`} onFocus={() => setSelected(cell.date)} onClick={() => setSelected(cell.date)} />;
            })}
          </div>)}
        </div>
      </div>
      <div className="token-activity-footer"><span aria-live="polite">{describe(day.date, day.count)}</span><span className="token-activity-legend" title={t('Color scale: lighter squares had fewer tokens, darker squares had more, relative to the peak day.')}>{t('Less')}{[0, 1, 2, 3, 4].map(level => <span key={level} className={`vhist-heat-cell level-${level}`} />)}{t('More')}</span></div>
      {stats.active === 0 && <p className="text-dim">{t('No token usage for this time range')}</p>}
      {(data?.syntheticCount ?? 0) > 0 && <p className="text-dim" title={t('Simulated usage an administrator imported, for example for screenshots. It is counted in these totals but is not real model usage.')}>{t('{{count}} synthetic usage records included', { count: data!.syntheticCount })}</p>}
    </>}
  </section>;
}
