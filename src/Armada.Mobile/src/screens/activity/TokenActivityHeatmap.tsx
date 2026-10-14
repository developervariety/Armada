import { useMemo, useRef, useState } from 'react';
import { Pressable, ScrollView, StyleSheet, View } from 'react-native';
import { getTokenUsage } from '@dashboard/api/client';
import { buildWeeks, heatLevel, TOKEN_HEATMAP_METRICS, tokenHeatmapDays, tokenHeatmapStats, tokenHeatmapWindow, type TokenHeatmapMetric } from '@dashboard/lib/tokenHeatmap';
import { buildMonthLabels, formatIsoDay } from '@dashboard/lib/vesselHistory';
import { formatTokens } from '@dashboard/lib/tokenUsage';
import { FieldCard } from '../../components/resource/DetailParts';
import { Button } from '../../components/ui/Button';
import { AppText } from '../../components/ui/AppText';
import { SegmentedControl } from '../../components/ui/SegmentedControl';
import { useLocale } from '../../i18n/LocaleContext';
import { useLoad } from '../../resource/useLoad';
import { useTheme } from '../../theme/ThemeContext';

/** UTC token calendar with accessible per-day selection, alongside monitoring charts. */
export function TokenActivityHeatmap({ harborId = '' }: { harborId?: string }) {
  const { t, locale } = useLocale();
  const { colors } = useTheme();
  const [range, setRange] = useState('365');
  const [metric, setMetric] = useState<TokenHeatmapMetric>('total');
  const [selected, setSelected] = useState('');
  const [refresh, setRefresh] = useState(() => Date.now());
  const scroll = useRef<ScrollView>(null);
  const window = useMemo(() => tokenHeatmapWindow(Number(range), new Date(refresh)), [range, refresh]);
  const { data, loading, error } = useLoad(() => getTokenUsage({ fromUtc: window.fromUtc, toUtc: window.toUtc, bucketMinutes: 1440, harborId: harborId || undefined }), [range, refresh, harborId]);
  const days = tokenHeatmapDays(data, window.from, window.to, metric);
  const weeks = buildWeeks(days);
  const months = buildMonthLabels(weeks);
  const stats = tokenHeatmapStats(days);
  const day = days.find(d => d.date === selected) ?? days[days.length - 1];
  const shade = (level: number) => level === 0 ? colors.track : colors.success;
  const opacity = (level: number) => level === 0 ? 1 : 0.35 + level * 0.1625;
  return <FieldCard title={t('Token activity')} testID="tokens-heatmap">
    <View style={styles.body}>
      <AppText variant="caption" muted>{t('Daily token totals (UTC)')}</AppText>
      <SegmentedControl label={t('Heatmap range')} value={range} onChange={setRange} options={[{ value: '31', label: t('Last Month') }, { value: '90', label: t('Last 3 months') }, { value: '365', label: t('Last 12 months') }]} />
      <SegmentedControl label={t('Heatmap metric')} value={metric} onChange={setMetric} options={TOKEN_HEATMAP_METRICS.map(m => ({ value: m, label: t(m[0].toUpperCase() + m.slice(1)) }))} />
      <Button label={t('Refresh token activity')} variant="ghost" onPress={() => setRefresh(Date.now())} />
      {loading ? <AppText muted>{t('Loading token usage...')}</AppText> : error ? <AppText>{t('Unable to load token activity.')}</AppText> : <>
        <AppText variant="caption">{`${t('Total')}: ${formatTokens(stats.total)} / ${t('Peak day')}: ${formatTokens(stats.peak)} / ${t('Active days')}: ${stats.active}`}</AppText>
        <ScrollView ref={scroll} horizontal showsHorizontalScrollIndicator onContentSizeChange={() => scroll.current?.scrollToEnd({ animated: false })}>
          <View>
            <View style={{ height: 22, marginLeft: 32, width: weeks.length * 18 }}>{months.map(m => <AppText variant="caption" key={`${m.year}-${m.month}`} style={{ position: 'absolute', left: m.column * 18 }}>{formatIsoDay(locale, `${m.year}-${String(m.month).padStart(2, '0')}-01`, { month: 'short' })}</AppText>)}</View>
            {Array.from({ length: 7 }, (_, row) => <View style={styles.row} key={row}>
              <AppText variant="caption" style={styles.weekday}>{formatIsoDay(locale, `2023-01-0${row + 1}`, { weekday: 'short' })}</AppText>
              {weeks.map((week, col) => {
                const cell = week[row];
                if (!cell) return <View key={col} style={styles.cell} />;
                const level = heatLevel(cell.count, stats.peak);
                return <Pressable key={cell.date} testID={`tokens-heat-day-${cell.date}`} hitSlop={4} accessibilityRole="button" accessibilityLabel={`${cell.date} UTC: ${cell.count} ${t('Tokens')}`} accessibilityState={{ selected: cell.date === day.date }} onPress={() => setSelected(cell.date)} style={[styles.cell, { backgroundColor: shade(level), opacity: opacity(level), borderColor: cell.date === day.date ? colors.text : 'transparent', borderWidth: 1 }]} />;
              })}
            </View>)}
          </View>
        </ScrollView>
        <AppText variant="caption" accessibilityLiveRegion="polite">{`${day.date} UTC: ${day.count.toLocaleString(locale)} ${t('Tokens')}`}</AppText>
        <View style={styles.legend}><AppText variant="caption">{t('Less')}</AppText>{[0, 1, 2, 3, 4].map(level => <View key={level} style={[styles.cell, { backgroundColor: shade(level), opacity: opacity(level) }]} />)}<AppText variant="caption">{t('More')}</AppText></View>
        {stats.active === 0 ? <AppText muted>{t('No token usage for this time range')}</AppText> : null}
        {(data?.syntheticCount ?? 0) > 0 ? <AppText variant="caption" muted>{t('{{count}} synthetic usage records included', { count: data?.syntheticCount ?? 0 })}</AppText> : null}
      </>}
    </View>
  </FieldCard>;
}

const styles = StyleSheet.create({
  body: { padding: 16, gap: 12 },
  row: { flexDirection: 'row', alignItems: 'center', gap: 4, marginBottom: 4 },
  weekday: { width: 28 },
  cell: { width: 14, height: 14, borderRadius: 3 },
  legend: { flexDirection: 'row', alignItems: 'center', gap: 4 },
});
