import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';

// Guards the Ask Armada composer geometry: the prompt input and its Send/Stop button must render the
// same height. The button used to stand shorter than the min-height the textarea carried, so the box
// sat taller than the button next to it. Both now read one shared height variable.

const css = readFileSync('./src/App.css', 'utf8');
const baseCss = readFileSync('./src/index.css', 'utf8');

// index.css sets the root font size (html, body, #root); every rem in the composer resolves against it.
const ROOT_FONT_SIZE_PX = 14;
const BORDER_PX = 1;

function readRule(source: string, selector: string): string {
  const start = source.indexOf(`${selector} {`);
  if (start < 0) {
    throw new Error(`Missing CSS rule: ${selector}`);
  }

  const end = source.indexOf('}', start);
  return source.slice(start + selector.length + 1, end);
}

function readProperty(block: string, property: string): string {
  const match = new RegExp(`(?:^|[;{\\s])${property}:\\s*([^;]+);`).exec(block);
  if (!match) {
    throw new Error(`Missing CSS property: ${property}`);
  }

  return match[1].trim();
}

function toPx(length: string): number {
  if (length.endsWith('rem')) {
    return Number.parseFloat(length) * ROOT_FONT_SIZE_PX;
  }

  if (length.endsWith('px') || /^-?\d+(\.\d+)?$/.test(length)) {
    return Number.parseFloat(length);
  }

  throw new Error(`Unsupported CSS length: ${length}`);
}

function evaluateHeight(expression: string): number {
  const inner = expression.trim().replace(/^calc\((.*)\)$/, '$1');
  let total = 0;

  for (const term of inner.split('+')) {
    let product = 1;
    for (const factor of term.split('*')) {
      product *= toPx(factor.trim());
    }
    total += product;
  }

  return total;
}

describe('Ask Armada composer layout', () => {
  it('sizes the prompt input and the Send/Stop button from one shared height', () => {
    const formRule = readRule(css, '.ask-composer-form');
    const inputRule = readRule(css, '.ask-composer-input');
    const buttonRule = readRule(css, '.ask-composer-form .btn');

    expect(readProperty(formRule, 'align-items')).toBe('flex-end');
    expect(readProperty(inputRule, 'min-height')).toBe('var(--ask-composer-control-height)');
    expect(readProperty(buttonRule, 'height')).toBe('var(--ask-composer-control-height)');
    expect(readProperty(buttonRule, 'padding-top')).toBe('0');
    expect(readProperty(buttonRule, 'padding-bottom')).toBe('0');
  });

  it('keeps the shared height equal to a one-line input so neither control outgrows the other', () => {
    const formRule = readRule(css, '.ask-composer-form');
    const inputRule = readRule(css, '.ask-composer-input');
    const baseInputRule = readRule(baseCss, 'input, select, textarea');

    const sharedHeight = readProperty(formRule, '--ask-composer-control-height');
    const fontSize = readProperty(baseInputRule, 'font-size');
    const padding = readProperty(baseInputRule, 'padding');
    const lineHeight = readProperty(inputRule, 'line-height');

    expect(sharedHeight).toBe('calc(0.9rem * 1.4 + 0.6rem * 2 + 2px)');
    expect(fontSize).toBe('0.9rem');
    expect(padding).toBe('0.6rem 0.75rem');
    expect(lineHeight).toBe('1.4');

    const oneLineInputHeight = toPx(fontSize) * Number.parseFloat(lineHeight) + toPx('0.6rem') * 2 + BORDER_PX * 2;

    expect(evaluateHeight(sharedHeight)).toBeCloseTo(oneLineInputHeight, 4);
  });
});
