import type { Vessel } from '../types/models';

const VARIABLE_PATTERN = /\{\{\s*([^{}]*?)\s*\}\}/g;

const KNOWN = ['vessel.name', 'vessel.id', 'vessel.defaultbranch', 'vessel.workingdirectory', 'vessel.buildcommand', 'vessel.ecosystems', 'vessel.testcommand', 'vessel.lintcommand', 'vessel.auditcommand', 'health.summary', 'scan.summary'];

/** Return every `{{name}}` in the text that the server would reject (case-insensitive, de-duplicated). */
export function findUnknownTemplateVariables(text: string | null | undefined, inputNames: string[] = []): string[] {
  if (!text) return [];
  const unknown: string[] = [];
  for (const match of text.matchAll(VARIABLE_PATTERN)) {
    const name = match[1].trim();
    if (!KNOWN.includes(name.toLowerCase()) && !inputNames.some(n => `input.${n}`.toLowerCase() === name.toLowerCase()) && !unknown.includes(name)) unknown.push(name);
  }
  return unknown;
}

export interface TemplatePreview {
  text: string;
  /** True when the template references `{{health.summary}}`, which only the server can render. */
  usesHealthSummary: boolean;
  /** True when the template references `{{vessel.buildCommand}}` and the vessel has none (it would be skipped). */
  missingBuildCommand: boolean;
}

/**
 * Client-side approximation of the server's single-pass template renderer, used only for previews.
 * `{{health.summary}}` is replaced with `healthPlaceholder` because it is rendered on the server.
 */
export function renderTemplatePreview(text: string, vessel: Pick<Vessel, 'name' | 'id' | 'defaultBranch' | 'workingDirectory'> & { definitionOfDoneBuildCommand?: string | null; definitionOfDoneTestCommand?: string | null; testCommand?: string | null; lintCommand?: string | null; auditCommand?: string | null }, healthPlaceholder: string): TemplatePreview {
  let usesHealthSummary = false;
  let missingBuildCommand = false;
  const rendered = text.replace(VARIABLE_PATTERN, (whole, rawName: string) => {
    const name = rawName.trim().toLowerCase();
    switch (name) {
      case 'vessel.name': return vessel.name ?? '';
      case 'vessel.id': return vessel.id ?? '';
      case 'vessel.defaultbranch': return vessel.defaultBranch || 'main';
      case 'vessel.workingdirectory': return vessel.workingDirectory ?? '';
      case 'vessel.buildcommand': {
        const cmd = vessel.definitionOfDoneBuildCommand ?? '';
        if (!cmd) missingBuildCommand = true;
        return cmd;
      }
      // Ecosystems and resolved commands depend on the working tree, so only explicit vessel overrides preview;
      // otherwise the variable is left as written for the server to render.
      case 'vessel.testcommand': return vessel.testCommand || vessel.definitionOfDoneTestCommand || whole;
      case 'vessel.lintcommand': return vessel.lintCommand || whole;
      case 'vessel.auditcommand': return vessel.auditCommand || whole;
      case 'health.summary':
        usesHealthSummary = true;
        return healthPlaceholder;
      default:
        return whole;
    }
  });
  return { text: rendered, usesHealthSummary, missingBuildCommand };
}
