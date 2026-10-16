import { useState } from 'react';
import type { VesselReadinessResult } from '../../types/models';
import { readinessCheckout, readinessCheckoutText, readinessLabel, readinessTone } from '../../lib/readiness';
import { useLocale } from '../../context/LocaleContext';
import CopyButton from '../shared/CopyButton';
import ReadinessPanel from '../shared/ReadinessPanel';

const EXPANDED_KEY = 'armada.workspace.readinessExpanded';

function readExpanded(): boolean | null {
  try {
    const value = localStorage.getItem(EXPANDED_KEY);
    return value === null ? null : value === 'true';
  } catch {
    return null;
  }
}

function writeExpanded(value: boolean) {
  try {
    localStorage.setItem(EXPANDED_KEY, String(value));
  } catch {
    // Storage unavailable: the choice lasts for this page only.
  }
}

interface WorkspaceReadinessStripProps {
  readiness: VesselReadinessResult | null;
  loading: boolean;
}

/**
 * One-line readiness summary above the Workspace editor: status, resolved profile, check types, and where the checkout
 * is. Details (anything unavailable, and the readiness issues) open below it on demand; they start open only when the
 * vessel is blocked, unless the user chose otherwise before.
 */
export default function WorkspaceReadinessStrip({ readiness, loading }: WorkspaceReadinessStripProps) {
  const { t } = useLocale();
  const [stored, setStored] = useState<boolean | null>(() => readExpanded());
  const tone = readinessTone(readiness);
  const checkout = readinessCheckout(readiness);
  const checkoutText = checkout ? readinessCheckoutText(checkout) : null;
  const attention = readiness ? readiness.errorCount + readiness.warningCount : 0;
  const expanded = stored ?? (readiness ? readiness.errorCount > 0 : false);

  function toggle() {
    setStored(!expanded);
    writeExpanded(!expanded);
  }

  return (
    <div className="card workspace-readiness" data-testid="workspace-readiness">
      <div className="workspace-readiness-row">
        <button
          type="button"
          className="workspace-readiness-toggle"
          onClick={toggle}
          aria-expanded={expanded}
          title={expanded ? t('Hide the readiness details.') : t('Show the readiness details: anything unavailable, and the readiness notes and issues.')}
        >
          <span className="workspace-readiness-chevron" aria-hidden="true">
            <svg viewBox="0 0 16 16"><path d="M6 4l4 4-4 4" /></svg>
          </span>
          {t('Readiness')}
        </button>
        {loading ? (
          <span className="text-dim">{t('Checking readiness...')}</span>
        ) : (
          <>
            <span className={`readiness-pill ${tone}`} title={t('Ready: no issues. Needs Attention: warnings only, work can still run. Blocked: at least one error stops missions or checks until fixed. Unknown: not checked yet.')}>{t(readinessLabel(readiness))}</span>
            {attention > 0 && (
              <span className="workspace-readiness-item" title={t('Errors and warnings in the readiness details.')}>
                {t('{{count}} issue(s)', { count: attention })}
              </span>
            )}
            {readiness?.workflowProfileName && (
              <span className="workspace-readiness-item" title={t('The workflow profile that applies to this vessel, chosen from its own, its fleet\'s, or the global profile. Its commands decide which checks can run.')}>
                {readiness.workflowProfileName}
              </span>
            )}
            {readiness && readiness.availableCheckTypes.length > 0 && (
              <span className="workspace-readiness-item" title={readiness.availableCheckTypes.join(', ')}>
                {t('{{count}} check type(s)', { count: readiness.availableCheckTypes.length })}
              </span>
            )}
            {checkout && checkoutText && (
              <span className={`workspace-readiness-checkout${checkout.kind === 'unavailable' ? ' unavailable' : ''}`} title={t('Where checks and workspace actions for this vessel run: its working directory on the Admiral host, or a Harbor\'s copy.')}>
                <span className="workspace-readiness-checkout-text">{t(checkoutText.template, checkoutText.params)}</span>
                {checkout.kind !== 'unavailable' && checkout.path && <CopyButton text={checkout.path} title={t('Copy the checkout path to the clipboard.')} />}
              </span>
            )}
          </>
        )}
      </div>
      {expanded && !loading && (
        <ReadinessPanel title={t('Readiness')} readiness={readiness} embedded emptyMessage={t('Readiness data is not available for this vessel yet.')} />
      )}
    </div>
  );
}
