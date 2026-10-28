import { useCallback, useEffect, useRef, useState } from 'react';
import { getStorage, refreshStorage } from '../api/client';
import type { StorageHistoryPoint, StorageLocation, StorageSnapshot } from '../types/models';
import { useLocale } from '../context/LocaleContext';
import StatusBadge from '../components/shared/StatusBadge';
import AutoRefreshSelect from '../components/shared/AutoRefreshSelect';
import { useAutoRefresh } from '../lib/useAutoRefresh';
import { formatBytes } from '../lib/format';

/** Milliseconds between cache reads while a collection runs. */
const DEFAULT_POLL_MS = 1500;
const LARGEST_FILES_SHOWN = 10;
const HISTORY_ROWS_SHOWN = 10;

/** Explanation of each warning code the server can name. */
const WARNING_TEXT: Record<string, string> = {
  EntryLimit: 'The scan stopped at its entry limit, so sizes are a lower bound.',
  DepthLimit: 'Directories below the depth limit were not entered, so sizes are a lower bound.',
  TimeLimit: 'The scan stopped at its time limit, so sizes are a lower bound.',
  NotReached: 'An earlier limit ended the scan before this location was measured.',
  AccessDenied: 'The server may not read some entries, so sizes are a lower bound.',
  Unreadable: 'Some entries could not be read, so sizes are a lower bound.',
  InvalidPath: 'A configured path is not valid and was skipped.',
  DatabaseUnavailable: 'The database statistics could not be read.',
  DatabaseTimedOut: 'The database statistics query timed out.',
  AllocationUnavailable: 'The database allocation could not be read.',
  TableSizesUnavailable: 'This database provider reports row counts only, not per-table sizes.',
  TableLimit: 'The database has more tables than the inventory lists.',
  ExactCountUnavailable: 'An exact row count failed for a table.',
  HistoryUnreadable: 'The stored growth history could not be read and restarted empty.',
  HistoryNotSaved: 'The growth history could not be saved.',
  CollectionTimedOut: 'The last collection timed out.',
  CollectionFailed: 'The last collection failed. The previous figures are shown; the Admiral log has the cause.',
};

function categoryKey(category: string): string {
  return category.length === 0 ? category : category.charAt(0).toLowerCase() + category.slice(1);
}

function GrowthChart({ points, label }: { points: StorageHistoryPoint[]; label: string }) {
  if (points.length < 2) return null;
  const width = 480;
  const height = 80;
  const values = points.map((p) => p.observedFileBytes);
  const min = Math.min(...values);
  const max = Math.max(...values);
  const span = max - min || 1;
  const coordinates = points
    .map((p, index) => {
      const x = (index / (points.length - 1)) * width;
      const y = height - ((p.observedFileBytes - min) / span) * (height - 8) - 4;
      return `${x.toFixed(1)},${y.toFixed(1)}`;
    })
    .join(' ');
  return (
    <svg viewBox={`0 0 ${width} ${height}`} width="100%" height={height} role="img" aria-label={label} preserveAspectRatio="none">
      <polyline points={coordinates} fill="none" stroke="currentColor" strokeWidth="2" />
    </svg>
  );
}

export default function Storage({ pollIntervalMs = DEFAULT_POLL_MS }: { pollIntervalMs?: number }) {
  const { t, formatDateTime, formatRelativeTime } = useLocale();
  const [snapshot, setSnapshot] = useState<StorageSnapshot | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [refused, setRefused] = useState(false);
  const [requesting, setRequesting] = useState(false);
  // The routes need a global administrator. Once refused, the page stops requesting them instead of polling into 403s.
  const refusedRef = useRef(false);

  const handleError = useCallback((err: unknown, message: string) => {
    if ((err as { status?: number } | null)?.status === 403) {
      refusedRef.current = true;
      setRefused(true);
      setError('');
    } else {
      setError(message);
    }
  }, []);

  const load = useCallback(async () => {
    if (refusedRef.current) return;
    try {
      setSnapshot(await getStorage());
      setError('');
    } catch (err) {
      handleError(err, t('Failed to load the storage inventory.'));
    } finally {
      setLoading(false);
    }
  }, [handleError, t]);

  useEffect(() => { load(); }, [load]);

  const { seconds: refreshSeconds, setSeconds: setRefreshSeconds } = useAutoRefresh('storage', load, 60);

  const collecting = snapshot?.collecting ?? false;
  useEffect(() => {
    if (!collecting) return undefined;
    const timer = setTimeout(load, pollIntervalMs);
    return () => clearTimeout(timer);
  }, [collecting, snapshot, load, pollIntervalMs]);

  async function requestRefresh() {
    if (requesting) return;
    setRequesting(true);
    try {
      setSnapshot(await refreshStorage(false));
      setError('');
    } catch (err) {
      handleError(err, t('Failed to start a storage refresh.'));
    } finally {
      setRequesting(false);
    }
  }

  function time(value: string | null | undefined) {
    if (!value) return <span className="text-dim">-</span>;
    return <span className="text-dim" title={formatDateTime(value)}>{formatRelativeTime(value)}</span>;
  }

  function warningText(code: string): string {
    return t(WARNING_TEXT[code] ?? code);
  }

  const files = snapshot?.files ?? null;
  const locations: StorageLocation[] = files?.locations ?? [];
  const observedBytes = locations.reduce((sum, location) => sum + (location.bytes ?? 0), 0);
  const largest = locations
    .flatMap((location) => location.largestFiles.map((file) => ({ ...file, category: location.category })))
    .sort((a, b) => b.bytes - a.bytes)
    .slice(0, LARGEST_FILES_SHOWN);
  const reasons = Array.from(new Set([...(files?.partialReasons ?? []), ...(snapshot?.warnings ?? [])]));
  const history = snapshot?.history ?? [];
  const recent = history.slice(-HISTORY_ROWS_SHOWN).reverse();
  const first = history[0];
  const last = history[history.length - 1];
  const growth = first && last && first !== last ? last.observedFileBytes - first.observedFileBytes : null;

  return (
    <div className="storage-page">
      <div className="view-header">
        <div>
          <h2>{t('Storage')}</h2>
          <p className="text-dim view-subtitle">
            {t("Where Armada's data directory and database use disk space. The inventory is read-only: it measures and never deletes.")}
          </p>
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: '0.5rem' }}>
          <AutoRefreshSelect seconds={refreshSeconds} onChange={setRefreshSeconds} />
          <button className="btn btn-sm" onClick={requestRefresh} disabled={refused || requesting || collecting} title={t('Measure storage now')}>
            {collecting ? t('Measuring...') : t('Refresh inventory')}
          </button>
        </div>
      </div>

      {error && <div className="alert alert-error">{error}</div>}
      {snapshot?.lastError && <div className="alert alert-warning" role="status">{warningText(snapshot.lastError)}</div>}

      {refused ? (
        <div className="card" style={{ padding: '1.25rem' }}>
          <p className="text-muted">{t('The storage inventory is available to global administrators.')}</p>
        </div>
      ) : loading && !snapshot ? (
        <p className="text-dim">{t('Loading...')}</p>
      ) : !files ? (
        <div className="card" style={{ padding: '1.25rem' }}>
          <p className="text-muted">{collecting ? t('The first measurement is running.') : t('No measurement yet. Use Refresh inventory to measure storage.')}</p>
        </div>
      ) : (
        <>
          <div className="cards" style={{ marginBottom: '1rem' }}>
            <div className="card" style={{ padding: '0.75rem 1rem' }}>
              <div className="text-dim">{t('Files')}</div>
              <strong>{formatBytes(observedBytes)}</strong>
            </div>
            <div className="card" style={{ padding: '0.75rem 1rem' }}>
              <div className="text-dim">{t('Database')}</div>
              <strong>{snapshot?.database.allocatedBytes != null ? formatBytes(snapshot.database.allocatedBytes) : '-'}</strong>
            </div>
            <div className="card" style={{ padding: '0.75rem 1rem' }}>
              <div className="text-dim">{t('Measured')}</div>
              <strong>{time(snapshot?.measuredUtc)}</strong>
            </div>
            <div className="card" style={{ padding: '0.75rem 1rem' }}>
              <div className="text-dim">{t('Refresh interval')}</div>
              <strong>{snapshot && snapshot.refreshIntervalSeconds > 0 ? `${snapshot.refreshIntervalSeconds} s` : t('Manual only')}</strong>
            </div>
          </div>

          {reasons.length > 0 && (
            <div className="alert alert-warning" role="status">
              <strong>{t('Some figures are partial.')}</strong>
              <ul style={{ margin: '0.25rem 0 0 1rem' }}>
                {reasons.map((code) => <li key={code}>{warningText(code)}</li>)}
              </ul>
            </div>
          )}

          <h3>{t('Data directory')}</h3>
          <div className="card" style={{ overflowX: 'auto', marginBottom: '1rem' }}>
            <table className="data-table">
              <thead>
                <tr>
                  <th>{t('Category')}</th>
                  <th>{t('Path')}</th>
                  <th>{t('Size')}</th>
                  <th>{t('Files')}</th>
                  <th>{t('Oldest write')}</th>
                  <th>{t('Newest write')}</th>
                  <th>{t('Status')}</th>
                </tr>
              </thead>
              <tbody>
                {locations.map((location) => (
                  <tr key={`${location.category}:${location.path}`}>
                    <td>{location.category}</td>
                    <td className="mono">{location.path}</td>
                    <td>{location.bytes != null ? formatBytes(location.bytes) : '-'}</td>
                    <td>{location.fileCount}</td>
                    <td>{time(location.oldestWriteUtc)}</td>
                    <td>{time(location.newestWriteUtc)}</td>
                    <td>
                      <StatusBadge status={location.status} />
                      {location.warnings.map((code) => (
                        <div key={code} className="text-dim" style={{ fontSize: '0.72rem' }}>{code}</div>
                      ))}
                      {location.linksSkipped > 0 && (
                        <div className="text-dim" style={{ fontSize: '0.72rem' }}>{t('{{count}} links not followed', { count: location.linksSkipped })}</div>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {files.volumes.length > 0 && (
            <>
              <h3>{t('Volumes')}</h3>
              <div className="card" style={{ overflowX: 'auto', marginBottom: '1rem' }}>
                <table className="data-table">
                  <thead>
                    <tr><th>{t('Volume')}</th><th>{t('Available')}</th><th>{t('Total')}</th></tr>
                  </thead>
                  <tbody>
                    {files.volumes.map((volume) => (
                      <tr key={volume.path}>
                        <td className="mono">{volume.path}</td>
                        <td>{volume.availableBytes != null ? formatBytes(volume.availableBytes) : '-'}</td>
                        <td>{volume.totalBytes != null ? formatBytes(volume.totalBytes) : '-'}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </>
          )}

          {largest.length > 0 && (
            <>
              <h3>{t('Largest files')}</h3>
              <div className="card" style={{ overflowX: 'auto', marginBottom: '1rem' }}>
                <table className="data-table">
                  <thead>
                    <tr><th>{t('File')}</th><th>{t('Category')}</th><th>{t('Size')}</th><th>{t('Last write')}</th></tr>
                  </thead>
                  <tbody>
                    {largest.map((file) => (
                      <tr key={file.path}>
                        <td className="mono">{file.path}</td>
                        <td>{file.category}</td>
                        <td>{formatBytes(file.bytes)}</td>
                        <td>{time(file.lastWriteUtc)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </>
          )}

          <h3>{t('Database')}</h3>
          <p className="text-dim">
            {snapshot?.database.provider} - {snapshot?.database.unusedBytes != null
              ? t('{{size}} reusable', { size: formatBytes(snapshot.database.unusedBytes) })
              : t('Reusable space not reported')}
          </p>
          {snapshot?.database.warnings.map((code) => (
            <p key={code} className="text-dim" style={{ fontSize: '0.8rem', margin: '0 0 0.5rem' }}>{warningText(code)}</p>
          ))}
          {snapshot && snapshot.database.tables.length > 0 ? (
            <div className="card" style={{ overflowX: 'auto', marginBottom: '1rem' }}>
              <table className="data-table">
                <thead>
                  <tr><th>{t('Table')}</th><th>{t('Rows')}</th><th>{t('Data')}</th><th>{t('Indexes')}</th></tr>
                </thead>
                <tbody>
                  {snapshot.database.tables.map((table) => (
                    <tr key={`${table.schema}.${table.name}`}>
                      <td className="mono">{table.name}</td>
                      <td>
                        {table.rows != null ? table.rows.toLocaleString() : '-'}
                        {table.countKind === 'Estimated' && <span className="text-dim"> ({t('estimated')})</span>}
                      </td>
                      <td>{table.dataBytes != null ? formatBytes(table.dataBytes) : '-'}</td>
                      <td>{table.indexBytes != null ? formatBytes(table.indexBytes) : '-'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : (
            <p className="text-muted">{t('No database table statistics.')}</p>
          )}

          <h3>{t('Growth')}</h3>
          {history.length < 2 ? (
            <p className="text-muted">{t('Growth appears after two measurements.')}</p>
          ) : (
            <div className="card" style={{ padding: '0.75rem 1rem', marginBottom: '1rem' }}>
              <GrowthChart points={history} label={t('File bytes over time')} />
              <p className="text-dim" style={{ margin: '0.5rem 0' }}>
                {growth != null && growth >= 0 ? '+' : '-'}{formatBytes(Math.abs(growth ?? 0))} {t('across {{count}} measurements', { count: history.length })}
              </p>
              <table className="data-table">
                <thead>
                  <tr><th>{t('Measured')}</th><th>{t('Files')}</th><th>{t('Database')}</th><th>{t('Docks')}</th><th>{t('Logs')}</th></tr>
                </thead>
                <tbody>
                  {recent.map((point) => (
                    <tr key={point.measuredUtc}>
                      <td>{time(point.measuredUtc)}</td>
                      <td>{formatBytes(point.observedFileBytes)}</td>
                      <td>{point.databaseBytes != null ? formatBytes(point.databaseBytes) : '-'}</td>
                      <td>{formatBytes(point.categories[categoryKey('Docks')] ?? 0)}</td>
                      <td>{formatBytes((point.categories[categoryKey('Logs')] ?? 0) + (point.categories[categoryKey('MissionLogs')] ?? 0))}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}
    </div>
  );
}
