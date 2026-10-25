import type { AskTurn } from '../../types/models';
import type { ChatTurn } from '../shared/CaptainChatPanel';

export function toChatTurns(stored: AskTurn[]): ChatTurn[] {
  const result: ChatTurn[] = [];
  for (const turn of stored) {
    result.push({ id: `${turn.requestId}:user`, role: 'user', text: turn.content });
    const response = turn.response;
    const uncertain = turn.state === 'Uncertain';
    const pending = turn.state === 'Pending' || turn.state === 'Running';
    const text = response?.reply || (uncertain
      ? 'The server cannot confirm that this turn completed. It was not replayed.'
      : response?.error || '');
    result.push({
      id: `${turn.requestId}:assistant`,
      role: 'assistant',
      text,
      streaming: pending,
      metrics: response?.metrics,
      model: response?.model,
      thinking: response?.thinking ?? undefined,
    });
  }
  return result;
}

export function replaceAskTurn(current: ChatTurn[], turn: AskTurn): ChatTurn[] {
  const next = toChatTurns([turn]);
  const userId = `${turn.requestId}:user`;
  const assistantId = `${turn.requestId}:assistant`;
  const userIndex = current.findIndex((item) => item.id === userId);
  const assistantIndex = current.findIndex((item) => item.id === assistantId);
  if (userIndex < 0) return [...current, ...next];
  const result = [...current];
  if (assistantIndex < 0) result.splice(userIndex + 1, 0, next[1]);
  else result[assistantIndex] = { ...result[assistantIndex], ...next[1], tools: result[assistantIndex].tools };
  return result;
}
