import type { CoordinationMessage } from '../../types/models';

/** Insert or replace a message, keeping the list ordered oldest first by createdUtc then id. */
export function upsertMessage(messages: CoordinationMessage[], message: CoordinationMessage): CoordinationMessage[] {
  const next = [...messages];
  const index = next.findIndex((item) => item.id === message.id);
  if (index >= 0) next[index] = message;
  else next.push(message);
  return sortMessages(next);
}

/** Sort messages oldest first; ties break on id so ordering is stable across reloads. */
export function sortMessages(messages: CoordinationMessage[]): CoordinationMessage[] {
  return [...messages].sort((a, b) => {
    const delta = new Date(a.createdUtc).getTime() - new Date(b.createdUtc).getTime();
    if (delta !== 0) return delta;
    return a.id < b.id ? -1 : a.id > b.id ? 1 : 0;
  });
}

/**
 * Stable per-browser participant key for dashboard presence, persisted in
 * localStorage so refreshes do not create a new participant row.
 */
export function getDashboardParticipantKey(): string {
  const storageKey = 'armada_coordination_participant_key';
  try {
    const existing = window.localStorage.getItem(storageKey);
    if (existing) return existing;
    const generated = 'dashboard-' + Math.random().toString(36).slice(2, 10);
    window.localStorage.setItem(storageKey, generated);
    return generated;
  } catch {
    return 'dashboard-anon';
  }
}
