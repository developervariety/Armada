import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import TokenUsage from './TokenUsage';
import { getTokenUsage, listCaptains } from '../api/client';

vi.mock('../api/client', () => ({ getTokenUsage: vi.fn(), listCaptains: vi.fn() }));
vi.mock('../context/LocaleContext', () => ({ useLocale: () => ({ locale: 'en', t: (text: string) => text }) }));

const summary = {
  fromUtc: null, toUtc: null, bucketMinutes: 15, recordCount: 1, estimatedCount: 0, inputTokens: 1000, outputTokens: 200, cachedTokens: 600, totalTokens: 1200,
  buckets: [{ bucketStartUtc: new Date().toISOString(), inputTokens: 1000, outputTokens: 200, cachedTokens: 600, totalTokens: 1200, models: [{ model: 'opus', inputTokens: 1000, outputTokens: 200, cachedTokens: 600, totalTokens: 1200 }] }],
  byModel: [{ model: 'opus', inputTokens: 1000, outputTokens: 200, cachedTokens: 600, totalTokens: 1200 }],
};

function Where() {
  const location = useLocation();
  return <div data-testid="where">{location.search}</div>;
}

describe('Token usage page', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    vi.mocked(getTokenUsage).mockResolvedValue(summary as never);
    vi.mocked(listCaptains).mockResolvedValue({ objects: [{ id: 'cpt_ada', name: 'Ada' }] } as never);
  });

  it('filters the totals, heatmap, and charts by the captain or model picker, with one shared time window', async () => {
    render(<MemoryRouter initialEntries={['/activity']}><Routes><Route path="/activity" element={<><TokenUsage /><Where /></>} /></Routes></MemoryRouter>);
    await waitFor(() => expect(screen.getByRole('option', { name: 'Ada' })).toBeInTheDocument());
    await waitFor(() => expect(screen.getByRole('option', { name: 'opus' })).toBeInTheDocument());
    expect(screen.queryByLabelText('Heatmap range')).not.toBeInTheDocument();

    fireEvent.change(screen.getByRole('combobox', { name: /Captain or model/ }), { target: { value: 'captain:cpt_ada' } });
    expect(screen.getByTestId('where')).toHaveTextContent('captainId=cpt_ada');
    await waitFor(() => {
      const calls = vi.mocked(getTokenUsage).mock.calls.map(c => c[0]);
      expect(calls.filter(c => c?.captainId === 'cpt_ada').map(c => c?.bucketMinutes).sort()).toEqual([15, 60]);
    });

    fireEvent.change(screen.getByRole('combobox', { name: /Captain or model/ }), { target: { value: 'model:opus' } });
    expect(screen.getByTestId('where')).toHaveTextContent('model=opus');
    expect(screen.getByTestId('where')).not.toHaveTextContent('captainId');

    fireEvent.click(screen.getByRole('button', { name: 'Last Month' }));
    await waitFor(() => expect(getTokenUsage).toHaveBeenCalledWith(expect.objectContaining({ bucketMinutes: 1440, model: 'opus' })));
  });

  it('always splits usage by model into token types that add up to the total', async () => {
    render(<MemoryRouter><TokenUsage /></MemoryRouter>);
    await waitFor(() => expect(screen.getByText('Usage by model')).toBeInTheDocument());
    expect(screen.getByText('Uncached input')).toBeInTheDocument();
    expect(screen.getByText('Cached input')).toBeInTheDocument();
  });
});
