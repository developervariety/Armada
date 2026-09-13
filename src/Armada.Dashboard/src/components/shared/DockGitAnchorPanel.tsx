import type { DockGitAnchorSnapshot, GitAnchorFileHistory } from '../../types/models';
import { useLocale } from '../../context/LocaleContext';

interface DockGitAnchorPanelProps {
  snapshot: DockGitAnchorSnapshot | null;
}

const labelStyle = { fontSize: 11, fontWeight: 600, textTransform: 'uppercase' as const, marginBottom: 4 };

/**
 * Git evidence captured when the dock was provisioned. It is context evidence, not a landing gate: the start-ref and
 * stage-base checks remain the authority. Absent, invalid or unsupported snapshots arrive as null and say so.
 */
export default function DockGitAnchorPanel({ snapshot }: DockGitAnchorPanelProps) {
  const { t, formatDateTime } = useLocale();

  if (!snapshot) {
    return (
      <div className="card" style={{ marginTop: 16 }}>
        <h3>{t('Starting Point')}</h3>
        <p className="text-muted">{t('No provisioning evidence is recorded for this dock.')}</p>
      </div>
    );
  }

  const anchors = snapshot.anchors;
  const files = anchors?.files ?? [];
  const priorArt = anchors?.priorArt ?? [];

  const pathNote = (file: GitAnchorFileHistory): string | null => {
    if (file.isExternalSourceTree) return t('external source tree');
    if (!file.existsOnRevision) return t('not on the provisioned revision');
    return null;
  };

  return (
    <div className="card" style={{ marginTop: 16 }}>
      <h3>{t('Starting Point')}</h3>

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(250px, 1fr))', gap: 16 }}>
        <div>
          <div className="text-muted" style={labelStyle}>{t('Provisioned Commit')}</div>
          <div className="mono" style={{ wordBreak: 'break-all' }}>{snapshot.provisionedCommit || '-'}</div>
        </div>
        <div>
          <div className="text-muted" style={labelStyle}>{t('Target Branch')}</div>
          <div className="mono">{anchors?.targetBranch || '-'}</div>
        </div>
        <div>
          <div className="text-muted" style={labelStyle}>{t('Target Tip')}</div>
          <div className="mono" style={{ wordBreak: 'break-all' }}>{anchors?.targetTip || '-'}</div>
        </div>
        <div>
          <div className="text-muted" style={labelStyle}>{t('Evidence State')}</div>
          <span className="tag">{snapshot.state}</span>
        </div>
        <div>
          <div className="text-muted" style={labelStyle}>{t('Provisioned')}</div>
          <div>{formatDateTime(snapshot.provisionedUtc)}</div>
        </div>
        <div>
          <div className="text-muted" style={labelStyle}>{t('Resolved')}</div>
          <div>{snapshot.resolvedUtc ? formatDateTime(snapshot.resolvedUtc) : '-'}</div>
        </div>
      </div>

      {snapshot.state === 'Seeded' && (
        <p className="text-muted" style={{ marginTop: 12 }}>
          {t('Anchor enrichment has not run yet; only the provisioned commit is recorded.')}
        </p>
      )}
      {snapshot.errorCode && (
        <p style={{ marginTop: 12 }}>
          <span className="text-muted">{t('Error code')}: </span>
          <span className="mono">{snapshot.errorCode}</span>
        </p>
      )}
      {snapshot.truncated && (
        <p className="text-muted" style={{ marginTop: 8 }}>{t('Some evidence was omitted to stay within storage bounds.')}</p>
      )}
      {anchors?.resolutionError && (
        <p style={{ marginTop: 8 }}>
          <span className="text-muted">{t('Resolution error')}: </span>
          <span>{anchors.resolutionError}</span>
        </p>
      )}

      {files.length > 0 && (
        <div style={{ marginTop: 16 }}>
          <div className="text-muted" style={labelStyle}>{t('Recent Commits On Mission Paths')}</div>
          {files.map((file) => {
            const note = pathNote(file);
            return (
              <div key={file.path} data-anchor-path={file.path} style={{ marginBottom: 8 }}>
                <div>
                  <span className="mono">{file.path}</span>
                  {file.requestedPath && file.requestedPath !== file.path && (
                    <span className="text-muted" style={{ marginLeft: 6 }}>{t('requested as {{path}}', { path: file.requestedPath })}</span>
                  )}
                  {note && <span className="tag" style={{ marginLeft: 6 }}>{note}</span>}
                </div>
                {file.commits.length > 0 && (
                  <ul className="mono" style={{ margin: '4px 0 0', paddingLeft: 18 }}>
                    {file.commits.map((commit) => (
                      <li key={commit.sha}>
                        {commit.sha.slice(0, 12)} {commit.subject}{commit.dateUtc ? ` (${commit.dateUtc})` : ''}
                      </li>
                    ))}
                  </ul>
                )}
              </div>
            );
          })}
        </div>
      )}

      {priorArt.length > 0 && (
        <div style={{ marginTop: 16 }}>
          <div className="text-muted" style={labelStyle}>{t('Prior Art On Target')}</div>
          {priorArt.map((item) => (
            <div key={item.term} data-prior-art={item.term} style={{ marginBottom: 4 }}>
              <span className="mono">{item.term}</span>
              <span className={`tag${item.found ? '' : ' stalled'}`} style={{ marginLeft: 6 }}>
                {item.found ? t('found in {{count}} files', { count: item.matchingFileCount }) : t('not found')}
              </span>
              {item.sampleLocations.length > 0 && (
                <span className="text-muted mono" style={{ marginLeft: 6 }}>{item.sampleLocations.join(', ')}</span>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
