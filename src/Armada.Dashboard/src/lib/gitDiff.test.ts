import { describe, it, expect } from 'vitest';
import { decodeQuotedPath, parseDiff, parseGitHeader } from './gitDiff';

describe('gitDiff', () => {
  it('decodes octal UTF-8 bytes and C escapes', () => {
    expect(decodeQuotedPath('"r\\303\\251sum\\303\\251.md"')).toBe('résumé.md');
    expect(decodeQuotedPath('"a\\tb\\"c"')).toBe('a\tb"c');
    expect(decodeQuotedPath('plain.txt')).toBe('plain.txt');
  });

  it('splits headers with one quoted side and with different names', () => {
    expect(parseGitHeader('a/plain.txt "b/Caf\\303\\251.txt"')).toEqual({ oldPath: 'plain.txt', newPath: 'Café.txt' });
    expect(parseGitHeader('a/old.txt b/new.txt')).toEqual({ oldPath: 'old.txt', newPath: 'new.txt' });
  });

  it('names a rename by its new path and a deletion by its old path', () => {
    const diff = [
      'diff --git a/x y b/x z',
      'similarity index 100%',
      'rename from x y',
      'rename to x z',
      'diff --git a/gone.txt b/gone.txt',
      'deleted file mode 100644',
      '--- a/gone.txt',
      '+++ /dev/null',
      '@@ -1 +0,0 @@',
      '-bye',
      '',
    ].join('\n');
    const files = parseDiff(diff).files;
    expect(files.map(f => [f.name, f.oldPath, f.newPath, f.deletions])).toEqual([
      ['x z', 'x y', 'x z', 0],
      ['gone.txt', 'gone.txt', null, 1],
    ]);
  });

  it('reads a plain unified diff without git headers', () => {
    const diff = ['--- a/one.txt', '+++ b/one.txt', '@@ -1 +1 @@', '-a', '+b', '--- a/two.txt', '+++ b/two.txt', '@@ -0,0 +1 @@', '+c'].join('\n');
    expect(parseDiff(diff).files.map(f => [f.name, f.additions, f.deletions])).toEqual([
      ['one.txt', 1, 1],
      ['two.txt', 1, 0],
    ]);
  });
});
