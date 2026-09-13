import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import type { MissionRecoveryReport } from '../../types/models';
import { useLocale } from '../../context/LocaleContext';

interface MissionRecoveryPanelProps {
  missionId: string | null;
  report: MissionRecoveryReport | null;
  error: string | null;
  loading: boolean;
}

/**
 * Recovery evidence for the mission an incident names, from the mission recovery report. Rescues are listed as
 * mission links, not counts. Truncated sections, unavailable reasons and load failures are stated, never hidden.
 */
export default function MissionRecoveryPanel({ missionId, report, error, loading }: MissionRecoveryPanelProps) {
  const { t, formatDateTime } = useLocale();

  let body: ReactNode;
  if (!missionId) {
    body = <p className="text-dim">{t('This incident names no mission, so no recovery report is available.')}</p>;
  } else if (loading) {
    body = <p className="text-dim">{t('Loading recovery report...')}</p>;
  } else if (error) {
    body = <p className="text-dim">{error}</p>;
  } else if (!report) {
    body = <p className="text-dim">{t('No recovery report was returned.')}</p>;
  } else {
    body = (
      <>
        {report.recoveryBudgetExhausted && (
          <p><span className="tag stalled">{t('Recovery budget exhausted')}</span></p>
        )}
        <div className="detail-meta-grid">
          <div className="detail-field">
            <span className="detail-label">{t('Mission')}</span>
            <span><Link to={`/missions/${report.missionId}`}>{report.missionId}</Link></span>
          </div>
          <div className="detail-field">
            <span className="detail-label">{t('Mission Status')}</span>
            <span>{report.status}</span>
          </div>
          <div className="detail-field">
            <span className="detail-label">{t('Recovery Attempts')}</span>
            <span>{t('{{attempts}} of {{max}}', { attempts: report.recoveryAttempts, max: report.maxRecoveryAttempts })}</span>
          </div>
          <div className="detail-field">
            <span className="detail-label">{t('Landing Retries')}</span>
            <span>{t('{{attempts}} of {{max}}', { attempts: report.landingRetryCount, max: report.maxLandingRetries })}</span>
          </div>
          <div className="detail-field">
            <span className="detail-label">{t('Autonomous Recovery')}</span>
            <span>{report.autonomousRecoveryEnabled ? t('Enabled') : t('Disabled')}</span>
          </div>
          <div className="detail-field">
            <span className="detail-label">{t('Last Recovery Action')}</span>
            <span>{report.lastRecoveryActionUtc ? formatDateTime(report.lastRecoveryActionUtc) : '-'}</span>
          </div>
          {report.failureReason && (
            <div className="detail-field detail-field-full">
              <span className="detail-label">{t('Failure Reason')}</span>
              <span>{report.failureReason}</span>
            </div>
          )}
        </div>

        <h4 style={{ marginTop: '1rem' }}>{t('Rescue Missions')}</h4>
        {report.rescuesUnavailableReason && <p className="text-dim">{report.rescuesUnavailableReason}</p>}
        {report.rescues.length === 0 && !report.rescuesUnavailableReason && (
          <p className="text-dim">{t('No rescue missions are recorded.')}</p>
        )}
        {report.rescues.length > 0 && (
          <ul style={{ margin: 0, paddingLeft: 18 }}>
            {report.rescues.map((rescue) => (
              <li key={rescue.missionId} data-rescue={rescue.missionId}>
                <Link to={`/missions/${rescue.missionId}`}>{rescue.title || rescue.missionId}</Link>
                <span className="tag" style={{ marginLeft: 6 }}>{rescue.status}</span>
                {rescue.commitHash && <span className="mono text-dim" style={{ marginLeft: 6 }}>{rescue.commitHash.slice(0, 12)}</span>}
                {rescue.failureReason && <span className="text-dim" style={{ marginLeft: 6 }}>{rescue.failureReason}</span>}
              </li>
            ))}
          </ul>
        )}
        {report.rescuesTruncated && <p className="text-dim">{t('More rescues exist than are listed.')}</p>}

        {report.incidentsUnavailableReason && <p className="text-dim">{report.incidentsUnavailableReason}</p>}
        {report.incidentsTruncated && <p className="text-dim">{t('More incidents exist than are listed.')}</p>}
        {report.eventsUnavailableReason && <p className="text-dim">{report.eventsUnavailableReason}</p>}
        {report.eventsWindowFull && <p className="text-dim">{t('Older recovery events may exist beyond the listed window.')}</p>}
      </>
    );
  }

  return (
    <section className="card detail-panel">
      <h3>{t('Mission Recovery')}</h3>
      {body}
    </section>
  );
}
