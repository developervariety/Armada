/**
 * Response fields a vessel update must not send: identity, ownership, server-maintained counters and timestamps,
 * and the retired flat auto-land fields the fork server never stored.
 */
const EXCLUDED_KEYS = [
  'id',
  'tenantId',
  'userId',
  'hasGitHubTokenOverride',
  'autoLandCalibrationLandedCount',
  'lastReflectionMissionId',
  'createdUtc',
  'lastUpdateUtc',
  'autoLandEnabled',
  'autoLandMaxFiles',
  'autoLandMaxLines',
  'autoLandPathAllowGlobs',
  'autoLandPathDenyGlobs',
];

/**
 * Build a vessel PUT body from the vessel as loaded plus the form's changes. The REST update replaces fields that a
 * body omits, so the loaded vessel is the base: configuration the form does not edit (protected paths, sibling
 * repositories, default playbooks, thresholds, the auto-land predicate) is sent back unchanged.
 */
export function buildVesselUpdatePayload(
  loaded: object,
  changes: Record<string, unknown>,
): Record<string, unknown> {
  const payload: Record<string, unknown> = { ...(loaded as Record<string, unknown>) };
  for (const key of EXCLUDED_KEYS) delete payload[key];

  // GET returns the predicate as a JSON string; the update route accepts it only as an object.
  const stored = payload.autoLandPredicate;
  if (typeof stored === 'string') {
    try {
      const parsed: unknown = JSON.parse(stored);
      if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) payload.autoLandPredicate = parsed;
      else delete payload.autoLandPredicate;
    } catch {
      delete payload.autoLandPredicate;
    }
  }

  for (const [key, value] of Object.entries(changes)) payload[key] = value;
  return payload;
}
