import type {
  Captain,
  ObjectiveRefinementMessage,
  ObjectiveRefinementSession,
  ObjectiveRefinementSummaryResponse,
} from '../../types/models';

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function stringList(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === 'string') : [];
}

/**
 * Normalize a summary the server sent inside an event. The envelope carries the session and message
 * ids; the lists default to empty so the draft panel can render a partial summary.
 */
function normalizeSummary(raw: Record<string, unknown>, sessionId: string, messageId: string | null): ObjectiveRefinementSummaryResponse {
  return {
    ...(raw as unknown as ObjectiveRefinementSummaryResponse),
    sessionId: typeof raw.sessionId === 'string' && raw.sessionId ? raw.sessionId : sessionId,
    messageId: typeof raw.messageId === 'string' && raw.messageId ? raw.messageId : messageId,
    summary: typeof raw.summary === 'string' ? raw.summary : '',
    acceptanceCriteria: stringList(raw.acceptanceCriteria),
    nonGoals: stringList(raw.nonGoals),
    rolloutConstraints: stringList(raw.rolloutConstraints),
    suggestedPipelineId: typeof raw.suggestedPipelineId === 'string' ? raw.suggestedPipelineId : null,
    preparation: (raw.preparation ?? null) as ObjectiveRefinementSummaryResponse['preparation'],
    method: typeof raw.method === 'string' ? raw.method : '',
  };
}

/**
 * Read an `objective-refinement-session.summary.created` event. The server sends
 * `{ sessionId, messageId, summary }`, where `summary` is the summary object itself; the envelope is
 * not a summary. Returns null for a payload without a session id or a summary object.
 */
export function readRefinementSummaryCreated(data: unknown): {
  sessionId: string;
  messageId: string | null;
  summary: ObjectiveRefinementSummaryResponse;
} | null {
  if (!isRecord(data) || typeof data.sessionId !== 'string' || !data.sessionId || !isRecord(data.summary)) return null;
  const messageId = typeof data.messageId === 'string' && data.messageId ? data.messageId : null;
  return { sessionId: data.sessionId, messageId, summary: normalizeSummary(data.summary, data.sessionId, messageId) };
}

/**
 * Read an `objective-refinement-session.applied` event. The server sends
 * `{ sessionId, objectiveId, summary }` and no objective, so a listener reloads the objective itself.
 * Returns null for a payload without an objective id or a summary object.
 */
export function readRefinementApplied(data: unknown): {
  sessionId: string;
  objectiveId: string;
  summary: ObjectiveRefinementSummaryResponse;
} | null {
  if (!isRecord(data) || typeof data.objectiveId !== 'string' || !data.objectiveId || !isRecord(data.summary)) return null;
  const sessionId = typeof data.sessionId === 'string' ? data.sessionId : '';
  return { sessionId, objectiveId: data.objectiveId, summary: normalizeSummary(data.summary, sessionId, null) };
}

export function upsertRefinementSession(
  sessions: ObjectiveRefinementSession[],
  session: ObjectiveRefinementSession,
): ObjectiveRefinementSession[] {
  const next = [...sessions];
  const index = next.findIndex((item) => item.id === session.id);
  if (index >= 0) next[index] = session;
  else next.unshift(session);
  return next.sort((a, b) => new Date(b.lastUpdateUtc).getTime() - new Date(a.lastUpdateUtc).getTime());
}

export function removeRefinementSession(
  sessions: ObjectiveRefinementSession[],
  sessionId: string,
): ObjectiveRefinementSession[] {
  return sessions.filter((session) => session.id !== sessionId);
}

export function upsertRefinementMessage(
  messages: ObjectiveRefinementMessage[],
  message: ObjectiveRefinementMessage,
): ObjectiveRefinementMessage[] {
  const next = [...messages];
  const index = next.findIndex((item) => item.id === message.id);
  if (index >= 0) next[index] = message;
  else next.push(message);
  return next.sort((a, b) => a.sequence - b.sequence);
}

export function mergeCaptainState(
  captains: Captain[],
  update: { id: string; state: string; name?: string | null },
): Captain[] {
  return captains.map((captain) => (
    captain.id === update.id
      ? {
          ...captain,
          state: update.state,
          name: update.name ?? captain.name,
        }
      : captain
  ));
}

export function getLatestAssistantRefinementMessage(
  messages: ObjectiveRefinementMessage[],
): ObjectiveRefinementMessage | null {
  return [...messages]
    .reverse()
    .find((message) => message.role.toLowerCase() === 'assistant' && message.content.trim().length > 0) || null;
}
