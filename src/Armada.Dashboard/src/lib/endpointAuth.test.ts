import { describe, expect, it } from 'vitest';
import catalogSource from '../../../Armada.Server/wwwroot/i18n/armada.json?raw';
import authSource from './endpointAuth.ts?raw';
import type { I18nCatalog } from '../i18n/runtime';
import type { ModelProvider } from '../types/models';
import {
  allowedAuthTypes,
  authFormFromEndpoint,
  authSecretLabel,
  authSecretPlaceholder,
  authSentAs,
  authSummary,
  buildEndpointAuthPayload,
  effectiveAuthType,
  newHeaderRow,
  validateEndpointAuth,
  type EndpointAuthForm,
} from './endpointAuth';

function form(patch: Partial<EndpointAuthForm> = {}): EndpointAuthForm {
  return { authType: 'ProviderDefault', authUsername: '', authHeaderName: '', authQueryParameter: '', headers: [], ...patch };
}

describe('endpoint auth types per provider', () => {
  it.each(['OllamaCloud', 'XAI'] as ModelProvider[])('%s requires bearer authentication', (provider) => {
    expect(allowedAuthTypes(provider)).toEqual(['ProviderDefault', 'Bearer']);
    expect(effectiveAuthType(provider, 'Basic')).toBe('ProviderDefault');
    expect(authSentAs(provider, 'ProviderDefault', form()).params?.wire).toBe('Authorization: Bearer <secret>');
  });
  it('signing providers accept only the provider default', () => {
    expect(allowedAuthTypes('VertexAI')).toEqual(['ProviderDefault']);
    expect(allowedAuthTypes('Bedrock')).toEqual(['ProviderDefault']);
    expect(allowedAuthTypes('Ollama')).toContain('Basic');
    expect(effectiveAuthType('Bedrock', 'Bearer')).toBe('ProviderDefault');
    expect(effectiveAuthType('Ollama', undefined)).toBe('ProviderDefault');
    expect(effectiveAuthType('Ollama', 'Header')).toBe('Header');
  });

  it('labels the secret for the chosen type, and hides it for None', () => {
    expect(authSecretLabel('Ollama', 'Bearer')).toBe('Bearer token');
    expect(authSecretLabel('Ollama', 'Basic')).toBe('Password');
    expect(authSecretLabel('OpenAI', 'Header')).toBe('Header value');
    expect(authSecretLabel('OpenAI', 'QueryParameter')).toBe('Query parameter value');
    expect(authSecretLabel('OpenAI', 'None')).toBeNull();
    expect(authSecretLabel('OpenAI', 'ProviderDefault')).toBe('API Key');
    expect(authSecretLabel('VertexAI', 'ProviderDefault')).toBe('Service Account JSON');
    expect(authSecretLabel('Bedrock', 'ProviderDefault')).toBe('AWS Secret Access Key');
    expect(authSecretPlaceholder('Ollama', 'Bearer', true)).toBe('(unchanged - leave blank to keep stored key)');
    expect(authSecretPlaceholder('Ollama', 'Bearer', false)).toBe('Paste the bearer token');
  });

  it('says exactly what is sent', () => {
    const wire = (provider: ModelProvider, type: Parameters<typeof authSentAs>[1], fields = {}) => authSentAs(provider, type, fields).params?.wire;
    expect(wire('Ollama', 'ProviderDefault')).toBe('Authorization: Bearer <secret>');
    expect(wire('Anthropic', 'ProviderDefault')).toBe('x-api-key: <secret>');
    expect(wire('Gemini', 'ProviderDefault')).toBe('x-goog-api-key: <secret>');
    expect(wire('AzureOpenAI', 'ProviderDefault')).toBe('api-key: <secret>');
    expect(wire('Ollama', 'Basic', { authUsername: 'svc' })).toBe('Authorization: Basic base64(svc:<secret>)');
    expect(wire('Ollama', 'Header', { authHeaderName: 'X-API-Key' })).toBe('X-API-Key: <secret>');
    expect(wire('Ollama', 'QueryParameter', { authQueryParameter: 'key' })).toBe('?key=<secret>');
    expect(authSentAs('Ollama', 'None', {}).key).toMatch(/^No credential is sent/);
    expect(authSentAs('Bedrock', 'ProviderDefault', {}).key).toMatch(/Signature Version 4/);
  });

  it('summarizes the credential for the list', () => {
    expect(authSummary({ provider: 'Ollama', authType: 'Header', authHeaderName: 'X-Key', authQueryParameter: null, authUsername: null }))
      .toEqual({ key: 'Header {{name}}', params: { name: 'X-Key' } });
    expect(authSummary({ provider: 'VertexAI', authType: 'Bearer', authHeaderName: null, authQueryParameter: null, authUsername: null }).key)
      .toBe('Provider default');
  });
});

describe('endpoint auth form and payload', () => {
  it('reads stored headers without values and keeps them on save when left blank', () => {
    const loaded = authFormFromEndpoint({
      provider: 'Ollama', authType: 'Bearer', authUsername: null, authHeaderName: null, authQueryParameter: null,
      headers: [{ name: 'CF-Access-Client-Id', value: null, hasValue: true }, { name: 'CF-Access-Client-Secret', value: null, hasValue: true }],
    });
    expect(loaded.headers.map((h) => [h.name, h.value, h.hasValue])).toEqual([
      ['CF-Access-Client-Id', '', true], ['CF-Access-Client-Secret', '', true],
    ]);
    loaded.headers[1].value = 'new-secret';
    expect(validateEndpointAuth('Ollama', loaded)).toBeNull();
    expect(buildEndpointAuthPayload('Ollama', loaded)).toEqual({
      authType: 'Bearer', authUsername: null, authHeaderName: null, authQueryParameter: null,
      headers: [{ name: 'CF-Access-Client-Id', value: null }, { name: 'CF-Access-Client-Secret', value: 'new-secret' }],
    });
  });

  it('a renamed stored header must get a new value (the stored one belongs to the old name)', () => {
    const loaded = authFormFromEndpoint({ provider: 'Ollama', headers: [{ name: 'X-A', value: null, hasValue: true }] });
    loaded.headers[0].name = 'x-a';
    expect(validateEndpointAuth('Ollama', loaded)).toBeNull();
    loaded.headers[0].name = 'X-B';
    expect(validateEndpointAuth('Ollama', loaded)?.key).toBe('Header "{{name}}" needs a value.');
  });

  it('sends only the fields of the chosen type and drops blank rows', () => {
    const row = { ...newHeaderRow(), name: ' X-Extra ', value: 'v' };
    const payload = buildEndpointAuthPayload('OpenAI', form({ authType: 'Basic', authUsername: ' svc ', authHeaderName: 'X', headers: [row, newHeaderRow()] }));
    expect(payload).toEqual({ authType: 'Basic', authUsername: 'svc', authHeaderName: null, authQueryParameter: null, headers: [{ name: 'X-Extra', value: 'v' }] });
    expect(buildEndpointAuthPayload('Bedrock', form({ authType: 'Header', authHeaderName: 'X' })).authType).toBe('ProviderDefault');
  });

  it('validates required names, duplicates and header syntax', () => {
    expect(validateEndpointAuth('OpenAI', form({ authType: 'Basic' }))?.key).toBe('Basic authentication needs a username.');
    expect(validateEndpointAuth('OpenAI', form({ authType: 'Header' }))?.key).toBe('Custom header authentication needs a header name.');
    expect(validateEndpointAuth('OpenAI', form({ authType: 'QueryParameter' }))?.key).toBe('Query parameter authentication needs a parameter name.');
    const a = { ...newHeaderRow(), name: 'X-A', value: '1' };
    const b = { ...newHeaderRow(), name: 'x-a', value: '2' };
    expect(validateEndpointAuth('OpenAI', form({ headers: [a, b] }))?.key).toBe('Header "{{name}}" is listed more than once.');
    expect(validateEndpointAuth('OpenAI', form({ headers: [{ ...a, name: 'Bad Name' }] }))?.key).toBe('Header name "{{name}}" is not a valid HTTP header name.');
    expect(validateEndpointAuth('OpenAI', form({ headers: [{ ...a, name: '' }] }))?.key).toBe('Every extra header needs a name.');
  });
});

const PAGE_KEYS = [
  'Authentication',
  'Authentication type',
  'How the secret is attached to each request. Provider default sends it the way this provider expects.',
  'Username',
  'Header name',
  'Query parameter name',
  'Extra headers',
  'Sent on every request, for example the two Cloudflare Access service token headers. Values are stored as secrets.',
  'Header {{index}} name',
  'Header {{index}} value',
  '(stored - leave blank to keep stored value)',
  'Value',
  'Remove this header. It stops being sent once you save.',
  'Remove',
  'Add a header that is sent on every request to this endpoint.',
  'Add header',
  'secret stored',
  'no secret stored',
  '{count, plural, one {# extra header} other {# extra headers}}',
];

describe('endpoint auth i18n catalog', () => {
  const catalog = JSON.parse(catalogSource) as I18nCatalog;
  const S = "'((?:[^'\\\\]|\\\\.)*)'";
  const keys = new Set<string>();
  // Every string literal the helper returns is a catalog key.
  for (const m of authSource.matchAll(new RegExp(`(?:return|key:)\\s*${S}`, 'g'))) keys.add(m[1]);
  for (const m of authSource.matchAll(new RegExp(`\\?\\s*${S}\\s*:\\s*${S}`, 'g'))) { keys.add(m[1]); keys.add(m[2]); }
  for (const k of ['ProviderDefault', 'None', 'Bearer', 'Basic', 'Header', 'QueryParameter', 'Authorization', 'x-api-key', 'x-goog-api-key', 'api-key']) keys.delete(k);
  // Strings the Endpoints page added for the authentication section, list column and detail view.
  for (const k of PAGE_KEYS) keys.add(k);

  it('finds the authentication strings', () => {
    expect(keys).toContain('Bearer token');
    expect(keys).toContain('Sent as: {{wire}}');
    expect(keys).toContain('Extra headers');
  });

  it('has a translation with intact placeholders for every string in every locale', () => {
    const problems: string[] = [];
    for (const meta of catalog.supportedLocales) {
      if (meta.code === catalog.defaultLocale) continue;
      const pack = catalog.locales[meta.code] ?? {};
      for (const key of keys) {
        const value = pack.phrases?.[key] ?? pack.terms?.[key] ?? pack.sections?.[key];
        if (!value) { problems.push(`${meta.code}: missing ${key}`); continue; }
        const want = (key.match(/\{\{\w+\}\}/g) ?? []).sort().join(',');
        const got = (value.match(/\{\{\w+\}\}/g) ?? []).sort().join(',');
        if (want !== got) problems.push(`${meta.code}: placeholders ${key}`);
      }
    }
    expect(problems).toEqual([]);
  });
});
