import { act, renderHook, waitFor } from '@testing-library/react';

const getVesselGitStatus = vi.fn();
const getVesselBranchCount = vi.fn();

vi.mock('../api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/client')>()),
  getVesselGitStatus: (...args: unknown[]) => getVesselGitStatus(...args),
  getVesselBranchCount: (...args: unknown[]) => getVesselBranchCount(...args),
}));

import { eachLimited, useVesselSyncStatus, VESSEL_SYNC_TTL_MS } from './useVesselSyncStatus';

describe('eachLimited', () => {
  it('never runs more than the limit at once and visits every item', async () => {
    let running = 0;
    let peak = 0;
    const seen: number[] = [];
    await eachLimited([1, 2, 3, 4, 5, 6, 7], 3, async (n) => {
      running += 1;
      peak = Math.max(peak, running);
      await new Promise((r) => setTimeout(r, 1));
      seen.push(n);
      running -= 1;
    });
    expect(peak).toBe(3);
    expect(seen.sort()).toEqual([1, 2, 3, 4, 5, 6, 7]);
  });
});

describe('useVesselSyncStatus', () => {
  beforeEach(() => {
    getVesselGitStatus.mockReset();
    getVesselBranchCount.mockReset();
    getVesselGitStatus.mockImplementation(async (id: string) => ({ vesselId: id, commitsAhead: 1, commitsBehind: 2 }));
    getVesselBranchCount.mockResolvedValue(3);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('reads only the vessels on screen', async () => {
    const { result } = renderHook(() => useVesselSyncStatus(['vsl_a', 'vsl_b'], 1));
    await waitFor(() => expect(result.current.branchCounts.vsl_b).toBe(3));
    expect(getVesselGitStatus.mock.calls.map((c) => c[0]).sort()).toEqual(['vsl_a', 'vsl_b']);
    expect(result.current.gitStatus.vsl_a).toEqual({ ahead: 1, behind: 2 });
  });

  it('does not read a vessel again on a reload within the lifetime, but does after it and on refresh', async () => {
    const start = Date.now();
    const now = vi.spyOn(Date, 'now').mockReturnValue(start);
    const { result, rerender } = renderHook(({ key }) => useVesselSyncStatus(['vsl_a'], key), { initialProps: { key: 1 } });
    await waitFor(() => expect(result.current.branchCounts.vsl_a).toBe(3));
    expect(getVesselGitStatus).toHaveBeenCalledTimes(1);

    rerender({ key: 2 });
    expect(getVesselGitStatus).toHaveBeenCalledTimes(1);

    now.mockReturnValue(start + VESSEL_SYNC_TTL_MS);
    rerender({ key: 3 });
    await waitFor(() => expect(getVesselGitStatus).toHaveBeenCalledTimes(2));

    act(() => result.current.refresh());
    await waitFor(() => expect(getVesselGitStatus).toHaveBeenCalledTimes(3));
    now.mockRestore();
  });

  it('keeps going when a read fails', async () => {
    getVesselGitStatus.mockRejectedValue(new Error('offline'));
    const { result } = renderHook(() => useVesselSyncStatus(['vsl_a'], 1));
    await waitFor(() => expect(result.current.branchCounts.vsl_a).toBe(3));
    expect(result.current.gitStatus.vsl_a).toEqual({ ahead: null, behind: null });
  });
});
