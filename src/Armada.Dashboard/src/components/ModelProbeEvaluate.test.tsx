import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import catalogSource from '../../../Armada.Server/wwwroot/i18n/armada.json?raw';
import componentSource from './ModelProbeEvaluate.tsx?raw';
import captainDetailSource from '../pages/CaptainDetail.tsx?raw';
import endpointsSource from '../pages/Endpoints.tsx?raw';
import ModelProbeEvaluate from './ModelProbeEvaluate';
import { evaluateCaptain, evaluateModelEndpoint, getModelProbeRun } from '../api/client';
import { translateTemplate } from '../i18n/runtime';
import type { I18nCatalog } from '../i18n/runtime';
import type { ModelProbeRun } from '../types/models';

vi.mock('../api/client', () => ({
  evaluateCaptain: vi.fn(),
  evaluateModelEndpoint: vi.fn(),
  getModelProbeRun: vi.fn(),
}));

const localeValue = {
  t: (text: string, params?: Record<string, string | number | null | undefined>) => translateTemplate('en', text, null, params),
  locale: 'en',
  formatDateTime: (v: string | null | undefined) => v ?? '',
  formatRelativeTime: (v: string | null | undefined) => v ?? '',
};

vi.mock('../context/LocaleContext', () => ({
  useLocale: () => localeValue,
}));

function run(overrides: Partial<ModelProbeRun> = {}): ModelProbeRun {
  return {
    id: 'mpr_1', targetKind: 'Captain', captainId: 'cpt_1', depth: 'Quick', status: 'Queued', isEvaluation: true,
    probeSetVersion: 'quick-1', tokenBudget: 1000, tokensUsed: 0, passedCount: 0, failedCount: 0, couldNotRunCount: 0,
    createdUtc: '2026-10-26T00:00:00Z', lastUpdateUtc: '2026-10-26T00:00:00Z', results: [],
    ...overrides,
  };
}

const COMPLETED = run({
  status: 'Completed', passedCount: 4, failedCount: 1, couldNotRunCount: 1,
  results: [
    { id: 'r1', runId: 'mpr_1', probe: 'AsciiFormat', outcome: 'Failed', summary: '2 em dashes', attempts: 1, tokensUsed: 10, durationMs: 5, createdUtc: '' },
    { id: 'r2', runId: 'mpr_1', probe: 'ServedContext', outcome: 'CouldNotRun', reason: 'vendor CLI hides the served context', attempts: 0, tokensUsed: 0, durationMs: 0, createdUtc: '' },
  ],
});

describe('ModelProbeEvaluate', () => {
  beforeEach(() => {
    vi.mocked(evaluateCaptain).mockReset();
    vi.mocked(evaluateModelEndpoint).mockReset();
    vi.mocked(getModelProbeRun).mockReset();
  });

  it('starts a Quick run, disables the button while polling, and shows the finished counts and probes', async () => {
    vi.mocked(evaluateCaptain).mockResolvedValue(run());
    vi.mocked(getModelProbeRun).mockResolvedValueOnce(run({ status: 'Running', passedCount: 2 })).mockResolvedValue(COMPLETED);
    render(<ModelProbeEvaluate target="captain" targetId="cpt_1" pollMs={5} />);

    await userEvent.click(screen.getByRole('button', { name: 'Evaluate' }));
    expect(evaluateCaptain).toHaveBeenCalledWith('cpt_1', 'Quick');
    expect(screen.getByRole('button', { name: 'Evaluating...' })).toBeDisabled();

    await waitFor(() => expect(screen.getByTestId('model-probe-counts')).toHaveTextContent('4 passed, 1 failed, 1 could not run'));
    expect(screen.getByTestId('model-probe-counts')).toHaveTextContent('Completed');
    expect(getModelProbeRun).toHaveBeenCalledWith('mpr_1');
    expect(screen.getByRole('button', { name: 'Evaluate' })).toBeEnabled();
    expect(screen.getByText('ASCII and format')).toBeInTheDocument();
    expect(screen.getByText('2 em dashes', { exact: false })).toBeInTheDocument();
    expect(screen.getByText('Could not run')).toBeInTheDocument();
    expect(screen.getByText('(vendor CLI hides the served context)', { exact: false })).toBeInTheDocument();
  });

  it('evaluates a model endpoint and stops polling when the run fails', async () => {
    vi.mocked(evaluateModelEndpoint).mockResolvedValue(run({ targetKind: 'ModelEndpoint', modelEndpointId: 'mep_1' }));
    vi.mocked(getModelProbeRun).mockResolvedValue(run({ status: 'Failed', failureReason: 'Evaluation budget exhausted.' }));
    render(<ModelProbeEvaluate target="modelEndpoint" targetId="mep_1" pollMs={5} />);

    await userEvent.click(screen.getByRole('button', { name: 'Evaluate' }));
    expect(evaluateModelEndpoint).toHaveBeenCalledWith('mep_1', 'Quick');
    expect(await screen.findByText('Evaluation budget exhausted.')).toBeInTheDocument();
    expect(screen.getByTestId('model-probe-counts')).toHaveTextContent('0 passed, 0 failed, 0 could not run');
    const calls = vi.mocked(getModelProbeRun).mock.calls.length;
    await new Promise((resolve) => setTimeout(resolve, 30));
    expect(vi.mocked(getModelProbeRun).mock.calls.length).toBe(calls);
  });

  it('hands a start error to the page error display', async () => {
    vi.mocked(evaluateCaptain).mockRejectedValue(new Error('Depth Full is not implemented until Phase 5.'));
    const onError = vi.fn();
    render(<ModelProbeEvaluate target="captain" targetId="cpt_1" onError={onError} pollMs={5} />);
    await userEvent.click(screen.getByRole('button', { name: 'Evaluate' }));
    await waitFor(() => expect(onError).toHaveBeenCalledWith('Depth Full is not implemented until Phase 5.'));
    expect(screen.getByRole('button', { name: 'Evaluate' })).toBeEnabled();
    expect(screen.queryByTestId('model-probe-counts')).toBeNull();
  });

  it('shows a poll error inline when no page error display is given', async () => {
    vi.mocked(evaluateCaptain).mockResolvedValue(run());
    vi.mocked(getModelProbeRun).mockRejectedValue(new Error('Model probe run not found.'));
    render(<ModelProbeEvaluate target="captain" targetId="cpt_1" pollMs={5} />);
    await userEvent.click(screen.getByRole('button', { name: 'Evaluate' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Model probe run not found.');
    expect(screen.getByRole('button', { name: 'Evaluate' })).toBeEnabled();
  });
});

describe('ModelProbeEvaluate i18n catalog', () => {
  const S = "'((?:[^'\\\\]|\\\\.)*)'";
  const call = new RegExp(`\\bt\\(\\s*${S}`, 'g');
  const literal = new RegExp(`^\\s*\\w+:\\s*${S},?$`, 'gm');
  const keys = new Set<string>();
  for (const m of componentSource.matchAll(call)) keys.add(m[1].replace(/\\'/g, "'"));
  for (const m of componentSource.matchAll(literal)) keys.add(m[1]);
  const pageKeys: Array<{ source: string; keys: string[] }> = [
    { source: captainDetailSource, keys: ['Evaluation', 'Probe this captain through its own runtime: served context, output contract, ASCII and format, tool calling, speed and determinism.'] },
    { source: endpointsSource, keys: ['Evaluate', 'Evaluation', 'Probe this model endpoint: served context, output contract, ASCII and format, tool calling, speed and determinism.', "Open this endpoint\\'s details to run the Quick probes against it as an evaluation."] },
  ];
  for (const page of pageKeys) for (const key of page.keys) keys.add(key.replace(/\\'/g, "'"));
  const catalog = JSON.parse(catalogSource) as I18nCatalog;

  it('finds the strings', () => {
    expect(keys.has('{{passed}} passed, {{failed}} failed, {{couldNotRun}} could not run')).toBe(true);
    expect(keys.has('Could not run')).toBe(true);
    expect(keys.has('Served context')).toBe(true);
    for (const page of pageKeys) for (const key of page.keys) expect(page.source).toContain(`'${key}'`);
  });

  it('has a translation for every string in every non-English locale', () => {
    const missing: string[] = [];
    for (const meta of catalog.supportedLocales) {
      if (meta.code === catalog.defaultLocale) continue;
      const pack = catalog.locales[meta.code] ?? {};
      for (const key of keys) {
        if (!pack.phrases?.[key] && !pack.terms?.[key] && !pack.sections?.[key]) missing.push(`${meta.code}: ${key}`);
      }
    }
    expect(missing).toEqual([]);
  });

  it('keeps placeholders intact', () => {
    const bad: string[] = [];
    for (const [code, pack] of Object.entries(catalog.locales)) {
      for (const key of keys) {
        const value = pack.phrases?.[key];
        if (!value) continue;
        const ph = (text: string) => (text.match(/\{\{[\w.]+\}\}/g) ?? []).sort().join(',');
        if (ph(key) !== ph(value)) bad.push(`${code}: ${key}`);
      }
    }
    expect(bad).toEqual([]);
  });
});
