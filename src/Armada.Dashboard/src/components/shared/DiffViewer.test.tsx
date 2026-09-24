import { describe, it, expect, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import DiffViewer from './DiffViewer';

vi.mock('../../context/LocaleContext', () => ({
  useLocale: () => ({ t: (text: string) => text }),
}));

// Git C-quotes a name with non-ASCII bytes: octal escapes are the UTF-8 bytes.
const QUOTED_DIFF = [
  'diff --git "a/src/Caf\\303\\251.cs" "b/src/Caf\\303\\251.cs"',
  'index 7898192..422c2b7 100644',
  '--- "a/src/Caf\\303\\251.cs"',
  '+++ "b/src/Caf\\303\\251.cs"',
  '@@ -1 +1,2 @@',
  ' a',
  '+quoted added line',
  '',
].join('\n');

// An unquoted name with a space before "b/": git ends the ---/+++ names with a tab.
const SPACE_B_DIFF = [
  'diff --git a/Plan b/data.cs b/Plan b/data.cs',
  'index 587be6b..b77b4eb 100644',
  '--- a/Plan b/data.cs\t',
  '+++ b/Plan b/data.cs\t',
  '@@ -1 +1,2 @@',
  ' x',
  '+y',
  '',
].join('\n');

const NO_NEWLINE_DIFF = [
  'diff --git a/f.txt b/f.txt',
  '--- a/f.txt',
  '+++ b/f.txt',
  '@@ -1 +1,2 @@',
  '-a',
  '\\ No newline at end of file',
  '+a',
  '+b',
  '',
].join('\n');

// Added content that itself starts with "++" and removed content that starts with "--".
const DASHED_CONTENT_DIFF = [
  'diff --git a/q.sql b/q.sql',
  '--- a/q.sql',
  '+++ b/q.sql',
  '@@ -1,2 +1,2 @@',
  '--- old comment',
  ' select 1;',
  '++++',
  '',
].join('\n');

function show(rawDiff: string) {
  return render(<DiffViewer open title="Diff" rawDiff={rawDiff} onClose={vi.fn()} />);
}

function gutter(container: HTMLElement, kind: 'add' | 'del' | 'ctx') {
  return Array.from(container.querySelectorAll(`.diff-line-${kind}`)).map(row => ({
    old: row.querySelector('.diff-line-num-old')?.textContent ?? '',
    new: row.querySelector('.diff-line-num-new')?.textContent ?? '',
    text: row.querySelector('.diff-line-content')?.textContent ?? '',
  }));
}

describe('DiffViewer', () => {
  it('decodes a C-quoted file name and opens its pane', () => {
    const { container } = show(QUOTED_DIFF);
    const item = container.querySelector('.diff-file-nav-item') as HTMLElement;
    expect(item.querySelector('.diff-file-nav-name')?.textContent).toBe('Café.cs');
    expect(item.querySelector('.diff-file-nav-path')?.textContent).toBe('src/');

    fireEvent.click(item);

    expect(screen.getByText('+quoted added line')).toBeInTheDocument();
  });

  it('keeps " b/" inside a file name', () => {
    const { container } = show(SPACE_B_DIFF);
    const item = container.querySelector('.diff-file-nav-item') as HTMLElement;
    expect(item.querySelector('.diff-file-nav-name')?.textContent).toBe('data.cs');
    expect(item.querySelector('.diff-file-nav-path')?.textContent).toBe('Plan b/');

    fireEvent.click(item);

    expect(gutter(container, 'add').map(r => r.text)).toEqual(['+y']);
  });

  it('does not count the no-newline marker as a line', () => {
    const { container } = show(NO_NEWLINE_DIFF);
    expect(gutter(container, 'del')).toEqual([{ old: '1', new: '', text: '-a' }]);
    expect(gutter(container, 'add').map(r => r.new)).toEqual(['1', '2']);
    expect(gutter(container, 'ctx')).toEqual([]);
  });

  it('reads hunk lines by the hunk counts, not by a leading "---" or "+++"', () => {
    const { container } = show(DASHED_CONTENT_DIFF);
    expect(gutter(container, 'del')).toEqual([{ old: '1', new: '', text: '--- old comment' }]);
    expect(gutter(container, 'add')).toEqual([{ old: '', new: '2', text: '++++' }]);
    expect(container.querySelector('.diff-file-nav-add')?.textContent).toBe('+1');
    expect(container.querySelector('.diff-file-nav-del')?.textContent).toBe('-1');
  });
});
