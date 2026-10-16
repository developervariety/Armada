import { describe, expect, it } from 'vitest';
import type { Vessel, WorkspaceTreeEntry } from '../types/models';
import {
  buildWorkspaceAskDraft,
  collapseWorkspacePathMap,
  createDraftFile,
  getExpandedWorkspacePaths,
  isWorkspacePathInScope,
  pruneWorkspaceEntryDirectoryMap,
  pruneWorkspacePathMap,
  remapWorkspaceEntryDirectoryMap,
  remapWorkspaceScopedPath,
  remapWorkspaceStringMap,
  sortWorkspacePathsByDepth,
} from './workspace';

function entry(relativePath: string, isDirectory = false): WorkspaceTreeEntry {
  return { name: relativePath.split('/').pop() ?? relativePath, relativePath, isDirectory, isEditable: !isDirectory, sizeBytes: null, lastWriteUtc: '' };
}

describe('workspace path scope helpers', () => {
  it('scopes and remaps paths below a renamed folder', () => {
    expect(isWorkspacePathInScope('src/a.ts', 'src')).toBe(true);
    expect(isWorkspacePathInScope('srcx/a.ts', 'src')).toBe(false);
    expect(remapWorkspaceScopedPath('src/a.ts', 'src', 'lib')).toBe('lib/a.ts');
    expect(remapWorkspaceScopedPath('docs/a.md', 'src', 'lib')).toBe('docs/a.md');
    expect(remapWorkspaceStringMap({ 'src/a.ts': 'x' }, 'src', 'lib')).toEqual({ 'lib/a.ts': 'x' });
  });

  it('prunes and collapses expanded folders', () => {
    const expanded = { '': true, src: true, 'src/x': true, docs: true };
    expect(pruneWorkspacePathMap(expanded, 'src')).toEqual({ '': true, docs: true });
    expect(collapseWorkspacePathMap(expanded, '')).toEqual({ '': true, docs: true, src: true, 'src/x': true });
    expect(getExpandedWorkspacePaths({ '': false, a: true, b: false })).toEqual(['', 'a']);
    expect(sortWorkspacePathsByDepth(['a/b/c', '/a/', 'z', 'a/b', 'a'])).toEqual(['a', 'z', 'a/b', 'a/b/c']);
  });

  it('remaps and prunes tree entries', () => {
    const tree = { '': [entry('src', true), entry('README.md')], src: [entry('src/a.ts')] };
    const renamed = remapWorkspaceEntryDirectoryMap(tree, 'src', 'lib');
    expect(Object.keys(renamed)).toEqual(['', 'lib']);
    expect(renamed[''][0]).toMatchObject({ name: 'lib', relativePath: 'lib' });
    expect(renamed.lib[0]).toMatchObject({ name: 'a.ts', relativePath: 'lib/a.ts' });
    expect(pruneWorkspaceEntryDirectoryMap(tree, 'src')).toEqual({ '': [entry('README.md')] });
  });

  it('creates an editable draft file', () => {
    expect(createDraftFile('/docs/new.md')).toMatchObject({ path: 'docs/new.md', name: 'new.md', content: '', isEditable: true, language: 'markdown' });
  });
});

describe('buildWorkspaceAskDraft', () => {
  const vessel = { id: 'vsl_1', name: 'Timestamps' } as Vessel;

  it('names the vessel, its checkout, and the selected files, and leaves a line for the question', () => {
    expect(buildWorkspaceAskDraft(vessel, ['src/a.cs', 'README.md'], '/Users/me/Code/Timestamps', 'laptop')).toBe(
      'About vessel "Timestamps" (vsl_1) (checked out at /Users/me/Code/Timestamps on Harbor laptop):\n\nFiles:\n- src/a.cs\n- README.md\n\n',
    );
  });

  it('works without a checkout or files', () => {
    expect(buildWorkspaceAskDraft(vessel, [], null, null)).toBe('About vessel "Timestamps" (vsl_1):\n\n');
  });
});
