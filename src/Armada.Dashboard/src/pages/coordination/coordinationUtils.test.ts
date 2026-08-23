import { describe, expect, it } from 'vitest';
import { getDashboardParticipantKey, sortMessages, upsertMessage } from './coordinationUtils';
import type { CoordinationMessage } from '../../types/models';

function makeMessage(id: string, createdUtc: string): CoordinationMessage {
  return {
    id,
    coordinationRoomId: 'crm_test',
    tenantId: null,
    authorType: 'Operator',
    authorId: 'op_' + id,
    authorName: 'Operator ' + id,
    content: 'note ' + id,
    voyageId: null,
    missionId: null,
    vesselId: null,
    incidentId: null,
    createdUtc,
    lastUpdateUtc: createdUtc,
  };
}

describe('coordinationUtils', () => {
  it('upsertMessage appends new messages and keeps chronological order', () => {
    let messages = [makeMessage('b', '2026-01-01T00:00:02Z')];
    messages = upsertMessage(messages, makeMessage('a', '2026-01-01T00:00:01Z'));
    expect(messages.map((m) => m.id)).toEqual(['a', 'b']);
  });

  it('upsertMessage replaces an existing message in place', () => {
    let messages = [
      makeMessage('a', '2026-01-01T00:00:01Z'),
      makeMessage('b', '2026-01-01T00:00:02Z'),
    ];
    const edited = { ...makeMessage('a', '2026-01-01T00:00:01Z'), content: 'edited' };
    messages = upsertMessage(messages, edited);
    expect(messages.length).toBe(2);
    expect(messages[0].id).toBe('a');
    expect(messages[0].content).toBe('edited');
  });

  it('sortMessages breaks ties on id for stability', () => {
    const messages = [
      makeMessage('b', '2026-01-01T00:00:01Z'),
      makeMessage('a', '2026-01-01T00:00:01Z'),
    ];
    expect(sortMessages(messages).map((m) => m.id)).toEqual(['a', 'b']);
  });

  it('getDashboardParticipantKey returns a stable non-empty key', () => {
    const first = getDashboardParticipantKey();
    const second = getDashboardParticipantKey();
    expect(first.length).toBeGreaterThan(0);
    expect(first).toBe(second);
  });
});
