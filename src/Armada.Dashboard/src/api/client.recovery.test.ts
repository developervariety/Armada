import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { chatWithCaptain, getFleet, setOnUnauthorized } from './client';

beforeEach(() => vi.useFakeTimers());
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); });

describe('Dashboard request recovery', () => {
  it('recovers a read after a transient browser Load failed error', async () => {
    const fetchMock = vi.fn()
      .mockRejectedValueOnce(new TypeError('Load failed'))
      .mockResolvedValue(new Response(JSON.stringify({ Id: 'fleet-example' })));
    vi.stubGlobal('fetch', fetchMock);
    const result = getFleet('fleet-example');
    const assertion = expect(result).resolves.toEqual({ id: 'fleet-example' });
    await vi.runAllTimersAsync();
    await assertion;
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('bounds repeated connection failures and allows a later read to recover', async () => {
    const fetchMock = vi.fn().mockRejectedValue(new TypeError('Load failed'));
    vi.stubGlobal('fetch', fetchMock);
    const assertion = expect(getFleet('fleet-example')).rejects.toThrow('Connection to Armada failed');
    await vi.runAllTimersAsync();
    await assertion;
    expect(fetchMock).toHaveBeenCalledTimes(3);
    fetchMock.mockResolvedValue(new Response(JSON.stringify({ Id: 'fleet-example' })));
    await expect(getFleet('fleet-example')).resolves.toEqual({ id: 'fleet-example' });
  });

  it('keeps the deadline active while reading a stalled response body', async () => {
    let signal: AbortSignal | undefined;
    vi.stubGlobal('fetch', vi.fn(async (_url: string, options: RequestInit) => {
      signal = options.signal as AbortSignal;
      return new Response(new ReadableStream({
        start(controller) {
          controller.enqueue(new TextEncoder().encode('{'));
          signal!.addEventListener('abort', () => controller.error(new DOMException('Aborted', 'AbortError')));
        },
      }));
    }));
    const result = getFleet('fleet-example');
    const assertion = expect(result).rejects.toThrow('Request timed out');
    await vi.advanceTimersByTimeAsync(30000);
    expect(signal?.aborted).toBe(true);
    await assertion;
  });

  it('does not repeat a chat whose delivery is uncertain', async () => {
    const fetchMock = vi.fn().mockRejectedValue(new TypeError('Load failed'));
    vi.stubGlobal('fetch', fetchMock);
    await expect(chatWithCaptain('captain-example', { message: 'hello', history: [] }))
      .rejects.toThrow('Connection to Armada failed');
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it.each([401, 403, 500])('preserves HTTP %s rather than retrying it as a network error', async (status) => {
    const unauthorized = vi.fn();
    setOnUnauthorized(unauthorized);
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ Message: 'Refused' }), { status }));
    vi.stubGlobal('fetch', fetchMock);
    await expect(getFleet('fleet-example')).rejects.toThrow(status === 401 ? 'Unauthorized' : 'Refused');
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(unauthorized).toHaveBeenCalledTimes(status === 401 ? 1 : 0);
  });
});
