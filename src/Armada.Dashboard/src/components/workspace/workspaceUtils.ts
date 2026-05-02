import type { Vessel, WorkspaceFileResponse } from '../../types/models';

export function normalizeWorkspacePath(path?: string | null): string {
  return (path || '').replace(/\\/g, '/').replace(/^\/+|\/+$/g, '');
}

export function getWorkspaceName(path: string): string {
  const normalized = normalizeWorkspacePath(path);
  if (!normalized) return '';
  const parts = normalized.split('/');
  return parts[parts.length - 1] || normalized;
}

export function getWorkspaceParentPath(path: string): string {
  const normalized = normalizeWorkspacePath(path);
  if (!normalized.includes('/')) return '';
  return normalized.substring(0, normalized.lastIndexOf('/'));
}

export function inferWorkspaceLanguage(path: string): string {
  const normalized = normalizeWorkspacePath(path).toLowerCase();
  if (normalized.endsWith('.cs')) return 'csharp';
  if (normalized.endsWith('.csproj') || normalized.endsWith('.xml')) return 'xml';
  if (normalized.endsWith('.md')) return 'markdown';
  if (normalized.endsWith('.json')) return 'json';
  if (normalized.endsWith('.ts') || normalized.endsWith('.tsx')) return 'typescript';
  if (normalized.endsWith('.js') || normalized.endsWith('.jsx')) return 'javascript';
  if (normalized.endsWith('.css')) return 'css';
  if (normalized.endsWith('.html')) return 'html';
  if (normalized.endsWith('.yml') || normalized.endsWith('.yaml')) return 'yaml';
  if (normalized.endsWith('.ps1')) return 'powershell';
  if (normalized.endsWith('.bat')) return 'bat';
  if (normalized.endsWith('.sql')) return 'sql';
  return 'plaintext';
}

export function buildScopedFileDirective(paths: string[]): string {
  const normalized = paths.map(normalizeWorkspacePath).filter(Boolean);
  if (!normalized.length) return '';
  return `Touch only ${normalized.join(', ')}`;
}

export function buildWorkspacePlanningDraft(vessel: Vessel, paths: string[]): { title: string; prompt: string } {
  const directive = buildScopedFileDirective(paths);
  const primary = getWorkspaceName(paths[0] || vessel.name);
  const title = `Plan ${primary}`;
  const prompt = [
    directive,
    '',
    `Help me plan the changes needed in vessel "${vessel.name}".`,
    'Review the selected files, identify likely dependencies, and outline a concrete implementation approach before dispatch.',
    'Call out risks, affected areas, and any follow-up files that may need to be touched if the current scope is too narrow.',
  ].filter(Boolean).join('\n');

  return { title, prompt };
}

export function buildWorkspaceDispatchDraft(vessel: Vessel, paths: string[]): { title: string; prompt: string } {
  const directive = buildScopedFileDirective(paths);
  const primary = getWorkspaceName(paths[0] || vessel.name);
  const title = `Workspace: ${primary}`;
  const prompt = [
    directive,
    '',
    `Implement the requested change in vessel "${vessel.name}".`,
    'Use the selected files as the primary scope. If adjacent files are required, expand carefully and explain why.',
    'Update tests and documentation when the change requires it.',
  ].filter(Boolean).join('\n');

  return { title, prompt };
}

export function buildWorkspaceContextSnippet(files: Array<Pick<WorkspaceFileResponse, 'path' | 'content'>>): string {
  const sections = files.map((file) => [
    `### ${file.path}`,
    '```text',
    file.content.trim(),
    '```',
  ].join('\n'));

  return [
    '## Workspace Selection',
    '',
    ...sections,
  ].join('\n');
}
