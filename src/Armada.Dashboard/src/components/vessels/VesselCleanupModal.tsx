import { useCallback, useEffect, useId, useMemo, useState } from 'react';
import {
  getVesselCleanupInventory,
  runVesselCleanup,
  type VesselCleanupDisposition,
  type VesselCleanupInventory,
  type VesselCleanupItemResult,
  type VesselCleanupResource,
} from '../../api/client';
import { useLocale } from '../../context/LocaleContext';
import { formatBytes } from '../../lib/format';
import ConfirmDialog from '../shared/ConfirmDialog';
import LoadingIndicator from '../shared/LoadingIndicator';

interface VesselCleanupModalProps {
  vesselId: string;
  vesselName: string;
  open: boolean;
  onClose: () => void;
}

const GROUPS: { disposition: VesselCleanupDisposition; title: string; hint: string }[] = [
  {
    disposition: 'Safe',
    title: 'Safe to clean up',
    hint: 'Evidence shows nothing needs these resources. Select the ones to remove.',
  },
  {
    disposition: 'InUse',
    title: 'Still in use',
    hint: 'Running, queued, review, landing, retry, captain, or merge-queue work still references these. They cannot be cleaned up.',
  },
  {
    disposition: 'Unknown',
    title: 'Unknown disposition',
    hint: 'These could not be proven safe (uncommitted or unmerged work, shared paths, unreachable Harbors, or unclear ownership). They cannot be cleaned up.',
  },
];

/**
 * Vessel cleanup: opens with a read-only inventory of the vessel's docks, worktrees, and Armada-managed branches,
 * grouped as safe, in use, or unknown with the reason for each. Only safe resources can be selected; cleanup asks for
 * confirmation, the server revalidates every item before removing it, and the per-item outcomes are shown with the
 * refreshed inventory.
 */
export default function VesselCleanupModal({ vesselId, vesselName, open, onClose }: VesselCleanupModalProps) {
  const { t } = useLocale();
  const titleId = useId();
  const [loading, setLoading] = useState(false);
  const [running, setRunning] = useState(false);
  const [error, setError] = useState('');
  const [inventory, setInventory] = useState<VesselCleanupInventory | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [results, setResults] = useState<VesselCleanupItemResult[] | null>(null);

  const scan = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      setInventory(await getVesselCleanupInventory(vesselId));
      setSelected(new Set());
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  }, [vesselId]);

  useEffect(() => {
    if (!open) return;
    setInventory(null);
    setResults(null);
    void scan();
  }, [open, scan]);

  const grouped = useMemo(() => {
    const map: Record<VesselCleanupDisposition, VesselCleanupResource[]> = { Safe: [], InUse: [], Unknown: [] };
    for (const r of inventory?.resources ?? []) (map[r.disposition] ?? map.Unknown).push(r);
    return map;
  }, [inventory]);

  if (!open) return null;

  const safeKeys = grouped.Safe.map(r => r.key);
  const allSafeSelected = safeKeys.length > 0 && safeKeys.every(k => selected.has(k));
  const selectedBytes = grouped.Safe.filter(r => selected.has(r.key)).reduce((sum, r) => sum + (r.sizeBytes ?? 0), 0);

  const toggle = (key: string) => {
    setSelected(prev => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key); else next.add(key);
      return next;
    });
  };

  const toggleAllSafe = () => setSelected(allSafeSelected ? new Set() : new Set(safeKeys));

  const runCleanup = async () => {
    setConfirmOpen(false);
    setRunning(true);
    setError('');
    try {
      const result = await runVesselCleanup(vesselId, [...selected]);
      setResults(result.results);
      setInventory(result.inventory);
      setSelected(new Set());
    } catch (e) {
      setError(t('Cleanup failed: {{message}}', { message: e instanceof Error ? e.message : String(e) }));
    } finally {
      setRunning(false);
    }
  };

  const typeLabel = (r: VesselCleanupResource) => t(r.resourceType);

  const owner = (r: VesselCleanupResource) => {
    const parts: string[] = [];
    if (r.voyageId) parts.push(`${t('Voyage')} ${r.voyageId}`);
    if (r.missionId) parts.push(`${t('Mission')} ${r.missionId}${r.missionStatus ? ` (${r.missionStatus})` : ''}`);
    if (r.dockId && r.resourceType !== 'Dock') parts.push(`${t('Dock')} ${r.dockId}`);
    if (r.captainId) parts.push(`${t('Captain')} ${r.captainId}`);
    parts.push(r.harborId ? `${t('Harbor')} ${r.harborId}` : t('Admiral host'));
    return parts;
  };

  const resultsSummary = results
    ? t('{{deleted}} removed, {{skipped}} skipped, {{failed}} failed.', {
      deleted: String(results.filter(r => r.status === 'Deleted').length),
      skipped: String(results.filter(r => r.status === 'Skipped').length),
      failed: String(results.filter(r => r.status === 'Failed').length),
    })
    : '';

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div
        className="modal vessel-cleanup-modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        style={{ width: 'min(1100px, 96vw)', maxWidth: 'min(1100px, 96vw)', maxHeight: '92vh', overflowY: 'auto' }}
        onClick={e => e.stopPropagation()}
      >
        <h3 id={titleId} style={{ marginTop: 0 }}>{t('Cleanup: {{name}}', { name: vesselName })}</h3>
        <p className="text-dim" style={{ marginTop: 0 }}>
          {t('Opening this window only inspects the vessel; nothing is deleted until you select safe resources and confirm. Remote branches, default branches, and the primary checkout are never removed.')}
        </p>

        {error && (
          <div role="alert" className="vessel-cleanup-error" style={{ color: 'var(--red)', marginBottom: '0.75rem' }}>
            {error}{' '}
            <button type="button" className="btn btn-sm" onClick={() => void scan()} disabled={loading || running}>{t('Retry')}</button>
          </div>
        )}

        {results && (
          <div className="vessel-cleanup-results" aria-label={t('Cleanup results')} style={{ marginBottom: '1rem' }}>
            <h4 style={{ margin: '0 0 0.4rem' }}>{t('Cleanup results')}</h4>
            <p className="text-dim" style={{ margin: '0 0 0.4rem' }}>{resultsSummary}</p>
            <ul style={{ margin: 0, paddingLeft: '1.2rem' }}>
              {results.map(r => (
                <li key={r.key} data-status={r.status}>
                  <strong style={{ color: r.status === 'Failed' ? 'var(--red)' : r.status === 'Deleted' ? 'var(--green)' : undefined }}>{t(r.status)}</strong>
                  {' '}<span className="mono">{r.identity ?? r.key}</span>: {r.message}
                </li>
              ))}
            </ul>
          </div>
        )}

        {(loading || running) && <LoadingIndicator label={running ? t('Cleaning up selected resources...') : t('Scanning vessel resources...')} />}

        {!loading && inventory && (
          <>
            <p className="vessel-cleanup-summary" style={{ margin: '0 0 0.75rem' }}>
              {t('{{safe}} safe, {{inUse}} in use, {{unknown}} unknown. Safe resources use {{size}} measured on disk.', {
                safe: String(inventory.safeCount),
                inUse: String(inventory.inUseCount),
                unknown: String(inventory.unknownCount),
                size: formatBytes(inventory.safeMeasuredBytes),
              })}
              {inventory.safeUnmeasuredCount > 0 && ` ${t('{{count}} safe resources could not be measured.', { count: String(inventory.safeUnmeasuredCount) })}`}
            </p>

            {inventory.resources.length === 0 && (
              <p className="text-dim vessel-cleanup-empty">{t('Nothing to clean up: this vessel has no docks, orphaned worktrees, or Armada branches.')}</p>
            )}

            {inventory.resources.length > 0 && GROUPS.map(group => {
              const rows = grouped[group.disposition];
              const selectable = group.disposition === 'Safe';
              return (
                <section key={group.disposition} className="vessel-cleanup-group" data-disposition={group.disposition} aria-label={t(group.title)} style={{ marginBottom: '1rem' }}>
                  <h4 style={{ margin: '0 0 0.25rem' }}>{t(group.title)} ({rows.length})</h4>
                  <p className="text-dim" style={{ margin: '0 0 0.4rem', fontSize: '0.85rem' }}>{t(group.hint)}</p>
                  {rows.length === 0 ? (
                    <p className="text-dim" style={{ margin: 0 }}>{t('None.')}</p>
                  ) : (
                    <div className="table-wrap"><table className="table-dense">
                      <thead>
                        <tr>
                          <th style={{ width: '2rem' }}>
                            {selectable && (
                              <input type="checkbox" aria-label={t('Select all safe resources')} checked={allSafeSelected} onChange={toggleAllSafe} disabled={running} />
                            )}
                          </th>
                          <th>{t('Type')}</th>
                          <th>{t('Resource')}</th>
                          <th>{t('Owner')}</th>
                          <th>{t('Reason')}</th>
                          <th className="text-right">{t('Size')}</th>
                        </tr>
                      </thead>
                      <tbody>
                        {rows.map(r => (
                          <tr key={r.key}>
                            <td>
                              <input
                                type="checkbox"
                                aria-label={t('Select {{name}}', { name: r.identity })}
                                checked={selectable && selected.has(r.key)}
                                disabled={!selectable || running}
                                title={selectable ? undefined : t('Only safe resources can be cleaned up.')}
                                onChange={() => toggle(r.key)}
                              />
                            </td>
                            <td>{typeLabel(r)}</td>
                            <td>
                              <div className="mono">{r.identity}</div>
                              {r.path && r.path !== r.identity && <div className="mono text-dim" style={{ fontSize: '0.8rem' }}>{r.path}</div>}
                              {r.branchName && r.branchName !== r.identity && <div className="mono text-dim" style={{ fontSize: '0.8rem' }}>{r.branchName}</div>}
                            </td>
                            <td className="text-dim" style={{ fontSize: '0.85rem' }}>{owner(r).map(p => <div key={p}>{p}</div>)}</td>
                            <td style={{ fontSize: '0.85rem' }}>{r.reason}</td>
                            <td className="text-right cell-nowrap">{r.sizeBytes == null ? <span className="text-dim" title={t('Size could not be measured.')}>-</span> : formatBytes(r.sizeBytes)}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table></div>
                  )}
                </section>
              );
            })}
          </>
        )}

        <div className="modal-actions" style={{ marginTop: '1rem' }}>
          <button type="button" className="btn" onClick={() => void scan()} disabled={loading || running} title={t('Inspect the vessel again. Read-only.')}>{t('Rescan')}</button>
          <button
            type="button"
            className="btn btn-danger"
            onClick={() => setConfirmOpen(true)}
            disabled={selected.size === 0 || loading || running}
            title={t('Remove the selected safe resources after you confirm. Each one is checked again just before removal.')}
          >
            {t('Clean up selected ({{count}})', { count: String(selected.size) })}
          </button>
          <button type="button" className="btn" onClick={onClose} disabled={running}>{t('Close')}</button>
        </div>

        <ConfirmDialog
          open={confirmOpen}
          title="Clean up vessel resources"
          confirmLabel="Clean up"
          danger
          message={t('Remove {{count}} selected resources ({{size}} measured) from {{name}}? Each one is checked again just before removal and skipped if it is no longer safe. This cannot be undone.', {
            count: String(selected.size),
            size: formatBytes(selectedBytes),
            name: vesselName,
          })}
          onConfirm={() => void runCleanup()}
          onCancel={() => setConfirmOpen(false)}
        />
      </div>
    </div>
  );
}
