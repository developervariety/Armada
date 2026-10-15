import { translateTemplate, type I18nCatalog } from './catalog';

/**
 * Global tooltip backfill. Every control, label, table cell and badge in the dashboard should explain itself on
 * hover, but most JSX does not set a title. A MutationObserver mounted once (LocaleProvider) gives every matching
 * element that has no author-provided title a generated, translated one and marks it data-tooltip-generated="true".
 *
 * Rules:
 * - An author title (any non-empty title we did not write) is never overwritten. When the author later sets a title
 *   on an element we had backfilled, theirs wins and our marker is dropped.
 * - Generated text carries only information that is actually there: the element's name, its current state, its
 *   aria-description or aria-describedby text, the visible help text in its field group (form-help and similar),
 *   and its placeholder as an example. With nothing more to say it is just the name, never filler such as
 *   "type a value" or "click to activate". Author-written titles are still the goal; this is the safety net.
 * - Generated titles are rebuilt whenever the element's content, label, value or the locale changes.
 * - Generated text is built from the rendered DOM (which the document translator has already localized) and from
 *   catalog templates rendered in the active locale, so the document translator skips generated titles
 *   (isGeneratedTitle) instead of caching them as source text.
 * - Elements inside aria-hidden="true" or data-tooltip-skip="true" subtrees are left alone.
 * - Work is batched to one pass per animation frame and, after the first full pass, only touches the subtrees and
 *   ancestors of what changed, so large tables stay fast.
 */

export const TOOLTIP_GENERATED_ATTR = 'data-tooltip-generated';

/** Elements that get a backfilled title. Badge-like elements are matched separately (see isBadgeLike). */
export const TOOLTIP_SELECTOR = [
  'button',
  'a',
  'input',
  'select',
  'textarea',
  'label',
  'th',
  'td',
  'summary',
  '[role="button"]',
  '[role="tab"]',
  '[role="menuitem"]',
  '[role="menuitemcheckbox"]',
  '[role="menuitemradio"]',
  '[role="option"]',
  '[role="switch"]',
  '[role="checkbox"]',
  '[role="radio"]',
  '[role="link"]',
].join(', ');

/** Candidates for badge-like elements; isBadgeLike filters the class tokens precisely. */
const BADGE_CANDIDATE_SELECTOR = '[class*="badge"], [class*="chip"], [class*="pill"], [class*="tag"], [class*="status"]';

/** A class token naming a badge, chip, pill, tag or status element ("tag", "badge-info", "workspace-status-pill"). */
const BADGE_TOKEN = /^(tag|badge|chip|pill|status)$|-(tag|badge|chip|pill|status)$|^badge-[a-z]+$/;

const SKIP_SELECTOR = '[aria-hidden="true"], [data-tooltip-skip="true"]';

/** Longest generated title; longer text is cut with "...". */
const MAX_TITLE_LENGTH = 500;

/** Above this many changed nodes in one frame a full pass is cheaper than walking each one. */
const FULL_PASS_THRESHOLD = 400;

/** The value we last generated per element; a title that differs from it was written by someone else. */
const generatedValues = new WeakMap<Element, string>();

type Params = Record<string, string | number | null | undefined>;

interface Context {
  locale: string;
  catalog: I18nCatalog | null | undefined;
}

function tr(ctx: Context, key: string, params?: Params): string {
  return translateTemplate(ctx.locale, key, ctx.catalog, params);
}

function collapse(text: string | null | undefined): string {
  return (text ?? '').replace(/\s+/g, ' ').trim();
}

function clip(text: string): string {
  return text.length > MAX_TITLE_LENGTH ? `${text.slice(0, MAX_TITLE_LENGTH - 3).trimEnd()}...` : text;
}

/** True when the element's current title is one this module generated (and nobody has replaced since). */
export function isGeneratedTitle(element: Element): boolean {
  if (element.getAttribute(TOOLTIP_GENERATED_ATTR) !== 'true') return false;
  const title = element.getAttribute('title');
  return title !== null && generatedValues.get(element) === title;
}

/** True when the element carries a badge, chip, pill, tag or status class. */
export function isBadgeLike(element: Element): boolean {
  const className = element.getAttribute('class');
  if (!className) return false;
  for (const token of className.split(/\s+/)) {
    if (token && BADGE_TOKEN.test(token)) return true;
  }
  return false;
}

function isTarget(element: Element): boolean {
  return element.matches(TOOLTIP_SELECTOR) || isBadgeLike(element);
}

/** Visible text of an element, skipping hidden subtrees and the option lists and values of nested controls. */
function visibleText(element: Element, excludeControls: boolean, excludeHints = false): string {
  const parts: string[] = [];
  const walk = (node: Node) => {
    if (node.nodeType === Node.TEXT_NODE) {
      parts.push(node.nodeValue ?? '');
      return;
    }
    if (!(node instanceof Element)) return;
    if (node.getAttribute('aria-hidden') === 'true') return;
    const tag = node.tagName;
    if (tag === 'SCRIPT' || tag === 'STYLE' || tag === 'TEMPLATE') return;
    if (excludeControls && (tag === 'SELECT' || tag === 'TEXTAREA' || tag === 'INPUT' || tag === 'OPTION' || tag === 'DATALIST')) return;
    // A label's name excludes help text nested inside it; the help is carried separately (controlHint).
    if (excludeHints && isHintElement(node)) return;
    if (tag === 'svg' || tag === 'SVG') {
      const svgTitle = node.querySelector('title');
      if (svgTitle) parts.push(` ${svgTitle.textContent ?? ''} `);
      return;
    }
    if (tag === 'IMG') {
      const alt = node.getAttribute('alt');
      if (alt) parts.push(` ${alt} `);
      return;
    }
    // Block-level children read as separate words.
    parts.push(' ');
    for (const child of Array.from(node.childNodes)) walk(child);
    parts.push(' ');
  };
  for (const child of Array.from(element.childNodes)) walk(child);
  return collapse(parts.join(''));
}

function textOfIds(ids: string, doc: Document): string {
  return collapse(ids.split(/\s+/).filter(Boolean)
    .map((id) => doc.getElementById(id))
    .filter((el): el is HTMLElement => el !== null)
    .map((el) => visibleText(el, true))
    .join(' '));
}

function isFormControl(element: Element | null): element is HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement {
  return element instanceof HTMLInputElement || element instanceof HTMLSelectElement || element instanceof HTMLTextAreaElement;
}

/** The control a label names: label[for], a nested control, or (form-group layout) the next sibling control. */
export function findLabelledControl(label: HTMLLabelElement): HTMLElement | null {
  const control = label.control;
  if (control) return control;
  if (label.htmlFor) return null;
  const next = label.nextElementSibling;
  if (isFormControl(next)) return next;
  return null;
}

function findLabelElement(control: Element): HTMLLabelElement | null {
  if (isFormControl(control) && control.labels && control.labels.length > 0) return control.labels[0];
  const wrapping = control.closest('label');
  if (wrapping) return wrapping;
  // Form-group layout: <label>Name</label><input/> without htmlFor.
  const previous = control.previousElementSibling;
  if (previous instanceof HTMLLabelElement && !previous.htmlFor && !previous.control) return previous;
  return null;
}

/**
 * The accessible name of a form control, in order: aria-label, aria-labelledby, its label (label[for], wrapping
 * label, or the label just before it), then placeholder, then name.
 */
export function controlLabel(control: Element): string {
  const aria = collapse(control.getAttribute('aria-label'));
  if (aria) return aria;
  const labelledBy = control.getAttribute('aria-labelledby');
  if (labelledBy) {
    const text = textOfIds(labelledBy, control.ownerDocument);
    if (text) return text;
  }
  const label = findLabelElement(control);
  if (label) {
    const text = visibleText(label, true, true);
    if (text) return text;
  }
  const placeholder = collapse(control.getAttribute('placeholder'));
  if (placeholder) return placeholder;
  const name = collapse(control.getAttribute('name'));
  if (name) return name;
  return '';
}

/** The accessible name of a button, link or other activatable element. */
export function actionLabel(element: Element): string {
  const aria = collapse(element.getAttribute('aria-label'));
  if (aria) return aria;
  const labelledBy = element.getAttribute('aria-labelledby');
  if (labelledBy) {
    const text = textOfIds(labelledBy, element.ownerDocument);
    if (text) return text;
  }
  if (element instanceof HTMLInputElement) return collapse(element.value);
  const text = visibleText(element, false);
  if (text) return text;
  return collapse(element.getAttribute('data-tooltip-label'));
}

function isDisabled(element: Element): boolean {
  if ((element as HTMLButtonElement).disabled === true) return true;
  return element.getAttribute('aria-disabled') === 'true';
}

function withDisabled(ctx: Context, element: Element, text: string): string {
  return isDisabled(element) ? tr(ctx, '{{text}} (unavailable right now)', { text }) : text;
}

/** Longest help text carried into a generated title. */
const MAX_HINT_LENGTH = 260;

/** A class token naming a field's visible help text ("form-help", "settings-hint", "persona-priority-help"). */
const HINT_TOKEN = /^(form-help|field-help|form-hint|field-hint|settings-hint|help-text|hint-text)$|-(help|hint)$/;

/** Containers that group one control with its label and help text. */
const FIELD_GROUP_SELECTOR = '.form-group, .form-field, .field, .setting-row, .settings-row, .wizard-field';

const CONTROL_SELECTOR = 'input:not([type="hidden"]), select, textarea';

function isHintElement(element: Element): boolean {
  const className = element.getAttribute('class');
  if (!className) return false;
  for (const token of className.split(/\s+/)) {
    if (token && HINT_TOKEN.test(token)) return true;
  }
  return false;
}

function clipHint(text: string): string {
  return text.length > MAX_HINT_LENGTH ? `${text.slice(0, MAX_HINT_LENGTH - 3).trimEnd()}...` : text;
}

/** The field group holding this control and no other control, or null. */
function fieldGroupOf(control: Element): Element | null {
  const group = control.closest(FIELD_GROUP_SELECTOR);
  if (!group) return null;
  return group.querySelectorAll(CONTROL_SELECTOR).length === 1 ? group : null;
}

/**
 * Help text that actually describes a control: aria-description, aria-describedby, or the visible help element
 * (form-help and similar) in the field group that holds only this control.
 */
export function controlHint(control: Element): string {
  const description = collapse(control.getAttribute('aria-description'));
  if (description) return clipHint(description);
  const describedBy = control.getAttribute('aria-describedby');
  if (describedBy) {
    const text = textOfIds(describedBy, control.ownerDocument);
    if (text) return clipHint(text);
  }
  const group = fieldGroupOf(control);
  if (group) {
    for (const el of Array.from(group.querySelectorAll('[class]'))) {
      if (!isHintElement(el) || el.closest(SKIP_SELECTOR)) continue;
      const text = visibleText(el, true);
      if (text) return clipHint(text);
    }
  }
  return '';
}

function sameText(a: string, b: string): boolean {
  return a.trim().toLowerCase().replace(/[.:\s]+$/, '') === b.trim().toLowerCase().replace(/[.:\s]+$/, '');
}

/**
 * What a placeholder adds beyond the label: a literal value (a path, URL, number or single token) reads as an
 * example; a phrase ("e.g. main", "Leave blank to use the default") is shown as written; a placeholder that only
 * repeats the label adds nothing.
 */
interface PlaceholderDetail {
  phrase: string;
  example: string;
}

function placeholderDetail(ctx: Context, control: Element, label: string): PlaceholderDetail {
  const placeholder = collapse(control.getAttribute('placeholder'));
  if (!placeholder || sameText(placeholder, label)) return { phrase: '', example: '' };
  const literal = !/\s/.test(placeholder) || /^[\w.+-]+:\/\//.test(placeholder) || /^[~./\\]/.test(placeholder);
  return literal
    ? { phrase: '', example: tr(ctx, 'Example: {{example}}', { example: placeholder }) }
    : { phrase: placeholder, example: '' };
}

/**
 * Join a label with any details (each ends as a sentence) and an optional example (left unpunctuated, so a path
 * or URL is not followed by a period). With no details, the label alone: filler words add nothing.
 */
function withDetails(ctx: Context, label: string, details: Array<string | null | undefined>, example = ''): string {
  const parts: string[] = [];
  for (const raw of details) {
    const part = collapse(raw);
    if (!part || sameText(part, label) || parts.some((p) => sameText(p, part))) continue;
    parts.push(/[.!?:)]$/.test(part) ? part : `${part}.`);
  }
  if (example) parts.push(example);
  if (parts.length === 0) return label;
  return tr(ctx, '{{label}}: {{detail}}', { label, detail: parts.join(' ') });
}

function describeSelect(ctx: Context, select: HTMLSelectElement): string | null {
  const label = controlLabel(select);
  const hint = controlHint(select);
  let state = '';
  if (select.multiple) {
    const chosen = Array.from(select.selectedOptions).map((o) => collapse(o.textContent)).filter(Boolean).join(', ');
    state = chosen ? tr(ctx, 'Currently {{value}}.', { value: chosen }) : tr(ctx, 'Nothing selected.');
  } else {
    const option = select.selectedIndex >= 0 ? select.options[select.selectedIndex] : null;
    const value = collapse(option?.textContent);
    if (value) state = tr(ctx, 'Currently {{value}}.', { value });
    // The chosen option's own title, when the author explained what choosing it does.
    const optionTitle = collapse(option?.getAttribute('title'));
    if (optionTitle) state = `${state} ${optionTitle}`.trim();
  }
  if (!label) return withDetails(ctx, state, [hint]) || null;
  return withDetails(ctx, label, [state, hint]);
}

function describeToggle(ctx: Context, label: string, on: boolean, hint: string): string {
  return withDetails(ctx, label, [on ? tr(ctx, 'Currently on.') : tr(ctx, 'Currently off.'), hint]);
}

function describeInput(ctx: Context, input: HTMLInputElement): string | null {
  const type = (input.getAttribute('type') || 'text').toLowerCase();
  if (type === 'hidden') return null;
  if (type === 'button' || type === 'submit' || type === 'reset' || type === 'image') {
    const label = actionLabel(input) || collapse(input.getAttribute('alt'));
    return label ? withDetails(ctx, label, [collapse(input.getAttribute('aria-description'))]) : null;
  }
  const label = controlLabel(input);
  const hint = controlHint(input);
  if (!label) return hint || null;
  if (type === 'checkbox') return describeToggle(ctx, label, input.checked, hint);
  if (type === 'radio') {
    return withDetails(ctx, label, [input.checked ? tr(ctx, 'Selected.') : tr(ctx, 'Not selected.'), hint]);
  }
  if (type === 'file') return withDetails(ctx, label, [hint || tr(ctx, 'Click to choose a file.')]);
  if (type === 'range') return withDetails(ctx, label, [tr(ctx, 'Currently {{value}}.', { value: input.value }), hint]);
  const readOnly = input.readOnly ? tr(ctx, '{{label}} (read only)', { label }) : label;
  const placeholder = placeholderDetail(ctx, input, label);
  return withDetails(ctx, readOnly, [hint, placeholder.phrase], placeholder.example);
}

function describeControl(ctx: Context, control: Element): string | null {
  if (control instanceof HTMLSelectElement) return describeSelect(ctx, control);
  if (control instanceof HTMLTextAreaElement) {
    const label = controlLabel(control);
    const hint = controlHint(control);
    if (!label) return hint || null;
    const base = control.readOnly ? tr(ctx, '{{label}} (read only)', { label }) : label;
    const placeholder = placeholderDetail(ctx, control, label);
    return withDetails(ctx, base, [hint, placeholder.phrase], placeholder.example);
  }
  if (control instanceof HTMLInputElement) return describeInput(ctx, control);
  return null;
}

/** An activatable element's name plus its aria-description, if any. */
function describeAction(ctx: Context, element: Element): string | null {
  const label = actionLabel(element);
  if (!label) return null;
  return withDetails(ctx, label, [collapse(element.getAttribute('aria-description'))]);
}

/** The generated tooltip for an element, or null when there is nothing useful to say. */
export function describeElement(element: Element, ctx: Context): string | null {
  const tag = element.tagName;
  const role = (element.getAttribute('role') || '').toLowerCase();
  let text: string | null = null;

  if (isFormControl(element)) {
    text = describeControl(ctx, element);
  } else if (tag === 'LABEL') {
    const control = findLabelledControl(element as HTMLLabelElement);
    text = isFormControl(control) ? describeControl(ctx, control) : visibleText(element, true) || null;
  } else if (tag === 'TD' || tag === 'TH') {
    text = visibleText(element, false) || null;
  } else if (role === 'switch' || role === 'checkbox' || role === 'menuitemcheckbox') {
    const label = actionLabel(element);
    const hint = collapse(element.getAttribute('aria-description'));
    if (label) text = describeToggle(ctx, label, element.getAttribute('aria-checked') === 'true', hint);
  } else if (role === 'radio' || role === 'menuitemradio') {
    const label = actionLabel(element);
    if (label) {
      const checked = element.getAttribute('aria-checked') === 'true';
      text = withDetails(ctx, label, [checked ? tr(ctx, 'Selected.') : tr(ctx, 'Not selected.'),
        collapse(element.getAttribute('aria-description'))]);
    }
  } else if (role === 'tab') {
    const label = actionLabel(element);
    if (label) {
      text = element.getAttribute('aria-selected') === 'true'
        ? tr(ctx, '{{label}}: this tab is showing.', { label })
        : tr(ctx, 'Show the {{label}} tab', { label });
    }
  } else if (role === 'option') {
    const label = actionLabel(element);
    if (label) text = tr(ctx, 'Choose {{label}}', { label });
  } else if (tag === 'SUMMARY') {
    const label = actionLabel(element);
    if (label) text = tr(ctx, '{{label}}: click to show or hide the details.', { label });
  } else if (tag === 'A' || role === 'link') {
    const label = actionLabel(element);
    if (label) {
      text = element.getAttribute('target') === '_blank'
        ? tr(ctx, 'Open {{label}} in a new tab', { label })
        : tr(ctx, 'Go to {{label}}', { label });
    }
  } else if (tag === 'BUTTON' || role === 'button' || role === 'menuitem') {
    const label = actionLabel(element);
    if (label) {
      const sortHeader = element.closest('th[aria-sort]');
      text = sortHeader ? tr(ctx, 'Sort by {{label}}', { label }) : describeAction(ctx, element);
    }
  } else if (isBadgeLike(element)) {
    text = visibleText(element, false) || null;
  }

  if (!text) return null;
  text = withDisabled(ctx, element, text);
  // A passive element's title would hide the hover text of a titled ancestor (a DataTable row title, a clickable
  // wrapper around a badge), so carry that text along on a second line.
  if (tag === 'TD' || tag === 'TH' || tag === 'LABEL' || (isBadgeLike(element) && !isFormControl(element))) {
    const inherited = ancestorAuthorTitle(element);
    if (inherited && inherited !== text) text = `${text}\n${inherited}`;
  }
  return clip(text);
}

/** The nearest ancestor title an author set (generated titles do not count). */
function ancestorAuthorTitle(element: Element): string | null {
  for (let node = element.parentElement; node && node !== node.ownerDocument.body; node = node.parentElement) {
    const title = node.getAttribute('title');
    if (title && title.trim() && !isGeneratedTitle(node)) return title.trim();
  }
  return null;
}

function shouldSkip(element: Element): boolean {
  return element.closest(SKIP_SELECTOR) !== null;
}

/** Give one element a generated title, refresh a stale generated one, or leave an author title alone. */
export function applyTooltip(element: Element, ctx: Context): void {
  const current = element.getAttribute('title');
  const marked = element.getAttribute(TOOLTIP_GENERATED_ATTR) === 'true';
  const ours = marked && current !== null && generatedValues.get(element) === current;

  if (current !== null && current.trim() !== '' && !ours) {
    // Author title: theirs wins (and if we had marked this element, the marker is now stale).
    if (marked) {
      element.removeAttribute(TOOLTIP_GENERATED_ATTR);
      generatedValues.delete(element);
    }
    return;
  }

  const text = shouldSkip(element) ? null : describeElement(element, ctx);
  if (!text) {
    if (ours) {
      element.removeAttribute('title');
      element.removeAttribute(TOOLTIP_GENERATED_ATTR);
      generatedValues.delete(element);
    }
    return;
  }

  generatedValues.set(element, text);
  if (current !== text) element.setAttribute('title', text);
  if (!marked) element.setAttribute(TOOLTIP_GENERATED_ATTR, 'true');
}

function collectTargets(root: Element, into: Set<Element>) {
  if (isTarget(root)) into.add(root);
  for (const el of Array.from(root.querySelectorAll(TOOLTIP_SELECTOR))) into.add(el);
  for (const el of Array.from(root.querySelectorAll(BADGE_CANDIDATE_SELECTOR))) {
    if (isBadgeLike(el)) into.add(el);
  }
}

/** Add the targets that depend on a changed element: the element's own subtree, its target ancestors, and the
 *  controls of any label it sits in (a label's text names its control). */
function collectAffected(element: Element, into: Set<Element>) {
  collectTargets(element, into);
  for (let node = element.parentElement; node; node = node.parentElement) {
    if (isTarget(node)) into.add(node);
    if (node instanceof HTMLLabelElement) {
      const control = findLabelledControl(node);
      if (control) into.add(control);
    }
  }
  if (element instanceof HTMLLabelElement) {
    const control = findLabelledControl(element);
    if (control) into.add(control);
  }
  if (isFormControl(element)) {
    const label = findLabelElement(element);
    if (label) into.add(label);
  }
  // Help text names the control in its field group.
  const hint = element.closest('[class]');
  for (let node: Element | null = hint; node; node = node.parentElement) {
    if (!isHintElement(node)) continue;
    const group = node.closest(FIELD_GROUP_SELECTOR);
    if (group) for (const control of Array.from(group.querySelectorAll(CONTROL_SELECTOR))) into.add(control);
    break;
  }
}

/** Backfill every matching element under root. */
export function backfillTooltips(root: Element, locale: string, catalog: I18nCatalog | null | undefined): void {
  const ctx: Context = { locale, catalog };
  const targets = new Set<Element>();
  collectTargets(root, targets);
  for (const el of targets) applyTooltip(el, ctx);
}

/**
 * Watch root and keep tooltips backfilled. Returns a disposer. Runs a full pass now, then one batched pass per
 * animation frame over whatever changed (DOM mutations, plus change/input events for values that change without
 * a DOM mutation, such as a select's current option or a checkbox's checked state).
 */
export function installTooltipBackfill(
  root: HTMLElement,
  getLocale: () => string,
  getCatalog: () => I18nCatalog | null,
): () => void {
  const dirty = new Set<Node>();
  let fullPass = false;
  let frame: number | null = null;
  let disposed = false;

  const run = () => {
    frame = null;
    if (disposed) return;
    const ctx: Context = { locale: getLocale(), catalog: getCatalog() };
    const targets = new Set<Element>();
    if (fullPass || dirty.size > FULL_PASS_THRESHOLD) {
      collectTargets(root, targets);
    } else {
      for (const node of dirty) {
        const element = node instanceof Element ? node : node.parentElement;
        if (element && element.isConnected && root.contains(element)) collectAffected(element, targets);
      }
    }
    fullPass = false;
    dirty.clear();
    for (const el of targets) applyTooltip(el, ctx);
  };

  const schedule = () => {
    if (frame !== null || disposed) return;
    frame = window.requestAnimationFrame(run);
  };

  backfillTooltips(root, getLocale(), getCatalog());

  const observer = new MutationObserver((records) => {
    for (const record of records) {
      if (record.type === 'childList') {
        dirty.add(record.target);
        for (const added of Array.from(record.addedNodes)) dirty.add(added);
      } else {
        dirty.add(record.target);
      }
    }
    if (dirty.size > 0) schedule();
  });
  observer.observe(root, {
    childList: true,
    subtree: true,
    characterData: true,
    attributes: true,
    attributeFilter: ['title', 'aria-label', 'aria-labelledby', 'placeholder', 'value', 'disabled', 'aria-disabled',
      'aria-checked', 'aria-selected', 'checked', 'class', 'target', 'readonly'],
  });

  const onValueChange = (event: Event) => {
    if (event.target instanceof Node) {
      dirty.add(event.target);
      schedule();
    }
  };
  root.addEventListener('change', onValueChange, true);
  root.addEventListener('input', onValueChange, true);

  return () => {
    disposed = true;
    observer.disconnect();
    root.removeEventListener('change', onValueChange, true);
    root.removeEventListener('input', onValueChange, true);
    if (frame !== null) window.cancelAnimationFrame(frame);
  };
}
