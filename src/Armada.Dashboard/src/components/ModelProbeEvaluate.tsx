import { useEffect, useRef, useState } from 'react';
import { evaluateCaptain, evaluateModelEndpoint, getModelProbeRun } from '../api/client';
import type { ModelProbeKind, ModelProbeOutcome, ModelProbeRun } from '../types/models';
import { useLocale } from '../context/LocaleContext';
import StatusBadge from './shared/StatusBadge';

/** How often a queued or running probe run is polled. */
export const MODEL_PROBE_POLL_MS = 3000;

const PROBE_LABELS: Record<ModelProbeKind, string> = {
  ServedContext: 'Served context',
  OutputContract: 'Output contract',
  AsciiFormat: 'ASCII and format',
  ToolCalling: 'Tool calling',
  Speed: 'Speed',
  Determinism: 'Determinism',
};

const OUTCOME_LABELS: Record<ModelProbeOutcome, string> = {
  Passed: 'Passed',
  Failed: 'Failed',
  CouldNotRun: 'Could not run',
};

interface ModelProbeEvaluateProps {
  /** What to evaluate: a captain (through its own runtime) or a model endpoint. */
  target: 'captain' | 'modelEndpoint';
  targetId: string;
  /** The page's error display (for example its ErrorModal); errors show inline when omitted. */
  onError?: (message: string) => void;
  /** Poll interval override (tests). */
  pollMs?: number;
}

function isFinished(run: ModelProbeRun | null): boolean {
  return !!run && (run.status === 'Completed' || run.status === 'Failed');
}

/** Evaluate button: starts a Quick probe run, polls it until it finishes, and shows the result counts. */
export default function ModelProbeEvaluate({ target, targetId, onError, pollMs = MODEL_PROBE_POLL_MS }: ModelProbeEvaluateProps) {
  const { t } = useLocale();
  const [run, setRun] = useState<ModelProbeRun | null>(null);
  const [starting, setStarting] = useState(false);
  const [polling, setPolling] = useState(false);
  const [inlineError, setInlineError] = useState('');
  const onErrorRef = useRef(onError);
  onErrorRef.current = onError;

  function report(message: string) {
    if (onErrorRef.current) onErrorRef.current(message);
    else setInlineError(message);
  }

  const runId = run?.id ?? null;
  useEffect(() => {
    if (!polling || !runId) return undefined;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const tick = async () => {
      try {
        const latest = await getModelProbeRun(runId);
        if (cancelled) return;
        setRun(latest);
        if (isFinished(latest)) { setPolling(false); return; }
        timer = setTimeout(tick, pollMs);
      } catch (err) {
        if (cancelled) return;
        setPolling(false);
        report(err instanceof Error ? err.message : t('Failed to load evaluation.'));
      }
    };
    timer = setTimeout(tick, pollMs);
    return () => {
      cancelled = true;
      if (timer) clearTimeout(timer);
    };
    // report and t are stable enough for a poll loop; restarting on their identity would re-poll immediately.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [polling, runId, pollMs]);

  async function handleEvaluate() {
    setStarting(true);
    setInlineError('');
    try {
      const started = target === 'captain'
        ? await evaluateCaptain(targetId, 'Quick')
        : await evaluateModelEndpoint(targetId, 'Quick');
      setRun(started);
      setPolling(!isFinished(started));
    } catch (err) {
      report(err instanceof Error ? err.message : t('Evaluate failed.'));
    } finally {
      setStarting(false);
    }
  }

  const busy = starting || polling;
  const finished = isFinished(run);

  return (
    <div className="model-probe-evaluate">
      <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'center', flexWrap: 'wrap' }}>
        <button
          type="button"
          className="btn btn-sm"
          onClick={handleEvaluate}
          disabled={busy}
          title={t('Run the Quick mechanical probes against this target as an evaluation. Results are observations only: they never change routing or settings and never count in production stage outcomes.')}
        >
          {busy ? t('Evaluating...') : t('Evaluate')}
        </button>
        {run && (
          <span data-testid="model-probe-counts">
            <StatusBadge status={run.status} />{' '}
            <span className="text-dim">
              {t('{{passed}} passed, {{failed}} failed, {{couldNotRun}} could not run', {
                passed: run.passedCount, failed: run.failedCount, couldNotRun: run.couldNotRunCount,
              })}
            </span>
          </span>
        )}
      </div>
      {inlineError && <p role="alert" className="text-danger">{inlineError}</p>}
      {finished && run?.failureReason && <p className="text-danger">{run.failureReason}</p>}
      {finished && run && run.results.length > 0 && (
        <ul className="model-probe-results" style={{ margin: '0.5rem 0 0', paddingLeft: '1.25rem' }}>
          {run.results.map((result) => (
            <li key={result.id || result.probe}>
              <strong>{t(PROBE_LABELS[result.probe] ?? result.probe)}</strong>{' '}
              <span className={`tag ${result.outcome.toLowerCase()}`}>{t(OUTCOME_LABELS[result.outcome] ?? result.outcome)}</span>
              {result.summary && <span>{' '}{result.summary}</span>}
              {result.reason && <span className="text-dim">{' '}({result.reason})</span>}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
