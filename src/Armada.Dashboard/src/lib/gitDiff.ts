/**
 * Reader for unified diffs shown in the dashboard. It follows the server's Git diff path rules:
 * C-quoted names ("a/Caf\303\251.cs") are decoded to the real name, an unquoted header whose name
 * contains " b/" is split at its middle when both sides name the same file, rename/copy lines and
 * the ---/+++ lines refine the header names, and hunk lines are classified by the hunk counts, so
 * content that starts with "---" or "+++" stays content and the "\ No newline" marker is no line.
 */

export type DiffLineKind = 'file-header' | 'hunk-header' | 'meta' | 'add' | 'del' | 'context' | 'no-newline';

export interface DiffLine {
  /** Index of this line in the split diff. */
  lineIndex: number;
  kind: DiffLineKind;
  text: string;
  /** Old-side line number for 'del' and 'context' lines. */
  oldNumber?: number;
  /** New-side line number for 'add' and 'context' lines. */
  newNumber?: number;
}

export interface DiffFileEntry {
  /** The post-change name, or the old name of a deleted file. */
  name: string;
  oldPath: string | null;
  newPath: string | null;
  additions: number;
  deletions: number;
  /** Index of the first line of this file's block in the split diff. */
  startLine: number;
  /** Index one past the last line of this file's block. */
  endLine: number;
}

export interface ParsedDiff {
  lines: DiffLine[];
  files: DiffFileEntry[];
}

interface Block {
  entry: DiffFileEntry;
  isNew: boolean;
  isDeleted: boolean;
  sawHunk: boolean;
  sawOldLine: boolean;
  sawNewLine: boolean;
}

function newBlock(startLine: number): Block {
  return {
    entry: { name: '', oldPath: null, newPath: null, additions: 0, deletions: 0, startLine, endLine: startLine },
    isNew: false,
    isDeleted: false,
    sawHunk: false,
    sawOldLine: false,
    sawNewLine: false,
  };
}

function isOctal(c: string): boolean {
  return c >= '0' && c <= '7';
}

const ESCAPES: Record<string, number> = { a: 7, b: 8, t: 9, n: 10, v: 11, f: 12, r: 13, '"': 34, '\\': 92 };

/** Read a quoted name starting at value[start] (the opening quote). Returns the name and the index after the closing quote. */
function readQuoted(value: string, start: number): { name: string; next: number } {
  const bytes: number[] = [];
  const encoder = new TextEncoder();
  let index = start + 1;
  while (index < value.length) {
    const c = value[index];
    if (c === '"') {
      index++;
      break;
    }
    if (c !== '\\' || index + 1 >= value.length) {
      const code = value.codePointAt(index) ?? 0;
      const ch = String.fromCodePoint(code);
      bytes.push(...encoder.encode(ch));
      index += ch.length;
      continue;
    }
    const escape = value[index + 1];
    if (escape >= '0' && escape <= '3' && index + 3 < value.length && isOctal(value[index + 2]) && isOctal(value[index + 3])) {
      bytes.push(parseInt(value.substring(index + 1, index + 4), 8));
      index += 4;
      continue;
    }
    const mapped = ESCAPES[escape];
    if (mapped === undefined) {
      bytes.push(92);
      index++;
      continue;
    }
    bytes.push(mapped);
    index += 2;
  }
  return { name: new TextDecoder('utf-8').decode(new Uint8Array(bytes)), next: index };
}

/** Decode a Git C-quoted name. A value not wrapped in double quotes is returned unchanged. */
export function decodeQuotedPath(value: string): string {
  if (value.length < 2 || value[0] !== '"' || value[value.length - 1] !== '"') return value;
  return readQuoted(value, 0).name;
}

function stripPrefix(token: string | null, prefix: string): string | null {
  if (!token || token === '/dev/null') return null;
  return token.startsWith(prefix) ? token.substring(prefix.length) : token;
}

function parsePathField(value: string, prefix: string): string | null {
  let name: string;
  if (value.startsWith('"')) {
    name = readQuoted(value, 0).name;
  } else {
    // Git ends an unquoted name that contains a space with a tab; a real tab is always quoted.
    const tab = value.indexOf('\t');
    name = tab >= 0 ? value.substring(0, tab) : value;
  }
  if (name === '/dev/null') return null;
  if (name.startsWith(prefix)) name = name.substring(prefix.length);
  return name.length === 0 ? null : name;
}

function splitSameNameHeader(remainder: string): [string, string] | null {
  // "a/<name> b/<name>" with an unquoted name that may contain spaces: when both sides name the
  // same file the split point is the exact middle.
  if (remainder.length < 5 || remainder.length % 2 === 0) return null;
  const middle = Math.floor(remainder.length / 2);
  if (remainder[middle] !== ' ') return null;
  const left = remainder.substring(0, middle);
  const right = remainder.substring(middle + 1);
  if (!left.startsWith('a/') || !right.startsWith('b/')) return null;
  if (left.substring(2) !== right.substring(2)) return null;
  return [left, right];
}

/** Old and new path from the text after "diff --git ". */
export function parseGitHeader(remainder: string): { oldPath: string | null; newPath: string | null } {
  let first: string | null = null;
  let second: string | null = null;
  if (remainder.startsWith('"')) {
    const read = readQuoted(remainder, 0);
    first = read.name;
    const rest = read.next < remainder.length ? remainder.substring(read.next).replace(/^ +/, '') : '';
    second = rest.startsWith('"') ? decodeQuotedPath(rest) : rest;
  } else {
    const quotedSecond = remainder.indexOf(' "');
    const sameName = splitSameNameHeader(remainder);
    if (quotedSecond >= 0 && remainder.endsWith('"')) {
      first = remainder.substring(0, quotedSecond);
      second = decodeQuotedPath(remainder.substring(quotedSecond + 1));
    } else if (sameName) {
      [first, second] = sameName;
    } else {
      const split = remainder.indexOf(' b/');
      if (split >= 0) {
        first = remainder.substring(0, split);
        second = remainder.substring(split + 1);
      } else {
        first = remainder;
      }
    }
  }
  return { oldPath: stripPrefix(first, 'a/'), newPath: stripPrefix(second, 'b/') };
}

function hunkRange(part: string): { start: number; count: number } {
  const [start, count] = part.split(',');
  return { start: parseInt(start, 10) || 0, count: count === undefined ? 1 : parseInt(count, 10) || 0 };
}

const META_PREFIXES = [
  'index ', 'new file', 'deleted file', 'old mode', 'new mode', 'similarity index', 'dissimilarity index',
  'rename from', 'rename to', 'copy from', 'copy to', 'Binary files', 'GIT binary patch',
];

/** Parse a unified diff into classified, numbered lines and one entry per file. */
export function parseDiff(rawDiff: string): ParsedDiff {
  const lines: DiffLine[] = [];
  const files: DiffFileEntry[] = [];
  if (!rawDiff) return { lines, files };

  let current: Block | null = null;
  let oldRemaining = 0;
  let newRemaining = 0;
  let oldNumber = 0;
  let newNumber = 0;

  const flush = (endLine: number) => {
    if (!current) return;
    const { entry } = current;
    entry.endLine = endLine;
    if (current.isNew) entry.oldPath = null;
    if (current.isDeleted) entry.newPath = null;
    const name = entry.newPath ?? entry.oldPath;
    if (name) {
      entry.name = name;
      files.push(entry);
    }
    current = null;
  };

  const rawLines = rawDiff.split('\n');
  rawLines.forEach((rawLine, i) => {
    // The newline that ends the last line is not a line of its own.
    if (i === rawLines.length - 1 && rawLine === '') return;
    const text = rawLine.endsWith('\r') ? rawLine.substring(0, rawLine.length - 1) : rawLine;

    if (text.startsWith('diff --git ')) {
      flush(i);
      current = newBlock(i);
      const header = parseGitHeader(text.substring('diff --git '.length));
      current.entry.oldPath = header.oldPath;
      current.entry.newPath = header.newPath;
      oldRemaining = 0;
      newRemaining = 0;
      lines.push({ lineIndex: i, kind: 'file-header', text: rawLine });
      return;
    }

    if (oldRemaining > 0 || newRemaining > 0) {
      const block = current as Block | null;
      if (text.length === 0 || text[0] === ' ') {
        lines.push({ lineIndex: i, kind: 'context', text: rawLine, oldNumber, newNumber });
        oldNumber++;
        newNumber++;
        oldRemaining--;
        newRemaining--;
        return;
      }
      if (text[0] === '-') {
        lines.push({ lineIndex: i, kind: 'del', text: rawLine, oldNumber });
        oldNumber++;
        oldRemaining--;
        if (block) block.entry.deletions++;
        return;
      }
      if (text[0] === '+') {
        lines.push({ lineIndex: i, kind: 'add', text: rawLine, newNumber });
        newNumber++;
        newRemaining--;
        if (block) block.entry.additions++;
        return;
      }
      if (text[0] === '\\') {
        lines.push({ lineIndex: i, kind: 'no-newline', text: rawLine });
        return;
      }
      // A line that cannot belong to the hunk ends it; read it as header text.
      oldRemaining = 0;
      newRemaining = 0;
    }

    if (text.startsWith('@@')) {
      const match = text.match(/^@@+ -(\d+(?:,\d+)?) \+(\d+(?:,\d+)?)/);
      if (match) {
        const oldRange = hunkRange(match[1]);
        const newRange = hunkRange(match[2]);
        oldNumber = oldRange.start;
        newNumber = newRange.start;
        oldRemaining = oldRange.count;
        newRemaining = newRange.count;
      }
      if (current) (current as Block).sawHunk = true;
      lines.push({ lineIndex: i, kind: 'hunk-header', text: rawLine });
      return;
    }

    if (text.startsWith('--- ') || text.startsWith('+++ ')) {
      const isOld = text.startsWith('--- ');
      let block = current as Block | null;
      if (!block || block.sawHunk || (isOld ? block.sawOldLine : block.sawNewLine)) {
        flush(i);
        block = newBlock(i);
        current = block;
      }
      const path = parsePathField(text.substring(4), isOld ? 'a/' : 'b/');
      if (isOld) {
        block.sawOldLine = true;
        if (path === null) block.isNew = true;
        else block.entry.oldPath = path;
      } else {
        block.sawNewLine = true;
        if (path === null) block.isDeleted = true;
        else block.entry.newPath = path;
      }
      lines.push({ lineIndex: i, kind: 'meta', text: rawLine });
      return;
    }

    const block = current as Block | null;
    if (block) {
      if (text.startsWith('rename from ') || text.startsWith('copy from ')) {
        block.entry.oldPath = decodeQuotedPath(text.substring(text.indexOf(' from ') + 6));
      } else if (text.startsWith('rename to ') || text.startsWith('copy to ')) {
        block.entry.newPath = decodeQuotedPath(text.substring(text.indexOf(' to ') + 4));
      } else if (text.startsWith('new file mode')) {
        block.isNew = true;
      } else if (text.startsWith('deleted file mode')) {
        block.isDeleted = true;
      }
    }

    const isMeta = META_PREFIXES.some(prefix => text.startsWith(prefix));
    lines.push({ lineIndex: i, kind: isMeta ? 'meta' : 'context', text: rawLine });
  });

  flush(rawLines.length);
  return { lines, files };
}
