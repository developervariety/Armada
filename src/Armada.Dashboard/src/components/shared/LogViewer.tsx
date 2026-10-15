import { useState, useEffect, useRef, useCallback } from 'react';
import { copyToClipboard } from './CopyButton';
import Markdown from './Markdown';
import { useLocale } from '../../context/LocaleContext';

interface LogViewerProps {
  open: boolean;
  title: string;
  content: string;
  totalLines?: number;
  loading?: boolean;
  /** Render the content as GitHub-flavored markdown instead of plain monospace text. Copy still copies the raw source. */
  markdown?: boolean;
  /** Whether the mission/process has completed (terminal state) */
  completed?: boolean;
  onClose: () => void;
  /** Called when line count changes */
  onLineCountChange?: (lines: number) => void;
  /** Called periodically when following is active - should refresh content */
  onRefresh?: () => void;
  /** Default line count */
  defaultLineCount?: number;
}

const LINE_COUNT_OPTIONS = [100, 200, 500, 1000];

export default function LogViewer({
  open,
  title,
  content,
  totalLines,
  loading,
  markdown,
  completed,
  onClose,
  onLineCountChange,
  onRefresh,
  defaultLineCount = 200,
}: LogViewerProps) {
  const { t } = useLocale();
  const [following, setFollowing] = useState(false);
  const [lineCount, setLineCount] = useState(defaultLineCount);
  const [copied, setCopied] = useState(false);
  const bodyRef = useRef<HTMLDivElement>(null);

  // Store onRefresh in a ref so the interval never needs to be recreated
  const onRefreshRef = useRef(onRefresh);
  onRefreshRef.current = onRefresh;

  // Use a ref for the interval ID to manage it imperatively
  const intervalRef = useRef<ReturnType<typeof setInterval> | null>(null);

  // Use a ref to track following state for the interval callback
  const followingRef = useRef(false);

  const startFollowing = useCallback(() => {
    if (intervalRef.current) return; // already running
    followingRef.current = true;
    setFollowing(true);
    onRefreshRef.current?.();
    intervalRef.current = setInterval(() => {
      if (followingRef.current) {
        onRefreshRef.current?.();
      }
    }, 1000);
  }, []);

  const stopFollowing = useCallback(() => {
    followingRef.current = false;
    setFollowing(false);
    if (intervalRef.current) {
      clearInterval(intervalRef.current);
      intervalRef.current = null;
    }
  }, []);

  const toggleFollow = useCallback(() => {
    if (followingRef.current) {
      stopFollowing();
    } else {
      startFollowing();
    }
  }, [startFollowing, stopFollowing]);

  // Auto-scroll when following and content changes
  useEffect(() => {
    if (followingRef.current && bodyRef.current) {
      bodyRef.current.scrollTop = bodyRef.current.scrollHeight;
    }
  }, [content]);

  // Stop when completed
  const prevCompleted = useRef(completed);
  useEffect(() => {
    if (completed && !prevCompleted.current && followingRef.current) {
      // One final refresh then stop
      onRefreshRef.current?.();
      stopFollowing();
    }
    prevCompleted.current = completed;
  }, [completed, stopFollowing]);

  // Cleanup on unmount
  useEffect(() => {
    return () => {
      if (intervalRef.current) {
        clearInterval(intervalRef.current);
        intervalRef.current = null;
      }
      followingRef.current = false;
    };
  }, []);

  // Reset when modal closes
  useEffect(() => {
    if (!open && followingRef.current) {
      stopFollowing();
    }
  }, [open, stopFollowing]);

  const handleLineCountChange = useCallback((count: number) => {
    setLineCount(count);
    onLineCountChange?.(count);
  }, [onLineCountChange]);

  const handleCopy = useCallback(() => {
    copyToClipboard(content).then(() => {
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    }).catch(() => {});
  }, [content]);

  if (!open) return null;

  return (
    <div className="viewer-overlay" onClick={onClose}>
      <div className="viewer-modal" onClick={e => e.stopPropagation()}>
        <div className="viewer-header">
          <h3 className="viewer-title">
            {title}
            {completed && <span className="log-live-badge" title={t('The work has finished, so this log will not grow any more.')} style={{ background: 'var(--tag-complete-bg, #2d4a2d)', color: 'var(--tag-complete-fg, #6fcf6f)' }}>{t('Done')}</span>}
            {following && !completed && <span className="log-live-badge" title={t('Following: the log reloads every second and scrolls to the newest lines.')}>{t('Live')}</span>}
          </h3>
          <div className="viewer-actions">
            <select
              className="log-line-select"
              aria-label={t('Log lines to show')}
              title={t('How many of the most recent log lines to load. More lines show more history but take longer to load.')}
              value={lineCount}
              onChange={e => handleLineCountChange(parseInt(e.target.value, 10))}
            >
              {LINE_COUNT_OPTIONS.map(n => (
                <option key={n} value={n}>{n} {t('lines')}</option>
              ))}
            </select>
            {onRefresh && !completed && (
              <button
                className={`btn btn-sm ${following ? 'btn-follow-active' : 'btn-follow-inactive'}`}
                onClick={toggleFollow}
                title={following ? t('Stop reloading the log every second, so you can read without it moving.') : t('Reload the log every second and keep the newest lines in view.')}
              >
                <span className={`follow-dot${following ? ' follow-dot-active' : ''}`}></span>
                {following ? t('Following') : t('Follow')}
              </button>
            )}
            {completed && (
              <span style={{ fontSize: '0.8em', color: 'var(--text-dim)', padding: '0.25rem 0.5rem' }} title={t('The work has finished, so this log will not grow any more.')}>{t('Completed')}</span>
            )}
            <button
              className={`btn btn-sm${copied ? ' copied' : ''}`}
              onClick={handleCopy}
              title={t('Copy the log text currently loaded here to the clipboard.')}
            >
              {copied ? t('Copied!') : t('Copy')}
            </button>
            <button className="btn btn-sm" onClick={onClose} title={t('Close the log. Following stops; the work itself keeps running.')}>{t('Close')}</button>
          </div>
        </div>
        <div className="viewer-body-wrap">
          <div
            ref={bodyRef}
            className={`viewer-body${!following ? ' log-paused' : ''}${markdown ? ' viewer-body-markdown' : ''}`}
            id="log-viewer-content"
            data-i18n-skip="true"
          >
            {loading
              ? t('Loading...')
              : markdown && content
                ? <Markdown preserveLineBreaks>{content}</Markdown>
                : (content || t('No log output'))}
          </div>
        </div>
        {totalLines !== undefined && (
          <div style={{
            padding: '0.4rem 1.25rem',
            borderTop: '1px solid var(--border)',
            fontSize: '0.75rem',
            color: 'var(--text-dim)',
          }}>
            {t('Total lines')}: {totalLines}
          </div>
        )}
      </div>
    </div>
  );
}
