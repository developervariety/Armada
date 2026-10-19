import type { ModelEndpoint, ModelEndpointAuthType, ModelEndpointHeader, ModelProvider } from '../types/models';

/**
 * Model endpoint authentication: which auth types a provider accepts, what the form calls the secret, exactly what
 * goes on the wire, and the create/update payload for the auth fields and extra headers. Pure; strings returned
 * here are catalog keys (with params) that the page translates.
 */

export const ENDPOINT_AUTH_TYPES: ModelEndpointAuthType[] = ['ProviderDefault', 'None', 'Bearer', 'Basic', 'Header', 'QueryParameter'];

/** A catalog key plus the params to interpolate. */
export interface LocalizedText {
  key: string;
  params?: Record<string, string | number>;
}

/** One row of the extra-headers editor. */
export interface EndpointHeaderRow {
  /** Stable React key. */
  id: string;
  name: string;
  value: string;
  /** The server holds a value for originalName. */
  hasValue: boolean;
  /** The name the server stored the value under ('' for a row added in this form). */
  originalName: string;
}

/** The auth part of the endpoint form. */
export interface EndpointAuthForm {
  authType: ModelEndpointAuthType;
  authUsername: string;
  authHeaderName: string;
  authQueryParameter: string;
  headers: EndpointHeaderRow[];
}

/** The auth part of a create/update payload. */
export interface EndpointAuthPayload {
  authType: ModelEndpointAuthType;
  authUsername: string | null;
  authHeaderName: string | null;
  authQueryParameter: string | null;
  headers: ModelEndpointHeader[];
}

/** Vertex AI and Bedrock sign requests themselves, so only ProviderDefault applies. */
export function providerSignsRequests(provider: ModelProvider): boolean {
  return provider === 'VertexAI' || provider === 'Bedrock';
}

/** The auth types the provider accepts. */
export function allowedAuthTypes(provider: ModelProvider): ModelEndpointAuthType[] {
  if (provider === 'OllamaCloud' || provider === 'XAI') return ['ProviderDefault', 'Bearer'];
  return providerSignsRequests(provider) ? ['ProviderDefault'] : ENDPOINT_AUTH_TYPES;
}

/** The auth type that will actually apply (an older server may omit it; signing providers force the default). */
export function effectiveAuthType(provider: ModelProvider, authType: ModelEndpointAuthType | null | undefined): ModelEndpointAuthType {
  if (!allowedAuthTypes(provider).includes(authType ?? 'ProviderDefault')) return 'ProviderDefault';
  return authType && ENDPOINT_AUTH_TYPES.includes(authType) ? authType : 'ProviderDefault';
}

/** Option label for an auth type (catalog key). */
export function authTypeLabel(authType: ModelEndpointAuthType): string {
  switch (authType) {
    case 'None': return 'None (no credential)';
    case 'Bearer': return 'Bearer token';
    case 'Basic': return 'Basic (username and password)';
    case 'Header': return 'Custom header';
    case 'QueryParameter': return 'Query parameter';
    default: return 'Provider default';
  }
}

/** True when the auth type sends a secret (so the form shows the secret box). */
export function authUsesSecret(authType: ModelEndpointAuthType): boolean {
  return authType !== 'None';
}

/** Label of the secret box (catalog key), or null when no secret is sent. */
export function authSecretLabel(provider: ModelProvider, authType: ModelEndpointAuthType): string | null {
  switch (effectiveAuthType(provider, authType)) {
    case 'None': return null;
    case 'Bearer': return 'Bearer token';
    case 'Basic': return 'Password';
    case 'Header': return 'Header value';
    case 'QueryParameter': return 'Query parameter value';
    default:
      if (provider === 'VertexAI') return 'Service Account JSON';
      if (provider === 'Bedrock') return 'AWS Secret Access Key';
      return 'API Key';
  }
}

/** Placeholder of the secret box (catalog key). */
export function authSecretPlaceholder(provider: ModelProvider, authType: ModelEndpointAuthType, hasStoredSecret: boolean): string {
  if (hasStoredSecret) {
    return provider === 'VertexAI'
      ? '(unchanged - leave blank to keep stored credential)'
      : '(unchanged - leave blank to keep stored key)';
  }
  switch (effectiveAuthType(provider, authType)) {
    case 'Bearer': return 'Paste the bearer token';
    case 'Basic': return 'Password for the username above';
    case 'Header': return 'Value sent in the header';
    case 'QueryParameter': return 'Value sent in the query parameter';
    default: return provider === 'VertexAI' ? 'Paste the service-account JSON' : 'Optional';
  }
}

/** The header the provider's default scheme uses for the secret, or null when it is not a plain header. */
export function providerDefaultHeader(provider: ModelProvider): string | null {
  switch (provider) {
    case 'Anthropic': return 'x-api-key';
    case 'Gemini': return 'x-goog-api-key';
    case 'AzureOpenAI': return 'api-key';
    case 'VertexAI':
    case 'Bedrock': return null;
    default: return 'Authorization';
  }
}

/**
 * Exactly what is sent, as helper text under the secret box. `fields` supplies the typed username, header name and
 * query parameter so the text shows the real wire format; blanks fall back to placeholders.
 */
export function authSentAs(
  provider: ModelProvider,
  authType: ModelEndpointAuthType,
  fields: { authUsername?: string; authHeaderName?: string; authQueryParameter?: string },
): LocalizedText {
  const type = effectiveAuthType(provider, authType);
  switch (type) {
    case 'None': return { key: 'No credential is sent. Extra headers below are still sent.' };
    case 'Bearer': return { key: 'Sent as: {{wire}}', params: { wire: 'Authorization: Bearer <secret>' } };
    case 'Basic': {
      const user = fields.authUsername?.trim() || '<username>';
      return { key: 'Sent as: {{wire}}', params: { wire: `Authorization: Basic base64(${user}:<secret>)` } };
    }
    case 'Header': {
      const header = fields.authHeaderName?.trim() || '<header name>';
      return { key: 'Sent as: {{wire}}', params: { wire: `${header}: <secret>` } };
    }
    case 'QueryParameter': {
      const parameter = fields.authQueryParameter?.trim() || '<parameter>';
      return { key: 'Sent on every request URL as: {{wire}}', params: { wire: `?${parameter}=<secret>` } };
    }
    default:
      if (provider === 'VertexAI') return { key: 'Exchanged for an OAuth access token from the service-account JSON, sent as: {{wire}}', params: { wire: 'Authorization: Bearer <token>' } };
      if (provider === 'Bedrock') return { key: 'Requests are signed with AWS Signature Version 4 using the access key ID and this secret access key.' };
      if (providerDefaultHeader(provider) === 'Authorization') return { key: 'Sent as: {{wire}}', params: { wire: 'Authorization: Bearer <secret>' } };
      return { key: 'Sent as: {{wire}}', params: { wire: `${providerDefaultHeader(provider)}: <secret>` } };
  }
}

/** Short credential summary for the list and detail views. */
export function authSummary(endpoint: Pick<ModelEndpoint, 'provider' | 'authType' | 'authHeaderName' | 'authQueryParameter' | 'authUsername'>): LocalizedText {
  const type = effectiveAuthType(endpoint.provider, endpoint.authType);
  switch (type) {
    case 'None': return { key: 'None' };
    case 'Bearer': return { key: 'Bearer token' };
    case 'Basic': return endpoint.authUsername
      ? { key: 'Basic ({{user}})', params: { user: endpoint.authUsername } }
      : { key: 'Basic authentication' };
    case 'Header': return { key: 'Header {{name}}', params: { name: endpoint.authHeaderName || '?' } };
    case 'QueryParameter': return { key: 'Query parameter {{name}}', params: { name: endpoint.authQueryParameter || '?' } };
    default: return { key: 'Provider default' };
  }
}

let rowCounter = 0;

/** A fresh, empty header row. */
export function newHeaderRow(): EndpointHeaderRow {
  rowCounter += 1;
  return { id: `hdr-${rowCounter}`, name: '', value: '', hasValue: false, originalName: '' };
}

/** The auth form for an endpoint being edited (or the defaults for a new one). */
export function authFormFromEndpoint(endpoint: Partial<ModelEndpoint> | null): EndpointAuthForm {
  const provider = (endpoint?.provider ?? 'OpenAI') as ModelProvider;
  return {
    authType: effectiveAuthType(provider, endpoint?.authType),
    authUsername: endpoint?.authUsername ?? '',
    authHeaderName: endpoint?.authHeaderName ?? '',
    authQueryParameter: endpoint?.authQueryParameter ?? '',
    headers: (endpoint?.headers ?? []).map((header) => {
      const row = newHeaderRow();
      return { ...row, name: header.name, hasValue: header.hasValue === true, originalName: header.name };
    }),
  };
}

/** True when a row's blank value keeps the server's stored value (same name, case-insensitive, and one is stored). */
export function headerKeepsStoredValue(row: EndpointHeaderRow): boolean {
  return row.hasValue && row.value === '' && row.originalName.trim().toLowerCase() === row.name.trim().toLowerCase();
}

/** The first problem with the auth form (catalog key with params), or null when it can be saved. */
export function validateEndpointAuth(provider: ModelProvider, form: EndpointAuthForm): LocalizedText | null {
  const type = effectiveAuthType(provider, form.authType);
  if (type === 'Basic' && !form.authUsername.trim()) return { key: 'Basic authentication needs a username.' };
  if (type === 'Header' && !form.authHeaderName.trim()) return { key: 'Custom header authentication needs a header name.' };
  if (type === 'QueryParameter' && !form.authQueryParameter.trim()) return { key: 'Query parameter authentication needs a parameter name.' };

  const seen = new Set<string>();
  for (const row of form.headers) {
    const name = row.name.trim();
    if (!name) {
      if (row.value !== '') return { key: 'Every extra header needs a name.' };
      continue;
    }
    if (!/^[!#$%&'*+.^_`|~0-9A-Za-z-]+$/.test(name)) return { key: 'Header name "{{name}}" is not a valid HTTP header name.', params: { name } };
    const lower = name.toLowerCase();
    if (seen.has(lower)) return { key: 'Header "{{name}}" is listed more than once.', params: { name } };
    seen.add(lower);
    if (row.value === '' && !headerKeepsStoredValue(row)) return { key: 'Header "{{name}}" needs a value.', params: { name } };
  }
  return null;
}

/**
 * The auth fields of a create/update payload. Fields that do not apply to the chosen type are sent as null. Every
 * named header row is sent (the list replaces the stored one); a row left blank that keeps its stored name sends
 * value null so the server keeps the stored value.
 */
export function buildEndpointAuthPayload(provider: ModelProvider, form: EndpointAuthForm): EndpointAuthPayload {
  const type = effectiveAuthType(provider, form.authType);
  return {
    authType: type,
    authUsername: type === 'Basic' ? form.authUsername.trim() || null : null,
    authHeaderName: type === 'Header' ? form.authHeaderName.trim() || null : null,
    authQueryParameter: type === 'QueryParameter' ? form.authQueryParameter.trim() || null : null,
    headers: form.headers
      .filter((row) => row.name.trim() !== '')
      .map((row) => ({ name: row.name.trim(), value: headerKeepsStoredValue(row) ? null : row.value })),
  };
}
