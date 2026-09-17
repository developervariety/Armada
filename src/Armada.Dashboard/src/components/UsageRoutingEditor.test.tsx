import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import UsageRoutingEditor, { emptyUsageRouting } from './UsageRoutingEditor';
import { previewUsageRouting } from '../api/client';
import type { Captain, SmartRoutingPreviewResult } from '../types/models';
vi.mock('../api/client', () => ({ previewUsageRouting: vi.fn() }));
vi.mock('../context/LocaleContext', () => ({
  useLocale: () => ({ t: (s: string, params?: Record<string, string | number>) => params ? Object.entries(params).reduce((text, [k, v]) => text.split(`{{${k}}}`).join(String(v)), s) : s }),
}));

const captains = [
  { id: 'cpt_a', name: 'alpha', model: 'model-x' },
  { id: 'cpt_b', name: 'beta', model: 'model-x' },
  { id: 'cpt_c', name: 'gamma', model: 'model-y' },
] as unknown as Captain[];

const accountPolicy = {
  ...emptyUsageRouting,
  enabled: true,
  accounts: [
    { id: 'acct-spent', collector: 'Manual', captainIds: ['cpt_a', 'cpt_b'] },
    { id: 'acct-ok', collector: 'Manual', captainIds: ['cpt_c'] },
  ],
};
const statuses = [
  { accountId: 'acct-spent', state: 'Exhausted', source: 'manual', reason: 'usage_window_exhausted', windows: [] },
  { accountId: 'acct-ok', state: 'Normal', source: 'manual', reason: 'ok', windows: [] },
];

function previewResult(overrides: Partial<SmartRoutingPreviewResult> = {}): SmartRoutingPreviewResult {
  return {
    reason: 'persona_models_default', smartRoutingEnabled: true, hasPersonaRoutes: false, hasPersonaModels: true,
    legacyOrder: [{ id: 'cpt_a', name: 'alpha', model: 'model-x', runtime: 'Codex' }, { id: 'cpt_c', name: 'gamma', model: 'model-y', runtime: 'Codex' }],
    usageFilter: [
      { captainId: 'cpt_a', model: 'model-x', accountId: 'acct-spent', state: 'Exhausted', outcome: 'removed', reason: 'usage_window_exhausted' },
      { captainId: 'cpt_c', model: 'model-y', accountId: 'acct-ok', state: 'Normal', outcome: 'kept', reason: 'normal_allowance' },
    ],
    modelGroups: [{ name: 'default', models: ['model-y'], captainIds: ['cpt_c'] }],
    capacity: { choice: 'Default', source: 'no_work_text', asked: false },
    candidates: [{ id: 'cpt_c', name: 'gamma', model: 'model-y', runtime: 'Codex' }],
    chosen: { id: 'cpt_c', name: 'gamma', model: 'model-y', runtime: 'Codex' },
    ...overrides,
  };
}

describe('Usage routing editor', () => {
  it('switches between Legacy Routing and Smart Routing in the draft policy', () => {
    const onChange = vi.fn();
    render(<UsageRoutingEditor value={JSON.stringify(emptyUsageRouting)} onChange={onChange} statuses={[]} />);
    expect(screen.getByLabelText('Legacy Routing')).toBeChecked();
    fireEvent.click(screen.getByLabelText('Smart Routing'));
    expect(JSON.parse(onChange.mock.calls[0][0]).enabled).toBe(true);
    expect(screen.queryByText(/V2/)).not.toBeInTheDocument();
  });

  it('warns beside a model when every captain running it is on an Exhausted account', () => {
    const policy = { ...accountPolicy, personaModels: { Worker: { default: ['model-x'], lighter: [], stronger: [] } } };
    render(<UsageRoutingEditor value={JSON.stringify(policy)} onChange={vi.fn()} statuses={statuses} personas={['Worker']} captains={captains} />);
    const defaults = screen.getByRole('group', { name: 'Worker Default' });
    expect(within(defaults).getByText('model-x')).toBeInTheDocument();
    expect(within(defaults).getByText('2 captains')).toBeInTheDocument();
    expect(within(defaults).getByText('All accounts exhausted')).toBeInTheDocument();
    const lighter = screen.getByLabelText('Add model to Worker Lighter');
    expect(within(lighter).getByRole('option', { name: 'model-y (1)' })).toBeInTheDocument();
  });

  it('notes when a persona restriction excludes every captain', () => {
    const excluding = { ...accountPolicy, personaRoutes: { Judge: [{ accountId: 'acct-ok', models: ['model-x'] }] } };
    const { rerender } = render(<UsageRoutingEditor value={JSON.stringify(excluding)} onChange={vi.fn()} statuses={statuses} personas={['Judge']} captains={captains} />);
    expect(within(screen.getByTestId('restriction-Judge')).getByText(/This restriction excludes every captain\./)).toBeInTheDocument();
    const admitting = { ...accountPolicy, personaRoutes: { Judge: [{ accountId: 'acct-ok', models: [] }] } };
    rerender(<UsageRoutingEditor value={JSON.stringify(admitting)} onChange={vi.fn()} statuses={statuses} personas={['Judge']} captains={captains} />);
    expect(within(screen.getByTestId('restriction-Judge')).queryByText(/This restriction excludes every captain\./)).not.toBeInTheDocument();
  });

  it('adds and removes a persona restriction in the draft policy', () => {
    const onChange = vi.fn();
    const { rerender } = render(<UsageRoutingEditor value={JSON.stringify(accountPolicy)} onChange={onChange} statuses={statuses} personas={['Judge']} captains={captains} />);
    fireEvent.change(screen.getByLabelText('Restrict persona'), { target: { value: 'Judge' } });
    fireEvent.click(screen.getByText('Add restriction'));
    const added = JSON.parse(onChange.mock.calls[0][0]);
    expect(added.personaRoutes).toEqual({ Judge: [{ accountId: 'acct-spent', models: [] }] });
    rerender(<UsageRoutingEditor value={JSON.stringify(added)} onChange={onChange} statuses={statuses} personas={['Judge']} captains={captains} />);
    fireEvent.click(screen.getByLabelText('Remove restriction for Judge'));
    expect(JSON.parse(onChange.mock.calls[1][0]).personaRoutes).toEqual({});
  });

  it('previews the draft and shows legacy order, the usage filter, the capacity reading, and the chosen captain', async () => {
    vi.mocked(previewUsageRouting).mockResolvedValue(previewResult());
    const onChange = vi.fn();
    render(<UsageRoutingEditor value={JSON.stringify(accountPolicy)} onChange={onChange} statuses={statuses} personas={['Worker', 'Judge']} captains={captains} />);
    fireEvent.change(screen.getByLabelText('Persona'), { target: { value: 'Judge' } });
    fireEvent.change(screen.getByLabelText('Priority'), { target: { value: '10' } });
    fireEvent.click(screen.getByText('Run preview'));
    await waitFor(() => expect(previewUsageRouting).toHaveBeenCalledWith({ persona: 'Judge', priority: 10, preferredModel: null, usageRouting: accountPolicy }));
    expect(onChange).not.toHaveBeenCalled();
    const result = await screen.findByRole('status');
    expect(result).toHaveTextContent('Chosen captain: gamma (model-y)');
    expect(within(result).getAllByRole('list')[0]).toHaveTextContent('alpha (model-x)');
    const rows = within(result).getAllByRole('row');
    expect(rows[1]).toHaveTextContent('alpha');
    expect(rows[1]).toHaveTextContent('removed');
    expect(rows[1]).toHaveTextContent('usage_window_exhausted');
    expect(rows[2]).toHaveTextContent('kept');
    expect(result).toHaveTextContent('Not asked (no mission title or text).');
  });

  it('sends mission title and text and shows the capacity choice', async () => {
    vi.mocked(previewUsageRouting).mockResolvedValue(previewResult({ capacity: { choice: 'Stronger', source: 'decision', asked: true } }));
    render(<UsageRoutingEditor value={JSON.stringify(accountPolicy)} onChange={vi.fn()} statuses={[]} personas={['Worker']} captains={captains} />);
    fireEvent.change(screen.getByLabelText('Mission title (optional)'), { target: { value: 'Hard diagnosis' } });
    fireEvent.change(screen.getByLabelText('Mission text (optional; asks the capacity decision)'), { target: { value: 'Find the cause.' } });
    fireEvent.click(screen.getByText('Run preview'));
    await waitFor(() => expect(previewUsageRouting).toHaveBeenLastCalledWith(expect.objectContaining({ missionTitle: 'Hard diagnosis', missionText: 'Find the cause.' })));
    expect(await screen.findByRole('status')).toHaveTextContent('Stronger list first (source: decision)');
  });

  it('blocks preview of invalid JSON and shows unknown data explicitly', () => {
    render(<UsageRoutingEditor value="{" onChange={vi.fn()} statuses={[{ accountId: 'example', state: 'Unknown', source: 'none', reason: 'required_usage_window_unknown_or_stale', windows: [] }]} />);
    expect(screen.getByText('Run preview')).toBeDisabled();
    expect(screen.getAllByText('Unknown').length).toBeGreaterThan(0);
  });
});
