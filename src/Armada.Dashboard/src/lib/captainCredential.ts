import type { Captain } from '../types/models';

/**
 * Per-captain provider credential fields used by captain forms. These override the
 * host-level provider API-key environment variable when the captain's model needs an external key.
 */
export interface CaptainCredentialFormFields {
  apiKey: string;
  apiBaseUrl: string;
}

export const EMPTY_CAPTAIN_CREDENTIAL_FORM: CaptainCredentialFormFields = {
  apiKey: '',
  apiBaseUrl: '',
};

export function credentialFormFromCaptain(
  captain: Pick<Captain, 'apiKey' | 'apiBaseUrl'> | null | undefined,
): CaptainCredentialFormFields {
  return {
    apiKey: captain?.apiKey ?? '',
    apiBaseUrl: captain?.apiBaseUrl ?? '',
  };
}

export function normalizeCredential(value: string): string | null {
  const trimmed = value.trim();
  return trimmed ? trimmed : null;
}
