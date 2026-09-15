import { describe, expect, it } from 'vitest';
import clientSource from './client.ts?raw';

// Every Armada.Server source file, as text. Routes register as app.<Verb>("path") or app.<Verb><T>("path").
const serverSources = import.meta.glob('../../../Armada.Server/**/*.cs', { query: '?raw', import: 'default', eager: true }) as Record<string, string>;

interface ClientCall {
  method: string;
  path: string;
  segments: string[];
}

// Replace each ${...} expression (braces may nest) with {p}.
function replaceTemplateExpressions(template: string): string {
  let result = '';
  let i = 0;
  while (i < template.length) {
    if (template[i] === '$' && template[i + 1] === '{') {
      let depth = 1;
      let j = i + 2;
      while (j < template.length && depth > 0) {
        if (template[j] === '{') depth++;
        else if (template[j] === '}') depth--;
        j++;
      }
      result += '{p}';
      i = j;
    } else {
      result += template[i];
      i++;
    }
  }
  return result;
}

function normalizePath(raw: string): string {
  let path = replaceTemplateExpressions(raw).split('?')[0];
  // A placeholder glued to the end of a segment is a query-string builder, not a path parameter.
  path = path.replace(/([^/]){p}$/, '$1');
  return path.replace(/\/+$/, '');
}

// A type argument list with at most one level of nesting, such as <Record<string, unknown>>.
const typeArguments = '<(?:[^<>]|<[^<>]*>)*>';

function clientCalls(): ClientCall[] {
  const calls: ClientCall[] = [];
  const helper = new RegExp(`\\b(get|post|put|del)${typeArguments}\\(\\s*(['\`])([\\s\\S]*?)\\2`, 'g');
  const verbs: Record<string, string> = { get: 'GET', post: 'POST', put: 'PUT', del: 'DELETE' };
  for (const match of clientSource.matchAll(helper)) {
    const path = normalizePath(match[3]);
    calls.push({ method: verbs[match[1]], path, segments: path.split('/') });
  }
  const direct = new RegExp(`\\brequest${typeArguments}\\(\\s*'([A-Z]+)',\\s*(['\`])([\\s\\S]*?)\\2`, 'g');
  for (const match of clientSource.matchAll(direct)) {
    const path = normalizePath(match[3]);
    calls.push({ method: match[1], path, segments: path.split('/') });
  }
  return calls.filter((call) => call.path.startsWith('/api/'));
}

function serverRoutes(): ClientCall[] {
  const routes: ClientCall[] = [];
  const registration = new RegExp(`\\bapp\\.(Get|Post|Put|Patch|Delete)(?:${typeArguments})?\\(\\s*"([^"]+)"`, 'g');
  for (const source of Object.values(serverSources)) {
    for (const match of source.matchAll(registration)) {
      const path = match[2].replace(/\{[^}]+\}/g, '{p}').replace(/\/+$/, '');
      routes.push({ method: match[1].toUpperCase(), path, segments: path.split('/') });
    }
  }
  return routes;
}

function matches(call: ClientCall, route: ClientCall): boolean {
  if (call.method !== route.method || call.segments.length !== route.segments.length) return false;
  return call.segments.every((segment, index) => segment === route.segments[index] || segment === '{p}' || route.segments[index] === '{p}');
}

describe('dashboard API client routes', () => {
  it('reads the server route registrations and parses every client request', () => {
    expect(Object.keys(serverSources).length).toBeGreaterThan(0);
    expect(serverRoutes().length).toBeGreaterThan(100);

    // Every typed helper call must be parsed; a call the patterns miss would hide a dead endpoint.
    const helperCalls = (clientSource.match(/\b(get|post|put|del|request)</g) ?? []).length;
    const parsed = clientCalls().length;
    // Not calls: the four helper definitions, and the helpers' own request<T>(method, path) forwarding calls.
    const helperDefinitions = (clientSource.match(/\bfunction (get|post|put|del)</g) ?? []).length;
    const genericForwarding = (clientSource.match(/\brequest<T>\(/g) ?? []).length;
    expect(parsed).toBe(helperCalls - helperDefinitions - genericForwarding);
  });

  it('calls only endpoints the server registers', () => {
    const routes = serverRoutes();
    const dead = clientCalls()
      .filter((call) => !routes.some((route) => matches(call, route)))
      .map((call) => `${call.method} ${call.path}`);

    expect([...new Set(dead)].sort()).toEqual([]);
  });

  it('calls the model endpoint and memory routes the server registers', () => {
    const routes = serverRoutes();
    const calls = clientCalls().filter((call) => call.path.startsWith('/api/v1/model-endpoints') || call.path.startsWith('/api/v1/memories'));
    const described = [...new Set(calls.map((call) => `${call.method} ${call.path}`))].sort();
    expect(described).toEqual([
      'DELETE /api/v1/memories/{p}',
      'DELETE /api/v1/model-endpoints/{p}',
      'GET /api/v1/memories',
      'GET /api/v1/memories/{p}',
      'GET /api/v1/model-endpoints',
      'GET /api/v1/model-endpoints/{p}',
      'POST /api/v1/model-endpoints',
      'POST /api/v1/model-endpoints/health-check',
      'POST /api/v1/model-endpoints/{p}/validate',
      'PUT /api/v1/model-endpoints/{p}',
    ]);
    for (const call of calls) {
      expect(routes.some((route) => matches(call, route))).toBe(true);
    }
  });
});
