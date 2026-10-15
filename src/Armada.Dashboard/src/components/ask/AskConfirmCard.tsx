import { useId } from 'react';
import type { AskActionProposal } from '../../types/models';
import { useLocale } from '../../context/LocaleContext';
import CopyButton from '../shared/CopyButton';
import { prettyJson } from '../../lib/askFormat';

export { prettyJson } from '../../lib/askFormat';

interface AskConfirmCardProps {
  proposal: AskActionProposal;
  onApprove?: (proposal: AskActionProposal) => void;
  onReject?: (proposal: AskActionProposal) => void;
  /** True while an approve or reject call for this proposal is in flight. */
  busy?: boolean;
  /** Compact mode for ActionResult messages: no heading text, outcome only. */
  compact?: boolean;
}

/**
 * Inline confirmation for a state-changing tool call. Shows the tool, a one-line summary, the exact arguments,
 * Approve / Reject while pending, and the outcome afterwards. Nothing runs until the user approves.
 */
export default function AskConfirmCard({ proposal, onApprove, onReject, busy, compact }: AskConfirmCardProps) {
  const { t, formatRelativeTime } = useLocale();
  const status = String(proposal.status || '');
  const normalized = status.toLowerCase();
  const pending = normalized === 'pending';
  const args = prettyJson(proposal.argumentsText);
  const result = prettyJson(proposal.resultText);
  const fromQuickAction = String(proposal.source).toLowerCase() === 'quickaction';
  // Approve and Reject are described by the proposal summary so a screen reader says what they act on.
  const summaryId = useId();

  return (
    <div className={`ask-confirm-card is-${normalized || 'unknown'}${compact ? ' is-compact' : ''}`} role="group" aria-label={t('Action: {{tool}}', { tool: proposal.toolName })}>
      <div className="ask-confirm-header">
        <span className="ask-confirm-label">
          {pending ? t('Approval needed') : t('Action')}
        </span>
        <code className="ask-confirm-tool" title={t('The Armada tool this action calls, for example dispatch or run_fleet_action.')}>{proposal.toolName}</code>
        <span className="ask-confirm-source text-dim" title={fromQuickAction ? t('You started this from a quick action or slash command.') : t('The captain suggested this action in its reply. It runs only if you approve it.')}>{fromQuickAction ? t('Quick action') : t('Proposed by the captain')}</span>
        <span className="ask-confirm-status">
          <span
            className={`tag ${normalized}`.trim()}
            title={
              normalized === 'pending' ? t('Waiting for you to approve or reject it. Nothing has run.')
                : normalized === 'approved' ? t('Approved and starting.')
                  : normalized === 'rejected' ? t('Rejected. Nothing was run.')
                    : normalized === 'expired' ? t('Nobody decided before the proposal expired, so nothing was run.')
                      : normalized === 'executed' ? t('The action ran successfully.')
                        : normalized === 'failed' ? t('The action was approved but failed when it ran. See the error below.')
                          : undefined
            }
          >{t(status)}</span>
        </span>
      </div>

      {proposal.summaryText && <p id={summaryId} className="ask-confirm-summary">{proposal.summaryText}</p>}

      {args && (
        <details className="ask-confirm-details">
          <summary title={t('Show the exact JSON arguments the tool will be called with, so you can check them before approving.')}>{t('Exact arguments')}</summary>
          <div className="ask-confirm-pre-wrap">
            <pre className="ask-confirm-pre">{args}</pre>
            <CopyButton text={proposal.argumentsText ?? ''} title={t('Copy the tool arguments JSON to the clipboard.')} />
          </div>
        </details>
      )}

      {pending && (
        <div className="ask-confirm-actions">
          <span className="text-dim ask-confirm-note">
            {proposal.expiresUtc
              ? t('Nothing runs until you approve. Expires {{time}}.', { time: formatRelativeTime(proposal.expiresUtc) })
              : t('Nothing runs until you approve.')}
          </span>
          <button type="button" className="btn btn-sm" aria-describedby={proposal.summaryText ? summaryId : undefined} onClick={() => onReject?.(proposal)} disabled={busy || !onReject} title={t('Decline this action. Nothing runs, and the captain is told it was rejected.')}>
            {t('Reject')}
          </button>
          <button type="button" className="btn btn-sm btn-primary" aria-describedby={proposal.summaryText ? summaryId : undefined} onClick={() => onApprove?.(proposal)} disabled={busy || !onApprove} title={t('Run this action now with exactly the arguments shown. It may start real work such as missions or commands.')}>
            {busy ? t('Working...') : t('Approve')}
          </button>
        </div>
      )}

      {normalized === 'approved' && <p className="ask-confirm-outcome text-dim">{t('Approved. Running now...')}</p>}
      {normalized === 'rejected' && <p className="ask-confirm-outcome text-dim">{t('Rejected. Nothing was run.')}</p>}
      {normalized === 'expired' && <p className="ask-confirm-outcome text-dim">{t('Expired without running.')}</p>}
      {normalized === 'executed' && (
        <div className="ask-confirm-outcome is-success">
          <span>
            {proposal.executedUtc ? t('Ran {{time}}.', { time: formatRelativeTime(proposal.executedUtc) }) : t('Ran successfully.')}
          </span>
          {result && (
            <details className="ask-confirm-details">
              <summary title={t('Show what the tool returned when it ran.')}>{t('Result')}</summary>
              <pre className="ask-confirm-pre">{result}</pre>
            </details>
          )}
        </div>
      )}
      {normalized === 'failed' && (
        <div className="ask-confirm-outcome is-failed" role="alert">
          {proposal.errorText || t('The action failed.')}
        </div>
      )}
    </div>
  );
}
