import { useState, useCallback, useMemo } from 'react';
import { copyToClipboard } from './CopyButton';
import { useLocale } from '../../context/LocaleContext';
import { parseDiff, type DiffLine } from '../../lib/gitDiff';

interface DiffViewerProps {
  open: boolean;
  title: string;
  rawDiff: string;
  loading?: boolean;
  onClose: () => void;
}

function escapeHtml(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

function lineNumber(value: number | undefined): string {
  return value === undefined ? '' : String(value);
}

function renderDiffLines(lines: DiffLine[]): string {
  let html = '';
  for (const line of lines) {
    const escaped = escapeHtml(line.text);
    switch (line.kind) {
      case 'file-header':
        html += `<div class="diff-file-header">${escaped}</div>`;
        break;
      case 'hunk-header':
        html += `<div class="diff-hunk-header">${escaped}</div>`;
        break;
      case 'meta':
      case 'no-newline':
        html += `<div class="diff-meta-line">${escaped}</div>`;
        break;
      default: {
        const css = line.kind === 'add' ? 'diff-line-add' : line.kind === 'del' ? 'diff-line-del' : 'diff-line-ctx';
        html += `<div class="diff-line ${css}"><span class="diff-line-num diff-line-num-old">${lineNumber(line.oldNumber)}</span><span class="diff-line-num diff-line-num-new">${lineNumber(line.newNumber)}</span><span class="diff-line-content">${escaped}</span></div>`;
      }
    }
  }
  return html;
}

export default function DiffViewer({ open, title, rawDiff, loading, onClose }: DiffViewerProps) {
  const { t } = useLocale();
  const [selectedFile, setSelectedFile] = useState<number | null>(null);
  const [shownDiff, setShownDiff] = useState(rawDiff);
  const [copied, setCopied] = useState(false);

  // A file is selected by its position in this diff, so a new diff starts on the whole view.
  if (shownDiff !== rawDiff) {
    setShownDiff(rawDiff);
    setSelectedFile(null);
  }

  const isEmpty = !rawDiff || !rawDiff.trim() || rawDiff === 'No changes' || rawDiff === 'No modified files';
  const parsed = useMemo(() => (isEmpty ? { lines: [], files: [] } : parseDiff(rawDiff)), [rawDiff, isEmpty]);
  const files = parsed.files;
  const totalAdditions = files.reduce((s, f) => s + f.additions, 0);
  const totalDeletions = files.reduce((s, f) => s + f.deletions, 0);

  const contentHtml = useMemo(() => {
    if (isEmpty) {
      return `<div class="diff-empty-state"><span class="text-dim">${t('No modified files')}</span></div>`;
    }
    const file = selectedFile === null ? undefined : files[selectedFile];
    if (file) {
      const fileLines = parsed.lines.filter(line => line.lineIndex >= file.startLine && line.lineIndex < file.endLine);
      return renderDiffLines(fileLines);
    }
    return renderDiffLines(parsed.lines);
  }, [parsed, files, selectedFile, isEmpty, t]);

  const handleCopy = useCallback(() => {
    copyToClipboard(rawDiff).then(() => {
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    }).catch(() => {});
  }, [rawDiff]);

  const handleFileClick = useCallback((fileIndex: number) => {
    setSelectedFile(prev => prev === fileIndex ? null : fileIndex);
  }, []);

  if (!open) return null;

  return (
    <div className="diff-modal-overlay" onClick={onClose}>
      <div className="diff-modal" onClick={e => e.stopPropagation()}>
        <div className="diff-modal-header">
          <h3 className="viewer-title">{title}</h3>
          {!isEmpty && (
            <div className="diff-modal-stats">
              <span className="diff-stat-files">
                {files.length} {t(files.length === 1 ? 'file changed' : 'files changed')}
              </span>
              <span className="diff-stat-add">+{totalAdditions}</span>
              <span className="diff-stat-del">-{totalDeletions}</span>
            </div>
          )}
          <div className="viewer-actions">
            {!isEmpty && (
              <button
                className={`btn btn-sm${copied ? ' copied' : ''}`}
                onClick={handleCopy}
              >
                {copied ? t('Copied!') : t('Copy Raw')}
              </button>
            )}
            <button className="btn btn-sm" onClick={onClose}>{t('Close')}</button>
          </div>
        </div>
        <div className="diff-modal-body">
          {files.length > 0 && (
            <div className="diff-file-nav">
              <div className="diff-file-nav-header">
                {t('Files')} ({files.length})
              </div>
              {files.map((f, index) => {
                const pathParts = f.name.split('/');
                const fileName = pathParts.pop() || f.name;
                const dirPath = pathParts.join('/');
                return (
                  <div
                    key={`${index}:${f.name}`}
                    className={`diff-file-nav-item${selectedFile === index ? ' active' : ''}`}
                    onClick={() => handleFileClick(index)}
                  >
                    <span className="diff-file-nav-name">{fileName}</span>
                    {dirPath && <span className="diff-file-nav-path">{dirPath}/</span>}
                    <div className="diff-file-nav-counts">
                      <span className="diff-file-nav-add">+{f.additions}</span>
                      <span className="diff-file-nav-del">-{f.deletions}</span>
                    </div>
                  </div>
                );
              })}
            </div>
          )}
          <div className="diff-content-wrap">
            {loading ? (
                <div className="diff-content-area">
                  <div className="diff-empty-state">
                  <span className="text-dim">{t('Loading diff...')}</span>
                </div>
              </div>
            ) : (
              <div
                className="diff-content-area"
                dangerouslySetInnerHTML={{ __html: contentHtml }}
              />
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
