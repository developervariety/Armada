import type { InferenceOptions } from '../types/models';

export const defaultInferenceOptions: InferenceOptions = {
  toolsEnabled: true, readOnlyTools: false, mcpToolsEnabled: true, allowedTools: null,
  maxIterations: 100, maxTokens: null, temperature: null, topP: null, requireHarbor: false,
};

/** Empty text permits all; use the tools switch to disable tools altogether. */
export function parseAllowedTools(value: string): string[] | null {
  return value.trim() ? value.split(/[\n,]/).map(s => s.trim()).filter(Boolean) : null;
}

export function validateInferenceOptions(options: InferenceOptions): string | null {
  if (!Number.isInteger(options.maxIterations) || options.maxIterations < 1 || options.maxIterations > 1000) return 'Maximum model turns must be between 1 and 1000.';
  if (options.maxTokens != null && (!Number.isInteger(options.maxTokens) || options.maxTokens < 1 || options.maxTokens > 10000000)) return 'Maximum output tokens must be between 1 and 10000000.';
  if (options.temperature != null && (!Number.isFinite(options.temperature) || options.temperature < 0 || options.temperature > 2)) return 'Temperature must be between 0 and 2.';
  if (options.topP != null && (!Number.isFinite(options.topP) || options.topP < 0 || options.topP > 1)) return 'Top P must be between 0 and 1.';
  if (options.allowedTools && (options.allowedTools.length > 256 || new Set(options.allowedTools).size !== options.allowedTools.length)) return 'Allowed tools must contain at most 256 distinct names.';
  return null;
}
