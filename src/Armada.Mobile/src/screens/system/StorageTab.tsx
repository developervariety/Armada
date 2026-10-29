import { useCallback, useEffect, useState } from 'react';
import { View } from 'react-native';
import { getStorage, listAllStorageHosts, refreshStorage } from '@dashboard/api/client';
import { formatStorageBytes } from '@dashboard/lib/storageFormatting';
import type { StorageHostReport, StorageSnapshot } from '@dashboard/types/storage';
import { useAuth } from '../../auth/AuthContext';
import { DetailBody } from '../../components/resource/DetailParts';
import { ResourceRow } from '../../components/resource/ResourceRow';
import { AppText } from '../../components/ui/AppText';
import { Button } from '../../components/ui/Button';
import { TabStrip } from '../../components/ui/TabStrip';
import { useLocale } from '../../i18n/LocaleContext';

/** Cached read-only administrator inventory, shared with the dashboard API and message catalog. */
export function StorageTab() {
  const { isAdmin } = useAuth();
  const { t, locale, formatDateTime } = useLocale();
  const [data, setData] = useState<StorageSnapshot | null>(null);
  // Host reports are not in the snapshot; they are read in explicit bounded pages (100 hosts per page).
  const [hosts, setHosts] = useState<StorageHostReport[]>([]);
  const [error, setError] = useState(false);
  const [busy, setBusy] = useState(false);
  const [group, setGroup] = useState('locations');
  const [page, setPage] = useState(0);
  const read = useCallback(async () => {
    if (!isAdmin) return;
    try {
      const [snapshot, reports] = await Promise.all([getStorage(), listAllStorageHosts()]);
      setData(snapshot); setHosts(reports); setError(false);
    } catch { setError(true); }
  }, [isAdmin]);
  useEffect(() => {
    let active = true;
    if (isAdmin) Promise.all([getStorage(), listAllStorageHosts()]).then(([result, reports]) => { if (active) { setData(result); setHosts(reports); } }).catch(() => { if (active) setError(true); });
    return () => { active = false; };
  }, [isAdmin]);
  useEffect(() => {
    if (!data?.collecting || !isAdmin) return;
    const timer = setInterval(() => { void read(); }, 5000);
    return () => clearInterval(timer);
  }, [data?.collecting, isAdmin, read]);
  const collect = async (exact: boolean) => {
    setBusy(true);
    try { setData(await refreshStorage(exact)); setHosts(await listAllStorageHosts()); setError(false); } catch { setError(true); }
    finally { setBusy(false); }
  };
  if (!isAdmin) return <AppText accessibilityRole="alert">{t('storage.denied')}</AppText>;
  const bytes = (n: number | null | undefined) => formatStorageBytes(n, locale, t('storage.unavailable'));
  const state = (s: string) => t(`storage.${s}`);
  const rows: { title: string; detail: string }[] = [];
  for (const host of hosts) {
    if (group === 'hosts') rows.push({ title: host.name, detail: `${state(host.status)} ${host.stale ? state('Stale') : ''}\n${formatDateTime(host.measuredUtc)}\n${host.warnings.map(state).join('\n')}` });
    if (group === 'volumes') for (const volume of host.volumes) rows.push({ title: `${host.name}: ${volume.path}`, detail: `${t('storage.capacity')}: ${bytes(volume.totalBytes)}\n${t('storage.available')}: ${bytes(volume.availableBytes)}\n${state(volume.status)}` });
    for (const location of host.locations) {
      if (group === 'locations') rows.push({ title: `${host.name}: ${location.path}`, detail: `${t(`storage.category.${location.category}`)}\n${bytes(location.bytes)} · ${state(location.status)} ${host.stale ? state('Stale') : ''}\n${location.warnings.map(state).join('\n')}` });
      if (group === 'files') for (const file of location.largestFiles) rows.push({ title: file.path, detail: `${host.name} · ${bytes(file.bytes)}\n${formatDateTime(file.lastWriteUtc)}` });
    }
  }
  if (group === 'database') for (const table of data?.database.tables ?? []) rows.push({ title: `${table.schema}.${table.name}`, detail: `${t('storage.records')}: ${table.rows == null ? state('Unavailable') : new Intl.NumberFormat(locale).format(table.rows)} (${state(table.countKind)})\n${t('storage.logicalBytes')}: ${bytes(table.logicalBytes)}\n${t('storage.dataBytes')}: ${bytes(table.dataBytes)}\n${t('storage.indexBytes')}: ${bytes(table.indexBytes)}\n${table.warning ? state(table.warning) : ''}` });
  if (group === 'retention') for (const [key, value] of Object.entries(data?.retention ?? {})) rows.push({ title: t(`storage.retention.${key}`), detail: key === 'BuildSlots' ? String(value) : value ? t('storage.days', { count: value }) : t('storage.never') });
  if (group === 'history') for (const sample of data?.history ?? []) rows.push({ title: formatDateTime(sample.measuredUtc), detail: `${t('storage.filesKpi')}: ${bytes(sample.observedFileBytes)}\n${t('storage.databaseKpi')}: ${bytes(sample.databaseBytes)}` });
  const currentPage = Math.min(page, Math.max(0, Math.ceil(rows.length / 10) - 1));
  return <DetailBody embedded onRefresh={() => { void read(); }} testID="storage">
    <AppText variant="heading">{t('storage.title')}</AppText>
    <AppText muted>{t('storage.accounting')}</AppText>
    {error ? <AppText accessibilityRole="alert">{t('storage.error')}</AppText> : null}
    <Button label={t('storage.reload')} onPress={() => { void read(); }} />
    <Button label={t(data?.collecting ? 'storage.collecting' : 'storage.collect')} disabled={busy || data?.collecting} onPress={() => { void collect(false); }} />
    <Button label={t('storage.exact')} disabled={busy || data?.collecting} onPress={() => { void collect(true); }} accessibilityHint={t('storage.exactHelp')} />
    <AppText>{t('storage.databaseKpi')}: {bytes(data?.database.allocatedBytes)}</AppText>
    <TabStrip label={t('storage.sections')} value={group} onChange={key => { setGroup(key); setPage(0); }} options={['locations', 'database', 'hosts', 'volumes', 'files', 'history', 'retention'].map(key => ({ value: key, label: t(`storage.${key}`) }))} />
    {group === 'history' ? <AppText muted>{t('storage.historyHelp', { count: data?.historyCapacity ?? 288 })}</AppText> : null}
    {group === 'volumes' ? <AppText muted>{t('storage.volumeHelp')}</AppText> : null}
    <View>{rows.slice(currentPage * 10, currentPage * 10 + 10).map((row, index) => <ResourceRow key={`${currentPage}-${index}`} title={row.title} subtitle={row.detail} />)}</View>
    {rows.length === 0 ? <AppText muted>{t(data ? 'storage.empty' : 'storage.loading')}</AppText> : null}
    <Button label={t('Previous')} disabled={currentPage === 0} onPress={() => setPage(currentPage - 1)} />
    <Button label={t('Next')} disabled={(currentPage + 1) * 10 >= rows.length} onPress={() => setPage(currentPage + 1)} />
  </DetailBody>;
}
