import { useId, useState, type KeyboardEvent } from 'react';
import { Link } from 'react-router-dom';
import type { CliPermissionRequest } from '../../types/models';
import { useLocale } from '../../context/LocaleContext';
import CopyButton from '../shared/CopyButton';
import { prettyJson } from '../../lib/askFormat';
import { decisionSourceText, isPendingRequest, requestStatusLabel } from '../../lib/cliPermissions';
import CliPermissionCountdown from './CliPermissionCountdown';
import CliPermissionDecisionControls from './CliPermissionDecisionControls';

interface CliPermissionCardProps {
  request: CliPermissionRequest;
  onDecided?: (updated: CliPermissionRequest) => void;
  /** Highlight (deep link target on the CLI Tool Permissions page). */
  highlighted?: boolean;
  /** Show the conversation title (on the permissions page, where the request is out of context). */
  showThread?: boolean;
  /** Let a decided card collapse to its header and command line (Ask Armada); click the header to toggle. */
  collapsible?: boolean;
}

/**
 * Approval card for a CLI tool call a captain wants to make (shell command, file edit, web fetch): the tool, the
 * command or input, who asked, a live expiry countdown while pending, and the decision afterwards. Approvers get
 * Allow once / Allow and remember / Allow for this conversation / Deny; everyone else sees that it waits on an admin.
 * When collapsible, a decided card starts collapsed to its header and command line, and clicking the header toggles it.
 */
export default function CliPermissionCard({ request, onDecided, highlighted, showThread, collapsible }: CliPermissionCardProps) {
  const { t, formatRelativeTime, formatDateTime } = useLocale();
  const status = String(request.status || '');
  const normalized = status.toLowerCase();
  const pending = isPendingRequest(request);
  const input = prettyJson(request.inputText);
  const summary = (request.summaryText ?? '').trim();
  const summaryId = useId();
  const decidedHow = pending ? null : decisionSourceText(t, request);
  const canCollapse = !!collapsible && !pending;
  // Expanded while pending, collapsed once decided, unless the user toggled it (only possible after the decision).
  const [toggled, setToggled] = useState<boolean | null>(null);
  const expanded = toggled ?? pending;
  const bodyId = useId();
  const collapsed = canCollapse && !expanded;
  const toggle = () => setToggled(!expanded);

  return (
    <div
      id={`cli-permission-${request.id}`}
      className={`ask-confirm-card cli-perm-card is-${normalized || 'unknown'}${highlighted ? ' is-highlighted' : ''}${canCollapse ? ' is-collapsible' : ''}${collapsed ? ' is-collapsed' : ''}`}
      role="group"
      aria-label={t('CLI permission: {{tool}}', { tool: request.toolName })}
      data-request-id={request.id}
    >
      <div
        className="ask-confirm-header"
        {...(canCollapse ? {
          role: 'button',
          tabIndex: 0,
          'aria-expanded': expanded,
          'aria-controls': bodyId,
          title: expanded ? t('Collapse this permission card.') : t('Expand this permission card to see who asked, the input, and the decision.'),
          onClick: toggle,
          onKeyDown: (e: KeyboardEvent<HTMLDivElement>) => {
            if (e.key === 'Enter' || e.key === ' ') {
              e.preventDefault();
              toggle();
            }
          },
        } : {})}
      >
        {canCollapse && (
          <span className="cli-perm-chevron" aria-hidden="true">
            <svg viewBox="0 0 16 16"><path d="M6 4l4 4-4 4" /></svg>
          </span>
        )}
        <span className="ask-confirm-label">{pending ? t('Permission needed') : t('CLI permission')}</span>
        <code className="ask-confirm-tool" title={t('The CLI tool the captain wants to use, such as Bash for a shell command, Edit for a file change, or WebFetch for a URL.')}>{request.toolName || t('tool')}</code>
        <span className="ask-confirm-status">
          <span className={`tag cli-perm-status is-${normalized}`} title={
            normalized === 'pending' ? t('Waiting for a decision. The captain is paused on this call until someone decides or it expires.')
              : normalized === 'allowed' ? t('The call was allowed and the captain ran it.')
                : normalized === 'denied' ? t('The call was denied and did not run.')
                  : normalized === 'expired' ? t('Nobody decided before the timeout, so the call was denied and did not run.')
                    : normalized === 'cancelled' ? t('The turn or mission ended, or the captain withdrew the call, before a decision.')
                      : undefined
          }>{requestStatusLabel(t, status)}</span>
        </span>
        <CliPermissionCountdown expiresUtc={request.expiresUtc} active={pending} timeoutDecision={request.timeoutDecision} />
      </div>

      {collapsed && summary && <code className="cli-perm-summary-line" title={summary}>{summary}</code>}

      {!collapsed && (
      <div id={bodyId} className="cli-perm-body">
      {summary && (
        <div className="ask-confirm-pre-wrap cli-perm-summary">
          <pre id={summaryId} className="ask-confirm-pre">{summary}</pre>
          <CopyButton text={summary} title={t('Copy the command or summary of this request to the clipboard.')} />
        </div>
      )}

      <dl className="cli-perm-meta">
        {request.captainName || request.captainId ? (
          <div>
            <dt title={t('The captain that asked to run this tool.')}>{t('Captain')}</dt>
            <dd>{request.captainId ? <Link to={`/captains/${encodeURIComponent(request.captainId)}`}>{request.captainName || request.captainId}</Link> : request.captainName}</dd>
          </div>
        ) : null}
        {request.vesselId && (
          <div>
            <dt title={t('The repository the captain was working in.')}>{t('Vessel')}</dt>
            <dd><Link to={`/vessels/${encodeURIComponent(request.vesselId)}`}>{request.vesselName || request.vesselId}</Link></dd>
          </div>
        )}
        {request.missionId && (
          <div>
            <dt title={t('The mission the captain was running when it asked.')}>{t('Mission')}</dt>
            <dd><Link to={`/missions/${encodeURIComponent(request.missionId)}`}>{request.missionTitle || request.missionId}</Link></dd>
          </div>
        )}
        {showThread && request.threadId && (
          <div>
            <dt title={t('The Ask Armada conversation whose turn made this request.')}>{t('Conversation')}</dt>
            <dd><Link to={`/ask/${encodeURIComponent(request.threadId)}`}>{request.threadTitle || request.threadId}</Link></dd>
          </div>
        )}
        {request.createdUtc && (
          <div>
            <dt title={t('When the captain made the request.')}>{t('Asked')}</dt>
            <dd><time dateTime={request.createdUtc} title={formatDateTime(request.createdUtc)}>{formatRelativeTime(request.createdUtc)}</time></dd>
          </div>
        )}
      </dl>

      {input && input !== '{}' && (
        <details className="ask-confirm-details">
          <summary title={t('Show the full tool input the captain sent, as JSON. Secrets are redacted.')}>{t('Input')}</summary>
          <div className="ask-confirm-pre-wrap">
            <pre className="ask-confirm-pre">{input}</pre>
            <CopyButton text={request.inputText ?? ''} title={t('Copy the tool input JSON to the clipboard.')} />
          </div>
        </details>
      )}

      {pending && request.canDecide && (
        <CliPermissionDecisionControls request={request} onDecided={onDecided} describedBy={summary ? summaryId : undefined} />
      )}
      {pending && !request.canDecide && (
        <p className="ask-confirm-outcome text-dim cli-perm-waiting">{t('Waiting for an admin to decide.')}</p>
      )}

      {!pending && (
        <div className={`ask-confirm-outcome cli-perm-outcome${normalized === 'allowed' ? ' is-success' : normalized === 'denied' ? ' is-failed' : ' text-dim'}`}>
          <span>
            {normalized === 'allowed' && t('Allowed. The captain ran the tool.')}
            {normalized === 'denied' && t('Denied. The tool did not run.')}
            {normalized === 'expired' && t('Expired without a decision. The tool did not run.')}
            {normalized === 'cancelled' && t('Cancelled. The turn ended before a decision.')}
          </span>
          {decidedHow && <span className="text-dim"> {decidedHow}</span>}
          {request.decidedUtc && (
            <span className="text-dim"> <time dateTime={request.decidedUtc} title={formatDateTime(request.decidedUtc)}>{formatRelativeTime(request.decidedUtc)}</time></span>
          )}
          {request.decisionMessage && <p className="cli-perm-decision-message">{t('Message: {{text}}', { text: request.decisionMessage })}</p>}
        </div>
      )}
      </div>
      )}
    </div>
  );
}
