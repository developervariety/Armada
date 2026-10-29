import { useCallback, useRef, useState } from 'react';
import { useLocale } from '../context/LocaleContext';
import type { DeleteProgressState } from '../components/shared/DeleteProgressDialog';

const IDLE: DeleteProgressState = { status: 'idle', title: '', error: '' };

interface PendingDelete {
  title: string;
  task: () => Promise<void>;
}

/**
 * Runs one slow delete behind a progress dialog. A second delete started while one is pending is ignored,
 * a failure keeps the dialog open with the error, and Retry runs the same task again.
 */
export function useDeleteProgress() {
  const { t } = useLocale();
  const [state, setState] = useState<DeleteProgressState>(IDLE);
  const pending = useRef(false);
  const last = useRef<PendingDelete | null>(null);

  const run = useCallback(async (title: string, task: () => Promise<void>): Promise<boolean> => {
    if (pending.current) return false;
    pending.current = true;
    last.current = { title, task };
    setState({ status: 'pending', title, error: '' });
    try {
      await task();
      setState(IDLE);
      return true;
    } catch (err: unknown) {
      setState({ status: 'error', title, error: err instanceof Error && err.message ? err.message : t('Delete failed.') });
      return false;
    } finally {
      pending.current = false;
    }
  }, [t]);

  const retry = useCallback(() => {
    const previous = last.current;
    if (previous) void run(previous.title, previous.task);
  }, [run]);

  const dismiss = useCallback(() => {
    if (!pending.current) setState(IDLE);
  }, []);

  return { state, run, retry, dismiss };
}
