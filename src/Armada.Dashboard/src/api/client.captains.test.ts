import { afterEach, describe, expect, it, vi } from 'vitest';
import { restartCaptain } from './client';

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Captain restart', () => {
  it('asks the server to restart the captain in place with one request and never deletes or recreates it', async () => {
    const stored = { id: 'cpt_keep', name: 'kept-captain', modelEndpointId: 'mep_inf', apiBaseUrl: 'https://provider.example.test', defaultPlaybooks: '[{"playbookId":"pbk_a"}]' };
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(stored), { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await restartCaptain('cpt_keep');

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toContain('/api/v1/captains/cpt_keep/restart');
    expect(options.method).toBe('POST');
    expect(result).toEqual(expect.objectContaining({ id: 'cpt_keep', modelEndpointId: 'mep_inf', apiBaseUrl: 'https://provider.example.test' }));
  });

  it('surfaces a refused restart as an error', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(
      JSON.stringify({ Error: 'Conflict', Message: 'Cannot restart captain while state is Working, Planning, or Refining. Stop the captain first.' }),
      { status: 409 },
    )));

    await expect(restartCaptain('cpt_busy')).rejects.toThrow();
  });
});
