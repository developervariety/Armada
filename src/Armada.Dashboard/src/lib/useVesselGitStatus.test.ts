import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { useVesselGitStatus, VESSEL_GIT_STATUS_CONCURRENCY } from './useVesselGitStatus';
import { getVesselGitStatus } from '../api/client';

vi.mock('../api/client', () => ({ getVesselGitStatus: vi.fn() }));

function reply(id: string, ahead: number | null, behind: number | null) {
  return { vesselId: id, commitsAhead: ahead, commitsBehind: behind };
}

describe('useVesselGitStatus', () => {
  beforeEach(() => {
    vi.mocked(getVesselGitStatus).mockReset();
  });

  it('reads only the vessels it is given', async () => {
    vi.mocked(getVesselGitStatus).mockImplementation(async (id: string) => reply(id, 1, 0));
    const { result } = renderHook(() => useVesselGitStatus(['vsl_a', 'vsl_b'], 0));

    await waitFor(() => expect(Object.keys(result.current).sort()).toEqual(['vsl_a', 'vsl_b']));
    expect(vi.mocked(getVesselGitStatus).mock.calls.map((call) => call[0]).sort()).toEqual(['vsl_a', 'vsl_b']);
    expect(result.current.vsl_a).toEqual({ ahead: 1, behind: 0 });
  });

  it('reads a vessel again only when the visible set or the refresh key changes', async () => {
    vi.mocked(getVesselGitStatus).mockImplementation(async (id: string) => reply(id, 0, 0));
    const { result, rerender } = renderHook(({ ids, key }) => useVesselGitStatus(ids, key), { initialProps: { ids: ['vsl_a'], key: 0 } });
    await waitFor(() => expect(result.current.vsl_a).toBeDefined());

    rerender({ ids: ['vsl_a'], key: 0 });
    expect(getVesselGitStatus).toHaveBeenCalledTimes(1);

    rerender({ ids: ['vsl_a', 'vsl_b'], key: 0 });
    await waitFor(() => expect(result.current.vsl_b).toBeDefined());
    expect(vi.mocked(getVesselGitStatus).mock.calls.map((call) => call[0])).toEqual(['vsl_a', 'vsl_a', 'vsl_b']);

    rerender({ ids: ['vsl_a', 'vsl_b'], key: 1 });
    await waitFor(() => expect(getVesselGitStatus).toHaveBeenCalledTimes(5));
  });

  it('keeps at most four reads in flight', async () => {
    let inFlight = 0;
    let peak = 0;
    const releases: Array<() => void> = [];
    vi.mocked(getVesselGitStatus).mockImplementation((id: string) => new Promise((resolve) => {
      inFlight++;
      peak = Math.max(peak, inFlight);
      releases.push(() => { inFlight--; resolve(reply(id, 0, 0)); });
    }));
    const ids = Array.from({ length: 10 }, (_, index) => `vsl_${index}`);
    const { result } = renderHook(() => useVesselGitStatus(ids, 0));

    await waitFor(() => expect(releases.length).toBe(VESSEL_GIT_STATUS_CONCURRENCY));
    while (Object.keys(result.current).length < ids.length) {
      const release = releases.shift();
      if (release) await act(async () => { release(); });
      else await act(async () => { await Promise.resolve(); });
    }

    expect(peak).toBe(VESSEL_GIT_STATUS_CONCURRENCY);
    expect(vi.mocked(getVesselGitStatus)).toHaveBeenCalledTimes(10);
  });

  it('leaves a vessel without a status when its read fails, and goes on with the rest', async () => {
    vi.mocked(getVesselGitStatus).mockImplementation(async (id: string) => {
      if (id === 'vsl_bad') throw new Error('boom');
      return reply(id, 2, 5);
    });
    const { result } = renderHook(() => useVesselGitStatus(['vsl_bad', 'vsl_ok'], 0));

    await waitFor(() => expect(Object.keys(result.current).sort()).toEqual(['vsl_bad', 'vsl_ok']));
    expect(result.current.vsl_bad).toEqual({ ahead: null, behind: null });
    expect(result.current.vsl_ok).toEqual({ ahead: 2, behind: 5 });
  });

  it('reads nothing for an empty page', () => {
    renderHook(() => useVesselGitStatus([], 0));
    expect(getVesselGitStatus).not.toHaveBeenCalled();
  });
});
