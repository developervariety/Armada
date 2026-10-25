import { useEffect, useState, type Dispatch, type RefObject, type SetStateAction } from 'react';
import { listAskTurns } from '../../api/client';
import type { ChatTurn } from '../shared/CaptainChatPanel';
import { toChatTurns } from './askThreadUtils';

interface EarlierAskTurnsOptions {
  selectionKey: string;
  enabled: boolean;
  selectedId: RefObject<string>;
  generation: RefObject<number>;
  earliestSequence: RefObject<number | null>;
  setTurns: Dispatch<SetStateAction<ChatTurn[]>>;
  setHasEarlier: Dispatch<SetStateAction<boolean>>;
  setError: Dispatch<SetStateAction<string>>;
  t: (text: string) => string;
}

/** Load a cursor page only into the conversation view that requested it. */
export function useEarlierAskTurns(options: EarlierAskTurnsOptions) {
  const [loadingEarlier, setLoadingEarlier] = useState(false);
  useEffect(() => { setLoadingEarlier(false); }, [options.selectionKey]);

  async function loadEarlierTurns() {
    if (!options.enabled || loadingEarlier) return;
    const threadId = options.selectedId.current;
    const generation = options.generation.current;
    const beforeSequence = options.earliestSequence.current;
    if (!threadId || beforeSequence == null) { options.setHasEarlier(false); return; }
    const current = () => options.selectedId.current === threadId && options.generation.current === generation;
    setLoadingEarlier(true);
    try {
      const older = await listAskTurns(threadId, beforeSequence, 100);
      if (!current()) return;
      if (older.length > 0) options.earliestSequence.current = older[0].sequence;
      options.setTurns((turns) => [...toChatTurns(older), ...turns]);
      options.setHasEarlier(older.length >= 100);
    } catch (error: unknown) {
      if (current()) options.setError(error instanceof Error ? error.message : options.t('Failed to load earlier turns.'));
    } finally {
      if (current()) setLoadingEarlier(false);
    }
  }

  return { loadingEarlier, loadEarlierTurns };
}
