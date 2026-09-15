export type HealthStatus = 'healthy' | 'warning' | 'error' | 'unknown';

export interface HealthClassification {
  status: HealthStatus;
  /** Build drift text to show beside the health indicator; null when the running build is current or drift is unknown. */
  driftWarning: string | null;
}

function readField(data: Record<string, unknown>, camel: string): unknown {
  const pascal = camel.charAt(0).toUpperCase() + camel.slice(1);
  return data[camel] ?? data[pascal];
}

/**
 * Classify the `/api/v1/status/health` response. The server reports `Status = "healthy"` whenever it
 * answers, so the status field alone never shows a problem; the build-drift fields (`BehindBy`,
 * `DriftWarning`) carry whether the running image is behind the landed commit. A drifted build reads
 * as a warning with its reason. A null running commit means drift is unknown, not absent.
 */
export function classifyHealth(data: Record<string, unknown> | null | undefined): HealthClassification {
  if (!data) return { status: 'error', driftWarning: null };

  const rawStatus = String(readField(data, 'status') ?? '').toLowerCase();
  let status: HealthStatus;
  if (rawStatus === 'healthy' || rawStatus === 'ok') status = 'healthy';
  else if (rawStatus === 'degraded' || rawStatus === 'warning') status = 'warning';
  else status = 'error';

  const warningText = readField(data, 'driftWarning');
  const behindBy = Number(readField(data, 'behindBy') ?? 0);
  let driftWarning: string | null = null;
  if (typeof warningText === 'string' && warningText.trim()) driftWarning = warningText.trim();
  else if (Number.isFinite(behindBy) && behindBy > 0) driftWarning = `Running build is ${behindBy} commits behind landed main.`;

  if (driftWarning && status === 'healthy') status = 'warning';
  return { status, driftWarning };
}
