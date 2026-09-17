import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import TypedDecisionsSettings from './TypedDecisionsSettings';
import { getTypedDecisions, removeTypedDecisionKey, saveTypedDecisionKey, updateTypedDecisions } from '../api/client';
import type { TypedDecisionStatus } from '../types/models';
vi.mock('../api/client', () => ({
  getTypedDecisions: vi.fn(), updateTypedDecisions: vi.fn(), saveTypedDecisionKey: vi.fn(), removeTypedDecisionKey: vi.fn(),
}));
vi.mock('../context/LocaleContext', () => {
  const locale = { t: (s: string, params?: Record<string, string | number>) => params ? Object.entries(params).reduce((text, [k, v]) => text.split(`{{${k}}}`).join(String(v)), s) : s };
  return { useLocale: () => locale };
});

function status(overrides: Partial<TypedDecisionStatus> = {}): TypedDecisionStatus {
  return {
    effectiveMode: 'Gate', effectiveReason: null, storedMode: 'Gate', keyPresent: true, keySource: 'file',
    decisions: [
      { key: 'failure_cause', mode: 'Gate', threshold: 0.9, description: 'Classifies a mission failure cause.' },
      { key: 'leak_hunk', mode: 'Off', threshold: 0.9, description: 'Advisory per-hunk leak check.' },
    ],
    ...overrides,
  };
}

const EXAMPLE_KEY = 'example-not-a-real-key';

describe('Typed decisions settings', () => {
  beforeEach(() => {
    vi.mocked(getTypedDecisions).mockReset();
    vi.mocked(updateTypedDecisions).mockReset();
    vi.mocked(saveTypedDecisionKey).mockReset();
    vi.mocked(removeTypedDecisionKey).mockReset();
  });

  it('shows the no-key banner when the effective mode is Off for want of a key', async () => {
    vi.mocked(getTypedDecisions).mockResolvedValue(status({ effectiveMode: 'Off', effectiveReason: 'typed_decisions_no_key', keyPresent: false, keySource: null }));
    render(<TypedDecisionsSettings />);
    expect(await screen.findByTestId('typed-decisions-no-key')).toHaveTextContent('Off — no Jev key');
    expect(screen.getByText('No key.')).toBeInTheDocument();
    expect(screen.getByText('Remove key')).toBeDisabled();
  });

  it('does not show the banner when a key is present', async () => {
    vi.mocked(getTypedDecisions).mockResolvedValue(status());
    render(<TypedDecisionsSettings />);
    expect(await screen.findByText('Key present (source: key file).')).toBeInTheDocument();
    expect(screen.queryByTestId('typed-decisions-no-key')).not.toBeInTheDocument();
  });

  it('saves a key, clears the field, never shows the key, and refreshes', async () => {
    vi.mocked(getTypedDecisions).mockResolvedValueOnce(status({ effectiveMode: 'Off', effectiveReason: 'typed_decisions_no_key', keyPresent: false, keySource: null }));
    vi.mocked(getTypedDecisions).mockResolvedValue(status());
    vi.mocked(saveTypedDecisionKey).mockResolvedValue(undefined);
    const { container } = render(<TypedDecisionsSettings />);
    const field = await screen.findByLabelText('Jev API key');
    expect(field).toHaveAttribute('type', 'password');
    fireEvent.change(field, { target: { value: EXAMPLE_KEY } });
    fireEvent.click(screen.getByText('Save key'));
    expect(field).toHaveValue('');
    await waitFor(() => expect(saveTypedDecisionKey).toHaveBeenCalledWith(EXAMPLE_KEY));
    expect(await screen.findByRole('status')).toHaveTextContent('Key saved.');
    expect(getTypedDecisions).toHaveBeenCalledTimes(2);
    expect(screen.queryByTestId('typed-decisions-no-key')).not.toBeInTheDocument();
    expect(container.innerHTML).not.toContain(EXAMPLE_KEY);
  });

  it('removes the key file and explains when the environment variable still supplies a key', async () => {
    vi.mocked(getTypedDecisions).mockResolvedValue(status({ keySource: 'env' }));
    vi.mocked(removeTypedDecisionKey).mockResolvedValue({
      fileRemoved: true, environmentSuppliesKey: true, keyPresent: true, keySource: 'env', effectiveMode: 'Gate', effectiveReason: null,
    });
    render(<TypedDecisionsSettings />);
    expect(await screen.findByText(/The environment variable supplies the key/)).toBeInTheDocument();
    fireEvent.click(screen.getByText('Remove key'));
    await waitFor(() => expect(removeTypedDecisionKey).toHaveBeenCalledTimes(1));
    expect(await screen.findByRole('status')).toHaveTextContent('the environment variable still supplies a key');
  });

  it('sends only the changed per-decision mode and threshold, then refreshes', async () => {
    vi.mocked(getTypedDecisions).mockResolvedValue(status());
    vi.mocked(updateTypedDecisions).mockResolvedValue(status());
    render(<TypedDecisionsSettings />);
    const save = await screen.findByText('Save modes');
    expect(save).toBeDisabled();
    fireEvent.change(screen.getByLabelText('Mode for leak_hunk'), { target: { value: 'Shadow' } });
    fireEvent.change(screen.getByLabelText('Threshold for failure_cause'), { target: { value: '0.95' } });
    expect(screen.getByText('Unsaved changes.')).toBeInTheDocument();
    fireEvent.click(save);
    await waitFor(() => expect(updateTypedDecisions).toHaveBeenCalledWith({
      decisions: { failure_cause: { gateThreshold: 0.95 }, leak_hunk: { mode: 'Shadow' } },
    }));
    expect(await screen.findByRole('status')).toHaveTextContent('Typed-decision settings saved.');
    expect(getTypedDecisions).toHaveBeenCalledTimes(2);
  });

  it('sends the global mode and blocks a threshold outside 0 to 1', async () => {
    vi.mocked(getTypedDecisions).mockResolvedValue(status());
    vi.mocked(updateTypedDecisions).mockResolvedValue(status({ storedMode: 'Shadow' }));
    render(<TypedDecisionsSettings />);
    fireEvent.change(await screen.findByLabelText('Global mode'), { target: { value: 'Shadow' } });
    fireEvent.change(screen.getByLabelText('Threshold for leak_hunk'), { target: { value: '1.5' } });
    expect(screen.getByText('Save modes')).toBeDisabled();
    const row = screen.getByLabelText('Threshold for leak_hunk').closest('td')!;
    expect(within(row).getByText('0 to 1')).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Threshold for leak_hunk'), { target: { value: '0.9' } });
    fireEvent.click(screen.getByText('Save modes'));
    await waitFor(() => expect(updateTypedDecisions).toHaveBeenCalledWith({ mode: 'Shadow' }));
  });
});
