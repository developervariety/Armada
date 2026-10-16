import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { copyOrDownloadSvgImage } from './chartImage';

const PNG = new Blob(['png'], { type: 'image/png' });

class FakeClipboardItem {
  items: Record<string, unknown>;
  constructor(items: Record<string, unknown>) { this.items = items; }
}

function svg(): SVGSVGElement {
  return document.createElementNS('http://www.w3.org/2000/svg', 'svg');
}

describe('copyOrDownloadSvgImage', () => {
  const originalClipboard = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
  let clicked: string[];

  beforeEach(() => {
    clicked = [];
    vi.stubGlobal('ClipboardItem', FakeClipboardItem);
    Object.defineProperty(window, 'isSecureContext', { configurable: true, value: true });
    URL.createObjectURL = vi.fn(() => 'blob:x');
    URL.revokeObjectURL = vi.fn();
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) { clicked.push(this.download); });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
    if (originalClipboard) Object.defineProperty(navigator, 'clipboard', originalClipboard);
    else delete (navigator as unknown as { clipboard?: unknown }).clipboard;
  });

  function setClipboard(write?: (items: unknown[]) => Promise<void>) {
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: write ? { write } : undefined });
  }

  it('writes a ClipboardItem built at once around the pending PNG, keeping the click activation', async () => {
    const write = vi.fn(async (items: unknown[]) => {
      const value = (items[0] as FakeClipboardItem).items['image/png'];
      expect(value).toBeInstanceOf(Promise);
      expect(await value).toBe(PNG);
    });
    setClipboard(write);
    const result = await copyOrDownloadSvgImage(svg(), { render: () => Promise.resolve(PNG) });
    expect(result).toBe('copied');
    expect(write).toHaveBeenCalledTimes(1);
    expect(clicked).toEqual([]);
  });

  it('retries with the finished blob when a Promise value is refused', async () => {
    const write = vi.fn(async (items: unknown[]) => {
      if ((items[0] as FakeClipboardItem).items['image/png'] instanceof Promise) throw new TypeError('no promises');
    });
    setClipboard(write);
    expect(await copyOrDownloadSvgImage(svg(), { render: () => Promise.resolve(PNG) })).toBe('copied');
    expect(write).toHaveBeenCalledTimes(2);
  });

  it('saves a PNG file when there is no clipboard (plain HTTP)', async () => {
    setClipboard(undefined);
    Object.defineProperty(window, 'isSecureContext', { configurable: true, value: false });
    expect(await copyOrDownloadSvgImage(svg(), { fileName: 'chart.png', render: () => Promise.resolve(PNG) })).toBe('downloaded');
    expect(clicked).toEqual(['chart.png']);
  });

  it('saves a PNG file when the clipboard refuses the write', async () => {
    setClipboard(vi.fn(async () => { throw new DOMException('denied', 'NotAllowedError'); }));
    expect(await copyOrDownloadSvgImage(svg(), { fileName: 'x.png', render: () => Promise.resolve(PNG) })).toBe('downloaded');
    expect(clicked).toEqual(['x.png']);
  });

  it('fails when the chart cannot be drawn or is missing', async () => {
    setClipboard(undefined);
    expect(await copyOrDownloadSvgImage(svg(), { render: () => Promise.reject(new Error('bad')) })).toBe('failed');
    expect(await copyOrDownloadSvgImage(null)).toBe('failed');
  });
});
