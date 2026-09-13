import { afterEach, describe, expect, it, vi } from 'vitest';
import { getSettings, previewUsageRouting } from './client';

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('Usage collection request timeouts', () => {
  for (const [name, request] of [
    ['settings', () => getSettings()],
    ['preview', () => previewUsageRouting({ persona: 'Worker' })],
  ] as const) {
    it(`allows a full collection sweep for ${name}`, async () => {
      vi.useFakeTimers();
      vi.stubGlobal('fetch', vi.fn((_url: string, options: RequestInit) => new Promise<Response>((resolve, reject) => {
        options.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
        setTimeout(() => resolve(new Response(JSON.stringify({ Reason: 'collected' }), { status: 200 })), 120000);
      })));
      const result = request().catch((error: Error) => error);
      await vi.advanceTimersByTimeAsync(120000);
      expect(await result).toEqual({ reason: 'collected' });
    });

    it(`reports an error when ${name} exceeds the bounded wait`, async () => {
      vi.useFakeTimers();
      vi.stubGlobal('fetch', vi.fn((_url: string, options: RequestInit) => new Promise<Response>((_resolve, reject) => {
        options.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
      })));
      const result = request().catch((error: Error) => error);
      await vi.advanceTimersByTimeAsync(150000);
      expect(await result).toEqual(new Error('Request timed out'));
    });
  }
});
