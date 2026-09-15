import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import MissionHistoryChart from './MissionHistoryChart';
import { getMissionHistory } from '../api/client';

vi.mock('../api/client', () => ({
  getMissionHistory: vi.fn(),
}));

vi.mock('../context/LocaleContext', () => {
  const locale = { t: (text: string) => text };
  return { useLocale: () => locale };
});

const emptyHistory = { totalCount: 0, completeCount: 0, failedCount: 0, otherCount: 0, buckets: [] };

function lastRequest() {
  const calls = vi.mocked(getMissionHistory).mock.calls;
  const params = calls[calls.length - 1][0] as { fromUtc: string; toUtc: string; bucketMinutes: number };
  return {
    hours: (new Date(params.toUtc).getTime() - new Date(params.fromUtc).getTime()) / 3600000,
    bucketMinutes: params.bucketMinutes,
  };
}

describe('MissionHistoryChart', () => {
  beforeEach(() => {
    vi.mocked(getMissionHistory).mockResolvedValue(emptyHistory as never);
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  it.each([
    ['Last Hour', 1, 1],
    ['Last Day', 24, 15],
    ['Last Week', 168, 60],
    ['Last Month', 720, 360],
  ])('%s requests %i hours in %i-minute buckets', async (label, hours, bucketMinutes) => {
    render(<MissionHistoryChart vessels={[]} fleets={[]} />);
    await waitFor(() => expect(getMissionHistory).toHaveBeenCalled());

    fireEvent.click(screen.getByRole('button', { name: label }));

    await waitFor(() => expect(lastRequest()).toEqual({ hours, bucketMinutes }));
  });

  it('reloads when the host refresh key advances', async () => {
    const { rerender } = render(<MissionHistoryChart vessels={[]} fleets={[]} refreshKey={0} />);
    await waitFor(() => expect(getMissionHistory).toHaveBeenCalledTimes(1));

    rerender(<MissionHistoryChart vessels={[]} fleets={[]} refreshKey={1} />);

    await waitFor(() => expect(getMissionHistory).toHaveBeenCalledTimes(2));
  });

  it('leaves the reload to the host when the refresh button has a host refresh', async () => {
    const onRefresh = vi.fn();
    render(<MissionHistoryChart vessels={[]} fleets={[]} onRefresh={onRefresh} />);
    await waitFor(() => expect(getMissionHistory).toHaveBeenCalledTimes(1));

    fireEvent.click(screen.getByTitle('Refresh'));

    expect(onRefresh).toHaveBeenCalledTimes(1);
    expect(getMissionHistory).toHaveBeenCalledTimes(1);
  });
});
