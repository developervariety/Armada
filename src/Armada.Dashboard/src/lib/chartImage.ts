// Utilities for copying an inline SVG chart to the clipboard as a PNG image, or saving it as a PNG file when the
// browser cannot put an image on the clipboard at all.
//
// The on-page charts colour their shapes with CSS custom properties (e.g. var(--accent)) so they stay
// theme-reactive. A serialized SVG loaded as a standalone image has no access to the page's CSS, so the
// clone is walked and every var(...) fill/stroke is resolved to a concrete colour before rasterizing.
//
// Two browser rules decide whether the clipboard works:
// - The async clipboard API exists only in a secure context (HTTPS or localhost). A dashboard opened over plain
//   HTTP on a LAN address has no navigator.clipboard at all.
// - Safari (and Chrome, more leniently) require clipboard.write to be called within the click's user activation.
//   Rasterizing first and writing afterwards loses it, so the ClipboardItem is built at once around a Promise of
//   the PNG and the browser waits for the image itself.
// Without the async clipboard (plain HTTP), the legacy document.execCommand('copy') still works, as it does for
// CopyButton's text. It can only write strings, so the chart goes on the clipboard as HTML: an <img> whose source
// is the PNG as a data URL. Rich-text targets (documents, mail, wikis) paste it as a picture; targets that accept
// only raw image data may not. When even that is refused, the PNG is downloaded.

/** What happened to the chart image. */
export type ChartImageResult = 'copied' | 'downloaded' | 'failed';

/**
 * Resolve a CSS colour that may be a `var(--name)` reference into a concrete colour string.
 * Non-var values are returned unchanged.
 */
export function resolveCssColor(color: string): string {
  const trimmed = color.trim();
  if (!trimmed.startsWith('var(')) return trimmed;
  const inner = trimmed.slice(4, -1).trim();
  const name = inner.split(',')[0].trim();
  const fallback = inner.includes(',') ? inner.slice(inner.indexOf(',') + 1).trim() : '';
  const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return value || fallback || '#888888';
}

/**
 * Rasterize an inline SVG element to a PNG blob at `scale` (default 2x), over the supplied background colour.
 * Rejects when the SVG cannot be drawn.
 */
export async function renderSvgToPng(svg: SVGSVGElement, options?: { background?: string; scale?: number }): Promise<Blob> {
  const scale = options?.scale ?? 2;
  const rect = svg.getBoundingClientRect();
  const viewBox = svg.viewBox && svg.viewBox.baseVal;
  const baseWidth = rect.width || (viewBox ? viewBox.width : 800);
  const aspect = viewBox && viewBox.width ? viewBox.height / viewBox.width : (rect.height && rect.width ? rect.height / rect.width : 0.25);
  const baseHeight = baseWidth * aspect;

  const clone = svg.cloneNode(true) as SVGSVGElement;
  clone.setAttribute('xmlns', 'http://www.w3.org/2000/svg');
  clone.setAttribute('width', String(baseWidth));
  clone.setAttribute('height', String(baseHeight));
  clone.removeAttribute('style');

  // Resolve any var(...) colours in the clone so they survive rasterization.
  const nodes: Element[] = [clone, ...Array.from(clone.querySelectorAll('*'))];
  for (const node of nodes) {
    for (const attr of ['fill', 'stroke']) {
      const value = node.getAttribute(attr);
      if (value && value.includes('var(')) node.setAttribute(attr, resolveCssColor(value));
    }
  }

  // Opaque background so a paste onto a light surface (docs, chat) is legible in either theme.
  const background = options?.background ? resolveCssColor(options.background) : '#ffffff';
  const bgRect = clone.ownerDocument.createElementNS('http://www.w3.org/2000/svg', 'rect');
  bgRect.setAttribute('x', '0');
  bgRect.setAttribute('y', '0');
  bgRect.setAttribute('width', String(baseWidth));
  bgRect.setAttribute('height', String(baseHeight));
  bgRect.setAttribute('fill', background);
  clone.insertBefore(bgRect, clone.firstChild);

  // Text uses the page font; name it so the standalone image does not fall back to a serif face.
  const fontFamily = getComputedStyle(svg).fontFamily;
  if (fontFamily) clone.setAttribute('font-family', fontFamily);

  const xml = new XMLSerializer().serializeToString(clone);
  const url = URL.createObjectURL(new Blob([xml], { type: 'image/svg+xml;charset=utf-8' }));
  try {
    const image = new Image();
    image.width = baseWidth;
    image.height = baseHeight;
    await new Promise<void>((resolve, reject) => {
      image.onload = () => resolve();
      image.onerror = () => reject(new Error('The chart image could not be drawn.'));
      image.src = url;
    });

    const canvas = document.createElement('canvas');
    canvas.width = Math.max(1, Math.round(baseWidth * scale));
    canvas.height = Math.max(1, Math.round(baseHeight * scale));
    const ctx = canvas.getContext('2d');
    if (!ctx) throw new Error('No canvas context.');
    ctx.fillStyle = background;
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.drawImage(image, 0, 0, canvas.width, canvas.height);

    const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob((b) => resolve(b), 'image/png'));
    if (!blob) throw new Error('The chart image could not be encoded.');
    return blob;
  } finally {
    URL.revokeObjectURL(url);
  }
}

/** Read a blob as a data URL. */
export function blobToDataUrl(blob: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result));
    reader.onerror = () => reject(reader.error ?? new Error('The image could not be read.'));
    reader.readAsDataURL(blob);
  });
}

/**
 * Put an image on the clipboard as HTML (an <img> with a data URL source, plus alt text as plain text) through the
 * legacy document.execCommand('copy'), which works without a secure context. Returns false when the browser
 * refuses the copy.
 */
export function copyImageAsHtml(dataUrl: string, altText: string): boolean {
  if (typeof document.execCommand !== 'function') return false;
  const alt = altText.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;');
  const html = `<img src="${dataUrl}" alt="${alt}">`;
  let written = false;
  const onCopy = (event: ClipboardEvent) => {
    if (!event.clipboardData) return;
    event.clipboardData.setData('text/html', html);
    event.clipboardData.setData('text/plain', altText);
    event.preventDefault();
    written = true;
  };
  document.addEventListener('copy', onCopy);
  try {
    // execCommand fires the copy event only when something is selected or editable in some browsers (Safari), so
    // select a throwaway element for the duration.
    const holder = document.createElement('textarea');
    holder.value = altText || ' ';
    holder.setAttribute('readonly', '');
    holder.style.position = 'fixed';
    holder.style.left = '-9999px';
    holder.style.top = '-9999px';
    holder.style.opacity = '0';
    document.body.appendChild(holder);
    holder.select();
    try {
      return document.execCommand('copy') && written;
    } finally {
      holder.remove();
    }
  } catch {
    return false;
  } finally {
    document.removeEventListener('copy', onCopy);
  }
}

/** Save a blob as a file through a temporary download link. */
export function downloadBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  link.style.display = 'none';
  document.body.appendChild(link);
  link.click();
  link.remove();
  window.setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/**
 * Copy an inline SVG chart to the clipboard as a PNG image. Without the async clipboard (plain HTTP on a LAN
 * address) or when it refuses the write, copy it as an HTML image through execCommand instead; only when that is
 * refused too, save it as a PNG file. Call it directly from the click handler, before any other await, so the
 * clipboard write keeps the click's user activation.
 */
export async function copyOrDownloadSvgImage(
  svg: SVGSVGElement | null,
  options?: { background?: string; scale?: number; fileName?: string; altText?: string; render?: (svg: SVGSVGElement) => Promise<Blob> },
): Promise<ChartImageResult> {
  if (!svg) return 'failed';
  const render = options?.render ?? ((el: SVGSVGElement) => renderSvgToPng(el, options));
  const png = render(svg);
  png.catch(() => undefined);

  const clipboard = (typeof navigator !== 'undefined' ? navigator.clipboard : undefined) as (Clipboard & { write?: (items: ClipboardItem[]) => Promise<void> }) | undefined;
  const ClipboardItemCtor = (window as unknown as { ClipboardItem?: typeof ClipboardItem }).ClipboardItem;
  if (window.isSecureContext !== false && clipboard?.write && ClipboardItemCtor) {
    try {
      // Built synchronously around the pending PNG, so the write stays inside the click's user activation.
      await clipboard.write([new ClipboardItemCtor({ 'image/png': png })]);
      return 'copied';
    } catch {
      try {
        // Browsers that do not accept a Promise in ClipboardItem take the finished blob.
        await clipboard.write([new ClipboardItemCtor({ 'image/png': await png })]);
        return 'copied';
      } catch {
        // Fall through to the HTML copy.
      }
    }
  }

  let blob: Blob;
  try {
    blob = await png;
  } catch {
    return 'failed';
  }

  try {
    if (copyImageAsHtml(await blobToDataUrl(blob), options?.altText ?? options?.fileName ?? 'Chart')) return 'copied';
  } catch {
    // Fall through to the download.
  }

  try {
    downloadBlob(blob, options?.fileName ?? 'chart.png');
    return 'downloaded';
  } catch {
    return 'failed';
  }
}
