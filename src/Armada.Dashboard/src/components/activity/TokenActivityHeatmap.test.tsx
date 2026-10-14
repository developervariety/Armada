import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import TokenActivityHeatmap from './TokenActivityHeatmap';
import { getTokenUsage } from '../../api/client';

vi.mock('../../api/client', () => ({ getTokenUsage: vi.fn() }));
vi.mock('../../context/LocaleContext', () => ({ useLocale: () => ({ locale: 'en', t: (text: string, args?: Record<string, unknown>) => text.replace('{{count}}', String(args?.count ?? '')) }) }));

describe('Token activity calendar', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    vi.mocked(getTokenUsage).mockResolvedValue({ recordCount: 1, syntheticCount: 1, buckets: [{ bucketStartUtc: '2026-01-01T00:00:00Z', totalTokens: 4000000000, inputTokens: 100, outputTokens: 20, cachedTokens: 30 }] } as never);
  });
  it('requests independent daily data with Harbor filtering and supports keyboard/day/type/range selection', async () => {
    render(<TokenActivityHeatmap harborId="hbr_test" />);
    await waitFor(() => expect(screen.getByRole('grid')).toBeInTheDocument());
    expect(getTokenUsage).toHaveBeenCalledWith(expect.objectContaining({ bucketMinutes: 1440, harborId: 'hbr_test' }));
    expect(screen.getAllByRole('gridcell')).toHaveLength(365);
    const cells = screen.getAllByRole('gridcell');
    const last = cells[cells.length - 1];
    last.focus();
    fireEvent.keyDown(last, { key: 'ArrowUp' });
    expect(document.activeElement).not.toBe(last);
    fireEvent.change(screen.getByLabelText('Heatmap metric'), { target: { value: 'cached' } });
    expect(screen.getByText('1 synthetic usage records included')).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Heatmap range'), { target: { value: '31' } });
    await waitFor(() => expect(screen.getAllByRole('gridcell')).toHaveLength(31));
  });
  it('reports fetch failure without presenting it as zero activity and allows retry', async () => {
    vi.mocked(getTokenUsage).mockRejectedValueOnce(new Error('offline'));
    render(<TokenActivityHeatmap />);
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('Unable to load token activity.'));
    expect(screen.queryByRole('grid')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Refresh token activity' }));
    await waitFor(() => expect(screen.getByRole('grid')).toBeInTheDocument());
  });
});
