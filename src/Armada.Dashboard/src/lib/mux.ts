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

/**
 * Approval policies a Mux captain can use. Captains run `mux print`, which is non-interactive: it accepts only auto
 * (sent as --yolo) or deny. There is nobody to answer an "ask" prompt, so ask is not offered.
 */
export const MUX_APPROVAL_POLICIES: string[] = ['auto', 'autoapprove', 'deny'];

/** The approval policy saved for "ask", which `mux print` rejects: nothing can answer the prompt, so it denies. */
export const MUX_ASK_SAVED_AS = 'deny';

/** English warning shown when a captain still has the unsupported "ask" policy (translate with t()). */
export const MUX_APPROVAL_ASK_WARNING = 'This captain uses ask, which mux print does not accept because nobody can answer its prompts. Saving changes it to deny; choose auto to run tools without prompts.';

/** True when the stored approval policy is the unsupported interactive "ask". */
export function isUnsupportedMuxApprovalPolicy(value: string | null | undefined): boolean {
  return (value ?? '').trim().toLowerCase() === 'ask';
}

/** Why a Mux override field cannot be saved (English, translate with t()), or null when it is valid or blank. */
export type MuxFieldError = 'muxTemperatureInvalid' | 'muxMaxTokensInvalid';

/** English message of a Mux field error. */
export function muxFieldErrorMessage(error: MuxFieldError): string {
  return error === 'muxTemperatureInvalid'
    ? 'Mux Temperature must be a number of 0 or more, for example 0.2, or blank.'
    : 'Mux Max Tokens must be a whole number greater than 0, for example 8192, or blank.';
}

/** Validate the Temperature override: blank, or a finite number of 0 or more. */
export function muxTemperatureError(value: string): MuxFieldError | null {
  const trimmed = value.trim();
  if (!trimmed) return null;
  if (!/^[0-9]*\.?[0-9]+$|^[0-9]+\.$/.test(trimmed)) return 'muxTemperatureInvalid';
  const parsed = Number(trimmed);
  return Number.isFinite(parsed) && parsed >= 0 ? null : 'muxTemperatureInvalid';
}

/** Validate the Max Tokens override: blank, or a whole number greater than 0. */
export function muxMaxTokensError(value: string): MuxFieldError | null {
  const trimmed = value.trim();
  if (!trimmed) return null;
  if (!/^[0-9]+$/.test(trimmed)) return 'muxMaxTokensInvalid';
  const parsed = Number(trimmed);
  return Number.isSafeInteger(parsed) && parsed > 0 ? null : 'muxMaxTokensInvalid';
}

/** The first invalid Mux override in a form, or null when all are valid (or the runtime is not Mux). */
export function muxFormFieldError(runtime: string, fields: Pick<MuxCaptainFormFields, 'muxTemperature' | 'muxMaxTokens'>): MuxFieldError | null {
  if (!isMuxRuntime(runtime)) return null;
  return muxTemperatureError(fields.muxTemperature) ?? muxMaxTokensError(fields.muxMaxTokens);
}

export function buildMuxRuntimeOptionsJson(runtime: string, fields: MuxCaptainFormFields): string | null {
  if (!isMuxRuntime(runtime)) return null;

  const temperature = parseOptionalNumber(fields.muxTemperature);
  const maxTokens = parseOptionalInteger(fields.muxMaxTokens);
  const approvalPolicy = isUnsupportedMuxApprovalPolicy(fields.muxApprovalPolicy) ? MUX_ASK_SAVED_AS : normalize(fields.muxApprovalPolicy);

  const payload: MuxCaptainOptions = {
    schemaVersion: 1,
    configDirectory: normalize(fields.muxConfigDirectory),
    endpoint: normalize(fields.muxEndpoint),
    baseUrl: normalize(fields.muxBaseUrl),
    adapterType: normalize(fields.muxAdapterType),
    temperature,
    maxTokens,
    systemPromptPath: normalize(fields.muxSystemPromptPath),
    approvalPolicy,
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
