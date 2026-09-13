import type { CSSProperties } from 'react';
import { useLocale } from '../../context/LocaleContext';
import type { LandingPreviewResult } from '../../types/models';

interface LandingPreviewCardProps {
  preview: LandingPreviewResult | null;
  loading: boolean;
  /** Branch line shown under the title, e.g. "feature/x -> main". */
  meta: string;
  /** Shown when no preview could be loaded. */
  unavailableMessage: string;
  /** Shown when the preview lists no issues. */
  noIssuesMessage: string;
  style?: CSSProperties;
}

/**
 * The one landing preview card used by vessel, mission and merge queue detail. The preview is advisory: its
 * pill reports whether the preview found error issues, never a landing verdict, and the card states that its
 * Check evidence is a scoped summary rather than the Check gate for the landed commit.
 */
export default function LandingPreviewCard({ preview, loading, meta, unavailableMessage, noIssuesMessage, style }: LandingPreviewCardProps) {
  const { t } = useLocale();

  return (
    <div className="card landing-preview-card" style={style}>
      <div className="readiness-panel-header">
        <div>
          <h3>{t('Landing Preview')}</h3>
          <div className="readiness-panel-meta">{meta}</div>
        </div>
        {preview && !loading && (
          <span className={`readiness-pill ${preview.isReadyToLand ? 'ready' : 'warning'}`}>
            {preview.isReadyToLand ? t('No blocking preview issues') : t('Preview issues')}
          </span>
        )}
      </div>
      {loading ? (
        <div className="text-dim">{t('Calculating landing preview...')}</div>
      ) : !preview ? (
        <div className="text-dim">{unavailableMessage}</div>
      ) : (
        <>
          <p className="text-dim" style={{ margin: '0 0 0.5rem', fontSize: '0.8rem' }}>
            {t('Advisory prediction. Check evidence here is a scoped summary, not the Check gate for the landed commit.')}
          </p>
          <div className="readiness-summary-row">
            <span>{t('Branch category')}: {preview.branchCategory}</span>
            <span>{t('Landing mode')}: {preview.landingMode || t('Inherited')}</span>
            <span>{t('Cleanup')}: {preview.branchCleanupPolicy || t('Inherited')}</span>
            {preview.expectedLandingAction && <span>{t('Action')}: {preview.expectedLandingAction}</span>}
            <span>{preview.requirePassingChecksToLand ? t('Advisory preview setting: passing checks required') : t('Advisory preview setting: passing checks optional')}</span>
          </div>
          <div className="readiness-summary-row">
            <span>{preview.targetBranchProtected ? t('Protected target branch') : t('Target branch not protected')}</span>
            {preview.protectedBranchMatch && <span>{t('Policy')}: <span className="mono">{preview.protectedBranchMatch}</span></span>}
            {preview.requirePullRequestForProtectedBranches && <span>{t('PR required for protected branches')}</span>}
            {preview.requireMergeQueueForReleaseBranches && <span>{t('Merge queue required for release branches')}</span>}
          </div>
          {preview.latestCheckSummary && (
            <div className="landing-preview-latest-check">
              <strong>{t('Latest check')}</strong>
              <div className="text-dim">{preview.latestCheckSummary}</div>
            </div>
          )}
          {preview.issues.length > 0 ? (
            <div className="readiness-issues">
              {preview.issues.map((issue, index) => (
                <div key={`${issue.code}-${index}`} className={`readiness-issue ${issue.severity.toLowerCase()}`}>
                  <div className="readiness-issue-title-row">
                    <strong>{issue.title}</strong>
                    <span className={`readiness-issue-severity ${issue.severity.toLowerCase()}`}>{issue.severity}</span>
                  </div>
                  <div className="text-dim">{issue.message}</div>
                </div>
              ))}
            </div>
          ) : (
            <div className="readiness-success-copy">{noIssuesMessage}</div>
          )}
        </>
      )}
    </div>
  );
}
