import type { Captain, MuxCaptainOptions } from '../types/models';

export const MUX_RUNTIME = 'Mux';

export interface MuxCaptainFormFields {
  muxConfigDirectory: string;
  muxEndpoint: string;
  muxBaseUrl: string;
  muxAdapterType: string;
  muxTemperature: string;
  muxMaxTokens: string;
  muxSystemPromptPath: string;
  muxApprovalPolicy: string;
}

export const EMPTY_MUX_CAPTAIN_FORM: MuxCaptainFormFields = {
  muxConfigDirectory: '',
  muxEndpoint: '',
  muxBaseUrl: '',
  muxAdapterType: '',
  muxTemperature: '',
  muxMaxTokens: '',
  muxSystemPromptPath: '',
  muxApprovalPolicy: '',
};

export function isMuxRuntime(runtime: string | null | undefined): boolean {
  return (runtime ?? '').trim() === MUX_RUNTIME;
}

export function parseMuxCaptainOptions(runtimeOptionsJson: string | null | undefined): MuxCaptainOptions | null {
  if (!runtimeOptionsJson) return null;

  try {
    const parsed = JSON.parse(runtimeOptionsJson) as Partial<MuxCaptainOptions>;
    return {
      schemaVersion: parsed.schemaVersion ?? 1,
      configDirectory: parsed.configDirectory ?? null,
      endpoint: parsed.endpoint ?? null,
      baseUrl: parsed.baseUrl ?? null,
      adapterType: parsed.adapterType ?? null,
      temperature: typeof parsed.temperature === 'number' ? parsed.temperature : null,
      maxTokens: typeof parsed.maxTokens === 'number' ? parsed.maxTokens : null,
      systemPromptPath: parsed.systemPromptPath ?? null,
      approvalPolicy: parsed.approvalPolicy ?? null,
    };
  } catch {
    return null;
  }
}

export function muxFormFromCaptain(captain: Pick<Captain, 'runtime' | 'runtimeOptionsJson'> | null | undefined): MuxCaptainFormFields {
  const options = parseMuxCaptainOptions(captain?.runtimeOptionsJson);
  if (!captain || !isMuxRuntime(captain.runtime) || !options) {
    return { ...EMPTY_MUX_CAPTAIN_FORM };
  }

  return {
    muxConfigDirectory: options.configDirectory ?? '',
    muxEndpoint: options.endpoint ?? '',
    muxBaseUrl: options.baseUrl ?? '',
    muxAdapterType: options.adapterType ?? '',
    muxTemperature: options.temperature != null ? String(options.temperature) : '',
    muxMaxTokens: options.maxTokens != null ? String(options.maxTokens) : '',
    muxSystemPromptPath: options.systemPromptPath ?? '',
    muxApprovalPolicy: options.approvalPolicy ?? '',
  };
}

export function buildMuxRuntimeOptionsJson(runtime: string, fields: MuxCaptainFormFields): string | null {
  if (!isMuxRuntime(runtime)) return null;

  const temperature = parseOptionalNumber(fields.muxTemperature);
  const maxTokens = parseOptionalInteger(fields.muxMaxTokens);

  const payload: MuxCaptainOptions = {
    schemaVersion: 1,
    configDirectory: normalize(fields.muxConfigDirectory),
    endpoint: normalize(fields.muxEndpoint),
    baseUrl: normalize(fields.muxBaseUrl),
    adapterType: normalize(fields.muxAdapterType),
    temperature,
    maxTokens,
    systemPromptPath: normalize(fields.muxSystemPromptPath),
    approvalPolicy: normalize(fields.muxApprovalPolicy),
  };

  return JSON.stringify(payload);
}

function normalize(value: string): string | null {
  const trimmed = value.trim();
  return trimmed ? trimmed : null;
}

function parseOptionalNumber(value: string): number | null {
  const trimmed = value.trim();
  if (!trimmed) return null;
  const parsed = Number(trimmed);
  return Number.isFinite(parsed) ? parsed : null;
}

function parseOptionalInteger(value: string): number | null {
  const trimmed = value.trim();
  if (!trimmed) return null;
  const parsed = Number.parseInt(trimmed, 10);
  return Number.isFinite(parsed) ? parsed : null;
}
