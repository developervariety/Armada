import { useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { getVesselLocalCheckouts, importVesselCheckout } from '../../api/client';
import { useAuth } from '../../context/AuthContext';
import { useLocale } from '../../context/LocaleContext';
import type { Fleet, LocalCheckoutDiscovery, Vessel } from '../../types/models';

interface CheckoutImportProps {
  fleets: Fleet[];
  onImported: (vessel: Vessel) => void;
}

export default function CheckoutImport({ fleets, onImported }: CheckoutImportProps) {
  const { isAdmin } = useAuth();
  const { t } = useLocale();
  const [open, setOpen] = useState(false);
  const [discovery, setDiscovery] = useState<LocalCheckoutDiscovery | null>(null);
  const [selectedPath, setSelectedPath] = useState('');
  const [name, setName] = useState('');
  const [fleetId, setFleetId] = useState('');
  const [loading, setLoading] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const requestId = useRef(0);
  const mounted = useRef(true);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; requestId.current++; }; }, []);
  const selected = discovery?.candidates.find((candidate) => candidate.checkoutPath === selectedPath) ?? null;
  const rootsText = discovery ? t('Search roots: {{roots}}', { roots: discovery.roots.join(', ') || t('none') }) : '';
  const rootErrors = discovery?.errors.map((message, index) => <p className="alert alert-warning" key={`${index}-${message}`}>{t('Some checkout roots could not be scanned: {{message}}', { message })}</p>);

  async function refresh() {
    const current = ++requestId.current;
    setLoading(true); setError('');
    try {
      const result = await getVesselLocalCheckouts();
      if (mounted.current && current === requestId.current) setDiscovery(result);
    } catch (err: unknown) {
      if (mounted.current && current === requestId.current) setError(err instanceof Error ? err.message : t('Checkout discovery failed.'));
    } finally {
      if (mounted.current && current === requestId.current) setLoading(false);
    }
  }

  function show() { setOpen(true); setDiscovery(null); setSelectedPath(''); setName(''); setFleetId(''); void refresh(); }
  function close() { requestId.current++; setOpen(false); }
  function choose(path: string) {
    setSelectedPath(path);
    const candidate = discovery?.candidates.find((item) => item.checkoutPath === path);
    setName(candidate?.suggestedName ?? '');
  }
  async function submit() {
    if (!selected || selected.existingVesselId || !name.trim() || busy) return;
    setBusy(true); setError('');
    try {
      const vessel = await importVesselCheckout({ checkoutPath: selected.checkoutPath, name: name.trim(), ...(fleetId ? { fleetId } : {}) });
      if (mounted.current) { close(); onImported(vessel); }
    } catch (err: unknown) {
      if (mounted.current) setError(err instanceof Error ? err.message : t('Checkout import failed.'));
    } finally {
      if (mounted.current) setBusy(false);
    }
  }

  if (!isAdmin) return null;
  return <>
    <button className="btn btn-sm" onClick={show}>{t('Import checkout')}</button>
    {open && <div className="modal-overlay" onClick={busy ? undefined : close}>
      <div className="modal modal-lg" role="dialog" aria-modal="true" aria-labelledby="checkout-import-title" onClick={(event) => event.stopPropagation()}>
        <h3 id="checkout-import-title">{t('Import local checkout')}</h3>
        <p className="text-dim">{t('Register an existing server checkout with Armada. Import does not change its files. Deleting the vessel later will not delete the checkout.')}</p>
        <button className="btn btn-sm" onClick={() => void refresh()} disabled={loading || busy}>{loading ? t('Scanning...') : t('Refresh checkouts')}</button>
        {loading && <p role="status">{t('Scanning configured checkout roots...')}</p>}
        {error && <p className="alert alert-error" role="alert">{error}</p>}
        {discovery && <>
          {!discovery.configured && <p role="status">{t('Checkout roots are not configured on the server.')}</p>}
          {discovery.configured && <p className="text-dim">{rootsText}</p>}
          {rootErrors}
          {discovery.truncated && <p className="alert alert-warning">{t('The checkout list is incomplete. Refresh after narrowing the configured roots.')}</p>}
          {discovery.configured && !discovery.candidates.length && <p role="status">{discovery.errors.length ? t('No checkouts were found in the roots that could be scanned.') : t('No local checkouts were found.')}</p>}
          {!!discovery.candidates.length && <label>{t('Checkout')}
            <select disabled={busy} value={selectedPath} onChange={(event) => choose(event.target.value)}><option value="">{t('Select a checkout...')}</option>
              {discovery.candidates.map((candidate) => <option key={candidate.checkoutPath} value={candidate.checkoutPath}>{candidate.suggestedName} — {candidate.checkoutPath}</option>)}
            </select>
          </label>}
        </>}
        {selected && <>
          <p><strong>{t('Path')}:</strong> <code>{selected.checkoutPath}</code></p>
          {selected.originUrl && <p><strong>{t('Origin')}:</strong> {selected.originUrl}</p>}
          <p><strong>{t('Default branch')}:</strong> {selected.defaultBranch}</p>
          <label>{t('Vessel name')}<input disabled={busy} value={name} onChange={(event) => setName(event.target.value)} /></label>
          <label>{t('Fleet (optional)')}<select disabled={busy} value={fleetId} onChange={(event) => setFleetId(event.target.value)}><option value="">{t('No fleet')}</option>{fleets.map((fleet) => <option key={fleet.id} value={fleet.id}>{fleet.name}</option>)}</select></label>
          {selected.existingVesselId && <p role="status">{t('This checkout is already registered.')}{' '}<Link to={`/vessels/${selected.existingVesselId}`}>{t('Open vessel')}</Link></p>}
        </>}
        <div className="modal-actions">
          <button className="btn" onClick={close} disabled={busy}>{t('Cancel')}</button>
          <button className="btn btn-primary" onClick={() => void submit()} disabled={busy || loading || !selected || !!selected.existingVesselId || !name.trim()}>{busy ? t('Importing...') : t('Import checkout')}</button>
        </div>
      </div>
    </div>}
  </>;
}
