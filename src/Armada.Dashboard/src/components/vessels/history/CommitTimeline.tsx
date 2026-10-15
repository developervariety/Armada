import { useMemo, useState } from 'react';
import type { GitChangedFile, VesselCommit } from '../../../types/models';
import { useLocale } from '../../../context/LocaleContext';
import CopyButton from '../../shared/CopyButton';
import { formatIsoDay, groupCommitsByDay } from '../../../lib/vesselHistory';

interface CommitTimelineProps {
  commits: VesselCommit[];
  /** Day (yyyy-MM-dd) the list was jumped to, highlighted when it has commits. */
  selectedDate: string | null;
}

const DAY_HEADER_FORMAT: Intl.DateTimeFormatOptions = { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' };

const KIND_CLASS: Record<string, string> = {
  Added: 'complete',
  Deleted: 'failed',
  Modified: 'working',
  Renamed: 'review',
  Copied: 'review',
};

function FileRow({ file }: { file: GitChangedFile }) {
  const { t } = useLocale();
  const hasSource = (file.kind === 'Renamed' || file.kind === 'Copied') && !!file.oldPath;
  return (
    <li className="vhist-file">
      <span className={`tag ${KIND_CLASS[file.kind] ?? ''} vhist-file-kind`} title={t('How this commit changed the file: {{kind}}.', { kind: t(file.kind) })}>{t(file.kind)}</span>
      <span className="mono vhist-file-path" title={hasSource ? `${file.oldPath} -> ${file.path}` : file.path}>
        {hasSource ? <>{file.oldPath} <span aria-hidden="true">-&gt;</span><span className="sr-only">{t('renamed to')}</span> {file.path}</> : file.path}
      </span>
      <span className="vhist-file-stats" title={file.isBinary ? t('Binary file: line counts do not apply.') : t('Lines added (+) and removed (-) in this file.')}>
        {file.isBinary
          ? <span className="text-dim">{t('binary')}</span>
          : (
            <>
              {file.addedLines !== null && <span className="vhist-added">+{file.addedLines}</span>}
              {file.deletedLines !== null && <span className="vhist-deleted">-{file.deletedLines}</span>}
            </>
          )}
      </span>
    </li>
  );
}

function CommitRow({ commit, expanded, onToggle }: { commit: VesselCommit; expanded: boolean; onToggle: () => void }) {
  const { t, formatDateTime, formatRelativeTime } = useLocale();
  const detailId = `commit-detail-${commit.sha}`;
  const shortSha = commit.shortSha || commit.sha.slice(0, 7);
  return (
    <li className={`vhist-commit${expanded ? ' expanded' : ''}`} data-testid="vhist-commit">
      <div className="vhist-commit-main">
        <button
          type="button"
          className="vhist-commit-toggle"
          aria-expanded={expanded}
          aria-controls={detailId}
          onClick={onToggle}
          title={expanded ? t('Hide the commit message body and changed files.') : t('Show the full commit message and the files this commit changed.')}
        >
          <span className="vhist-commit-caret" aria-hidden="true">{expanded ? '\u25BE' : '\u25B8'}</span>
          <span className="vhist-commit-subject">{commit.subject || t('(no message)')}</span>
        </button>
        {commit.isMerge && <span className="tag review vhist-merge-badge" title={t('Merge commit: it joins two or more lines of history, for example a landed mission branch.')}>{t('Merge')}</span>}
        <span className="vhist-commit-sha">
          <span className="mono" title={commit.sha}>{shortSha}</span>
          <CopyButton text={commit.sha} />
        </span>
      </div>
      <div className="vhist-commit-meta text-dim">
        <span title={commit.authorEmail ? t('Author: {{name}} <{{email}}>', { name: commit.authorName, email: commit.authorEmail }) : undefined}>{commit.authorName}</span>
        <span aria-hidden="true">&middot;</span>
        <time dateTime={commit.committedUtc} title={formatDateTime(commit.committedUtc)}>
          {formatRelativeTime(commit.committedUtc)}
        </time>
        <span className="vhist-commit-absolute">({formatDateTime(commit.committedUtc)})</span>
        <span aria-hidden="true">&middot;</span>
        <span className="vhist-added" title={t('Lines added across all files in this commit.')}>+{commit.addedLines}</span>
        <span className="vhist-deleted" title={t('Lines removed across all files in this commit.')}>-{commit.deletedLines}</span>
        <span>{t('{count, plural, one {# file} other {# files}}', { count: commit.filesChanged })}</span>
      </div>
      {expanded && (
        <div id={detailId} className="vhist-commit-detail">
          {commit.body && <pre className="vhist-commit-body">{commit.body}</pre>}
          {commit.authorName !== commit.committerName && commit.committerName && (
            <div className="text-dim vhist-commit-committer">
              {t('Committed by {{name}}', { name: commit.committerName })}
            </div>
          )}
          {commit.files.length > 0 ? (
            <ul className="vhist-file-list" aria-label={t('Changed files')}>
              {commit.files.map((file) => <FileRow key={`${file.oldPath ?? ''}>${file.path}`} file={file} />)}
            </ul>
          ) : (
            <div className="text-dim">{t('No file changes.')}</div>
          )}
          {commit.filesTruncated && (
            <div className="text-dim vhist-files-truncated">
              {t('Showing {{shown}} of {{total}} changed files.', { shown: commit.files.length, total: commit.filesChanged })}
            </div>
          )}
        </div>
      )}
    </li>
  );
}

/** Commits grouped under local day headers; each row expands to show the body and changed files. */
export default function CommitTimeline({ commits, selectedDate }: CommitTimelineProps) {
  const { locale, t } = useLocale();
  const groups = useMemo(() => groupCommitsByDay(commits), [commits]);
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set());

  function toggle(sha: string) {
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(sha)) next.delete(sha);
      else next.add(sha);
      return next;
    });
  }

  return (
    <div className="vhist-timeline">
      {groups.map((group) => (
        <section
          key={group.date}
          className={`vhist-day${group.date === selectedDate ? ' selected' : ''}`}
          aria-label={formatIsoDay(locale, group.date, DAY_HEADER_FORMAT)}
          data-testid="vhist-day"
        >
          <h4 className="vhist-day-header" title={t('Commits made on this day, in your local time zone, newest first.')}>{formatIsoDay(locale, group.date, DAY_HEADER_FORMAT)}</h4>
          <ul className="vhist-commit-list">
            {group.commits.map((commit) => (
              <CommitRow key={commit.sha} commit={commit} expanded={expanded.has(commit.sha)} onToggle={() => toggle(commit.sha)} />
            ))}
          </ul>
        </section>
      ))}
    </div>
  );
}
