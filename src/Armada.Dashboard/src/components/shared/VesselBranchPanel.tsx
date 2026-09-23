import { useCallback, useEffect, useState } from 'react';
import { getVesselBranches, mergeVesselBranch, pushVesselBranch } from '../../api/client';
import type { BranchInfo, BranchListResponse, BranchMergeStrategy } from '../../types/models';
import { useLocale } from '../../context/LocaleContext';
import { useLatestRequest } from '../../lib/useLatestRequest';

interface VesselBranchPanelProps {
  vesselId: string;
}

interface WriteDialog {
  kind: 'push' | 'merge';
  source: string;
  target: string;
  strategy: BranchMergeStrategy;
}

export default function VesselBranchPanel({ vesselId }: VesselBranchPanelProps) {
  const { t } = useLocale();
  const [listing, setListing] = useState<BranchListResponse | null>(null);
  const [loadError, setLoadError] = useState('');
  const [dialog, setDialog] = useState<WriteDialog | null>(null);
  const [busy, setBusy] = useState(false);
  const [refusal, setRefusal] = useState('');
  const [notice, setNotice] = useState('');

  // A read superseded by a later one (another vessel or a reread after a write) writes nothing.
  const requests = useLatestRequest();
  const load = useCallback(async () => {
    const request = requests.begin(vesselId);
    try {
      const result = await getVesselBranches(vesselId);
      if (!request.isCurrent()) return;
      setListing(result);
      setLoadError('');
    } catch (err) {
      if (!request.isCurrent()) return;
      setListing(null);
      setLoadError(err instanceof Error ? err.message : String(err));
    }
  }, [requests, vesselId]);

  useEffect(() => { load(); }, [load]);

  const controls = listing?.writeControls;
  const remote = controls?.remote || 'origin';

  function openDialog(kind: WriteDialog['kind'], branch: BranchInfo) {
    setRefusal('');
    setNotice('');
    setDialog({
      kind,
      source: branch.name,
      target: kind === 'merge' ? (listing?.defaultBranch || 'main') : branch.name,
      strategy: 'FastForward',
    });
  }

  async function confirmWrite() {
    if (!dialog) return;
    setBusy(true);
    setRefusal('');
    try {
      const result = dialog.kind === 'push'
        ? await pushVesselBranch(vesselId, { sourceRef: dialog.source, targetRef: dialog.target, remote })
        : await mergeVesselBranch(vesselId, { sourceRef: dialog.source, targetRef: dialog.target, strategy: dialog.strategy });
      setDialog(null);
      setNotice(result.message);
      await load();
    } catch (err) {
      setRefusal(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }

  const summary = dialog
    ? dialog.kind === 'push'
      ? t('Push {{source}} to {{remote}}/{{target}}. This is not a force push; the remote refuses an update that would rewrite its history.', { source: dialog.source, remote, target: dialog.target })
      : t('Merge {{source}} into {{target}} in the landing repository using {{strategy}}. Nothing is pushed. The working checkout is fast-forwarded only when it is on {{target}}.', { source: dialog.source, target: dialog.target, strategy: dialog.strategy })
    : '';

  return (
    <div className="detail-context-section">
      <h3>{t('Branches')}</h3>
      {loadError && <p className="text-dim" role="alert">{loadError}</p>}
      {!listing && !loadError && <p className="text-dim">{t('Loading branches...')}</p>}
      {listing?.error && <p className="text-dim" role="alert">{listing.error}</p>}
      {notice && <p role="status">{notice}</p>}
      {controls && !controls.pushAvailable && !controls.mergeAvailable && (
        <p className="text-dim">{t('Branch write controls are unavailable: {{reason}}', { reason: controls.pushUnavailableReason || controls.mergeUnavailableReason || 'unavailable' })}</p>
      )}
      {listing && listing.branches.length > 0 && (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>{t('Branch')}</th>
                <th>{t('Tip')}</th>
                <th>{t('Ahead / Behind')}</th>
                {(controls?.pushAvailable || controls?.mergeAvailable) && <th>{t('Actions')}</th>}
              </tr>
            </thead>
            <tbody>
              {listing.branches.map(branch => (
                <tr key={branch.name}>
                  <td className="mono">{branch.name}{branch.isDefault ? ` (${t('default')})` : ''}</td>
                  <td className="mono" title={branch.commitSubject ?? ''}>{branch.commitHash || '-'}</td>
                  <td>{branch.divergenceError ? '-' : `${branch.ahead} / ${branch.behind}`}</td>
                  {(controls?.pushAvailable || controls?.mergeAvailable) && (
                    <td>
                      {controls?.pushAvailable && (
                        <button type="button" className="btn btn-sm" onClick={() => openDialog('push', branch)}>{t('Push')}</button>
                      )}
                      {controls?.mergeAvailable && !branch.isDefault && (
                        <button type="button" className="btn btn-sm" onClick={() => openDialog('merge', branch)}>{t('Merge')}</button>
                      )}
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {dialog && (
        <div className="modal-overlay" style={{ zIndex: 1500 }} onClick={() => { if (!busy) setDialog(null); }}>
          <div className="modal-box" role="dialog" aria-label={dialog.kind === 'push' ? t('Confirm push') : t('Confirm merge')} onClick={e => e.stopPropagation()}>
            <h3 style={{ marginTop: 0 }}>{dialog.kind === 'push' ? t('Confirm push') : t('Confirm merge')}</h3>
            <label>{t('Source')}<input value={dialog.source} readOnly /></label>
            <label>{t('Target')}
              <input aria-label={t('Target')} value={dialog.target} disabled={busy} onChange={e => setDialog({ ...dialog, target: e.target.value })} />
            </label>
            {dialog.kind === 'push'
              ? <label>{t('Remote')}<input value={remote} readOnly /></label>
              : (
                <label>{t('Strategy')}
                  <select aria-label={t('Strategy')} value={dialog.strategy} disabled={busy} onChange={e => setDialog({ ...dialog, strategy: e.target.value as BranchMergeStrategy })}>
                    <option value="FastForward">{t('Fast-forward only')}</option>
                    <option value="MergeCommit">{t('Merge commit')}</option>
                  </select>
                </label>
              )}
            <p data-testid="branch-write-summary">{summary}</p>
            {refusal && <p role="alert">{refusal}</p>}
            <div className="modal-actions">
              <button type="button" className="btn" disabled={busy} onClick={() => setDialog(null)}>{t('Cancel')}</button>
              <button type="button" className="btn btn-primary" disabled={busy || dialog.target.trim().length === 0} onClick={confirmWrite}>
                {busy
                  ? (dialog.kind === 'push' ? t('Pushing...') : t('Merging...'))
                  : (dialog.kind === 'push' ? t('Confirm push') : t('Confirm merge'))}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
