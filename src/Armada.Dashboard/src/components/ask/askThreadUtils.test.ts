import { describe, expect, it } from 'vitest';
import { replaceAskTurn, toChatTurns } from './askThreadUtils';
import type { AskTurn, ChatToolCall } from '../../types/models';

const metrics = { timeToFirstTokenMs: 1, streamingMs: 1, totalMs: 2, promptTokens: 1, completionTokens: 1, totalTokens: 2, tokensPerSecond: 1 };
const call: ChatToolCall = { id: 'call-1', name: 'read', status: 'success', arguments: 'src/File.cs', result: 'ok', elapsedMs: 12 };

function turn(overrides: Partial<AskTurn>): AskTurn {
  return {
    threadId: 'ath_1', requestId: 'req_1', sequence: 1, captainId: 'cpt_1', content: 'Look at the file', showThinking: false,
    state: 'Completed', response: { success: true, reply: 'Done.', model: 'm', metrics, error: null }, createdUtc: '', completedUtc: '',
    ...overrides,
  };
}

describe('Ask tool-call cards on a reloaded conversation', () => {
  it('restores the tool calls stored with a finished turn, including a failed one', () => {
    const done = toChatTurns([turn({ response: { success: true, reply: 'Done.', model: 'm', metrics, error: null, toolCalls: [call] } })]);
    expect(done[1].tools).toEqual([call]);
    const failed = toChatTurns([turn({ state: 'Failed', response: { success: false, reply: '', model: null, metrics, error: 'quota', toolCalls: [call] } })]);
    expect(failed[1].text).toBe('quota');
    expect(failed[1].tools).toEqual([call]);
  });

  it('restores the calls a running turn has made so far', () => {
    const running = toChatTurns([turn({ state: 'Running', response: null, liveToolCalls: [{ ...call, status: 'running', result: null }] })]);
    expect(running[1].streaming).toBe(true);
    expect(running[1].tools).toEqual([{ ...call, status: 'running', result: null }]);
  });

  it('shows no cards for a turn that made no calls', () => {
    expect(toChatTurns([turn({ response: { success: true, reply: 'Hi', model: 'm', metrics, error: null, toolCalls: [] } })])[1].tools).toBeUndefined();
  });

  it('replaces a card by id instead of adding a second one, and keeps streamed cards until the server reports calls', () => {
    const running = turn({ state: 'Running', response: null, liveToolCalls: [{ ...call, status: 'running', result: null }] });
    let turns = toChatTurns([running]);
    turns = replaceAskTurn(turns, turn({ state: 'Running', response: null, liveToolCalls: [call] }));
    expect(turns[1].tools).toEqual([call]);
    turns = replaceAskTurn(turns, turn({ response: { success: true, reply: 'Done.', model: 'm', metrics, error: null, toolCalls: [call] } }));
    expect(turns[1].tools).toHaveLength(1);

    const streamed = [{ id: 'streamed', name: 'list', status: 'running' as const }];
    const local = toChatTurns([running]);
    local[1] = { ...local[1], tools: streamed };
    expect(replaceAskTurn(local, turn({ state: 'Running', response: null }))[1].tools).toEqual(streamed);
  });
});
