// Utilities for copying an inline SVG chart to the clipboard as a PNG image.
//
// The on-page charts colour their shapes with CSS custom properties (e.g. var(--accent)) so they stay
// theme-reactive. A serialized SVG loaded as a standalone image has no access to the page's CSS, so the
// clone is walked and every var(...) fill/stroke is resolved to a concrete colour before rasterizing.

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
 * Copy an inline SVG element to the clipboard as a PNG image. Returns true on success.
 * The image is rasterized at 2x for crispness and painted over the supplied background colour.
 */
export async function copySvgToClipboard(
  svg: SVGSVGElement | null,
  options?: { background?: string; scale?: number },
): Promise<boolean> {
  try {
    if (!svg) return false;
    const clipboard = navigator.clipboard as (Clipboard & { write?: (items: ClipboardItem[]) => Promise<void> }) | undefined;
    const ClipboardItemCtor = (window as unknown as { ClipboardItem?: typeof ClipboardItem }).ClipboardItem;
    if (!clipboard || !clipboard.write || !ClipboardItemCtor) return false;

    const scale = options?.scale ?? 2;
    const rect = svg.getBoundingClientRect();
    const viewBox = svg.viewBox && svg.viewBox.baseVal;
    const baseWidth = rect.width || (viewBox ? viewBox.width : 800);
    const aspect = viewBox && viewBox.width ? viewBox.height / viewBox.width : (rect.height && rect.width ? rect.height / rect.width : 0.25);
    const baseHeight = baseWidth * aspect;

    const clone = svg.cloneNode(true) as SVGSVGElement;
    clone.setAttribute('width', String(baseWidth));
    clone.setAttribute('height', String(baseHeight));

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

    const xml = new XMLSerializer().serializeToString(clone);
    const svgBlob = new Blob([xml], { type: 'image/svg+xml;charset=utf-8' });
    const url = URL.createObjectURL(svgBlob);

    try {
      const image = new Image();
      image.width = baseWidth;
      image.height = baseHeight;
      await new Promise<void>((resolve, reject) => {
        image.onload = () => resolve();
        image.onerror = () => reject(new Error('svg image load failed'));
        image.src = url;
      });

      const canvas = document.createElement('canvas');
      canvas.width = Math.max(1, Math.round(baseWidth * scale));
      canvas.height = Math.max(1, Math.round(baseHeight * scale));
      const ctx = canvas.getContext('2d');
      if (!ctx) return false;
      ctx.fillStyle = background;
      ctx.fillRect(0, 0, canvas.width, canvas.height);
      ctx.drawImage(image, 0, 0, canvas.width, canvas.height);

      const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob((b) => resolve(b), 'image/png'));
      if (!blob) return false;

      await clipboard.write([new ClipboardItemCtor({ 'image/png': blob })]);
      return true;
    } finally {
      URL.revokeObjectURL(url);
    }
  } catch {
    return false;
  }
}
