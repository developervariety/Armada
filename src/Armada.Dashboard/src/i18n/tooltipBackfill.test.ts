import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import catalogSource from '../../../Armada.Server/wwwroot/i18n/armada.json?raw';
import backfillSource from './tooltipBackfill.ts?raw';
import { translateSubtree, type I18nCatalog } from './runtime';
import {
  TOOLTIP_GENERATED_ATTR,
  backfillTooltips,
  installTooltipBackfill,
  isBadgeLike,
  isGeneratedTitle,
} from './tooltipBackfill';

const de: I18nCatalog = {
  defaultLocale: 'en',
  supportedLocales: [],
  locales: {
    de: {
      phrases: {
        '{{label}}: {{detail}}': '{{label}}: {{detail}}',
        'Currently on.': 'Derzeit an.',
        Save: 'Speichern',
        Name: 'Name',
      },
    },
  },
};

function mount(html: string): HTMLElement {
  const root = document.createElement('div');
  root.innerHTML = html;
  document.body.appendChild(root);
  return root;
}

afterEach(() => {
  document.body.innerHTML = '';
});

describe('tooltip backfill: what each element says', () => {
  it('buttons and role=button use their name and description, with no filler words', () => {
    const root = mount(`<button>Save</button><span role="button" aria-label="Open menu">...</span><button disabled>Run</button>
      <button aria-label="Purge" aria-description="Deletes the mission and its logs permanently">x</button>`);
    backfillTooltips(root, 'en', null);
    const [save, run, purge] = Array.from(root.querySelectorAll('button'));
    expect(save.getAttribute('title')).toBe('Save');
    expect(save.getAttribute(TOOLTIP_GENERATED_ATTR)).toBe('true');
    expect(root.querySelector('[role=button]')!.getAttribute('title')).toBe('Open menu');
    expect(run.getAttribute('title')).toBe('Run (unavailable right now)');
    expect(purge.getAttribute('title')).toBe('Purge: Deletes the mission and its logs permanently.');
  });

  it('links say where they go, and whether a new tab opens', () => {
    const root = mount('<a href="/x">Vessels</a><a href="https://x" target="_blank">Docs</a>');
    backfillTooltips(root, 'en', null);
    const [inner, outer] = Array.from(root.querySelectorAll('a'));
    expect(inner.title).toBe('Go to Vessels');
    expect(outer.title).toBe('Open Docs in a new tab');
  });

  it('inputs are described from a wrapping label, label[for], aria-labelledby, a preceding label, or placeholder', () => {
    const root = mount(`
      <label>Name <input id="a" type="text"></label>
      <label for="b">Secret</label><input id="b" type="password">
      <span id="lbl">Search vessels</span><input id="c" type="search" aria-labelledby="lbl">
      <div class="form-group"><label>Fleet Name</label><input id="d"></div>
      <input id="e" placeholder="Filter by name">
      <textarea id="f" aria-label="Notes"></textarea>`);
    backfillTooltips(root, 'en', null);
    expect(root.querySelector('#a')!.getAttribute('title')).toBe('Name');
    expect(root.querySelector('#b')!.getAttribute('title')).toBe('Secret');
    expect(root.querySelector('#c')!.getAttribute('title')).toBe('Search vessels');
    expect(root.querySelector('#d')!.getAttribute('title')).toBe('Fleet Name');
    expect(root.querySelector('#e')!.getAttribute('title')).toBe('Filter by name');
    expect(root.querySelector('#f')!.getAttribute('title')).toBe('Notes');
  });

  it('never says "type a value"; inputs carry their help text and placeholder instead', () => {
    const root = mount(`
      <div class="form-group"><label>Local Path</label><input id="a" placeholder="/var/armada/repos/my-app.git">
        <p class="text-dim form-help">Where the Admiral keeps its bare clone of the repository.</p></div>
      <div class="form-group"><label>Branch</label><input id="b" placeholder="e.g. main"></div>
      <input id="c" aria-label="Retries" aria-describedby="hint"><span id="hint">How many times to retry a failed mission</span>
      <label class="form-field"><span class="form-label">Token</span><span class="form-help">Sent as a bearer token.</span><input id="d" type="password"></label>
      <div class="form-group"><label>A</label><input id="e"><label>B</label><input id="f"><p class="form-help">Shared help</p></div>
      <input id="g" aria-label="Path" readonly>`);
    backfillTooltips(root, 'en', null);
    const title = (id: string) => root.querySelector(`#${id}`)!.getAttribute('title');
    expect(title('a')).toBe('Local Path: Where the Admiral keeps its bare clone of the repository. Example: /var/armada/repos/my-app.git');
    expect(title('b')).toBe('Branch: e.g. main.');
    expect(title('c')).toBe('Retries: How many times to retry a failed mission.');
    expect(title('d')).toBe('Token: Sent as a bearer token.');
    // Help shared by several controls is ambiguous, so it is not attributed to either one.
    expect(title('e')).toBe('A');
    expect(title('g')).toBe('Path (read only)');
    for (const el of Array.from(root.querySelectorAll('[title]'))) expect(el.getAttribute('title')).not.toMatch(/type a value/);
  });

  it('selects include the current option, and the label text excludes the option list', () => {
    const root = mount('<label>Provider <select><option>OpenAI</option><option selected>Ollama</option></select></label>');
    backfillTooltips(root, 'en', null);
    const select = root.querySelector('select')!;
    expect(select.title).toBe('Provider: Currently Ollama.');
    // The label describes the field it labels.
    expect(root.querySelector('label')!.title).toBe('Provider: Currently Ollama.');
  });

  it('selects add the chosen option\'s own title and the field help', () => {
    const root = mount(`<div class="form-group"><label>Landing Mode</label><select>
      <option title="Merge into the working directory on this machine.">Local Merge</option></select>
      <p class="form-help">How finished work is landed.</p></div>`);
    backfillTooltips(root, 'en', null);
    expect(root.querySelector('select')!.title)
      .toBe('Landing Mode: Currently Local Merge. Merge into the working directory on this machine. How finished work is landed.');
  });

  it('checkboxes and switches say their state and what a click does', () => {
    const root = mount('<label><input type="checkbox" checked> Enabled</label><button role="switch" aria-checked="false">Dark mode</button>');
    backfillTooltips(root, 'en', null);
    expect(root.querySelector('input')!.title).toBe('Enabled: Currently on.');
    expect(root.querySelector('[role=switch]')!.getAttribute('title')).toBe('Dark mode: Currently off.');
  });

  it('cells, headers and badges carry their full text; empty cells get nothing', () => {
    const root = mount(`<table><thead><tr><th>Base URL</th></tr></thead>
      <tbody><tr><td><span>http://localhost:11434/a/very/long/path</span></td><td></td></tr></tbody></table>
      <span class="tag healthy">Healthy</span><span class="readiness-type-pill">Build</span><div class="backlog-group-pills">x</div>`);
    backfillTooltips(root, 'en', null);
    const tds = root.querySelectorAll('td');
    expect(root.querySelector('th')!.title).toBe('Base URL');
    expect(tds[0].title).toBe('http://localhost:11434/a/very/long/path');
    expect(tds[1].hasAttribute('title')).toBe(false);
    expect(root.querySelector('.tag')!.getAttribute('title')).toBe('Healthy');
    expect(root.querySelector('.readiness-type-pill')!.getAttribute('title')).toBe('Build');
    expect(root.querySelector('.backlog-group-pills')!.hasAttribute('title')).toBe(false);
  });

  it('cells and badges keep a titled ancestor\'s hover text on a second line', () => {
    const root = mount(`<table><tr title="Open the mission"><td>Fix login</td></tr></table>
      <span role="button" title="View health detail"><span class="tag">Healthy</span></span>`);
    backfillTooltips(root, 'en', null);
    expect(root.querySelector('td')!.title).toBe('Fix login\nOpen the mission');
    expect(root.querySelector('.tag')!.getAttribute('title')).toBe('Healthy\nView health detail');
  });

  it('tabs, options, summaries and sortable headers', () => {
    const root = mount(`<div role="tab" aria-selected="false">Logs</div><div role="option">Opus</div>
      <details><summary>Advanced</summary></details>
      <table><tr><th aria-sort="none"><button class="th-sort-btn">Name</button></th></tr></table>`);
    backfillTooltips(root, 'en', null);
    expect(root.querySelector('[role=tab]')!.getAttribute('title')).toBe('Show the Logs tab');
    expect(root.querySelector('[role=option]')!.getAttribute('title')).toBe('Choose Opus');
    expect(root.querySelector('summary')!.title).toBe('Advanced: click to show or hide the details.');
    expect(root.querySelector('.th-sort-btn')!.getAttribute('title')).toBe('Sort by Name');
  });

  it('recognizes badge-like class names used by the dashboard', () => {
    const el = document.createElement('span');
    for (const cls of ['tag', 'badge badge-info', 'auth-badge', 'workspace-status-pill', 'ask-work-chip', 'chat-tool-status']) {
      el.className = cls;
      expect(isBadgeLike(el)).toBe(true);
    }
    for (const cls of ['git-sync-badges', 'tag-entry-row', 'backlog-chip-row', 'chip-count', 'stage', 'tunnel-status-card']) {
      el.className = cls;
      expect(isBadgeLike(el)).toBe(false);
    }
  });
});

describe('tooltip backfill: ownership', () => {
  it('never overwrites an author title, and treats an empty title as missing', () => {
    const root = mount('<button title="Deletes the fleet">Delete</button><span class="tag" title="">Idle</span>');
    backfillTooltips(root, 'en', null);
    const button = root.querySelector('button')!;
    expect(button.title).toBe('Deletes the fleet');
    expect(button.hasAttribute(TOOLTIP_GENERATED_ATTR)).toBe(false);
    expect(root.querySelector('.tag')!.getAttribute('title')).toBe('Idle');
  });

  it('hands the element back when the author sets a title later, and regenerates when the content changes', () => {
    const root = mount('<button>Save</button>');
    const button = root.querySelector('button')!;
    backfillTooltips(root, 'en', null);
    expect(isGeneratedTitle(button)).toBe(true);

    button.textContent = 'Save Changes';
    backfillTooltips(root, 'en', null);
    expect(button.title).toBe('Save Changes');

    button.setAttribute('title', 'Author text');
    backfillTooltips(root, 'en', null);
    expect(button.title).toBe('Author text');
    expect(button.hasAttribute(TOOLTIP_GENERATED_ATTR)).toBe(false);
    expect(isGeneratedTitle(button)).toBe(false);
  });

  it('removes a generated title when there is nothing left to say', () => {
    const root = mount('<table><tr><td>value</td></tr></table>');
    const td = root.querySelector('td')!;
    backfillTooltips(root, 'en', null);
    expect(td.title).toBe('value');
    td.textContent = '';
    backfillTooltips(root, 'en', null);
    expect(td.hasAttribute('title')).toBe(false);
    expect(td.hasAttribute(TOOLTIP_GENERATED_ATTR)).toBe(false);
  });

  it('skips aria-hidden and opted-out subtrees', () => {
    const root = mount('<div aria-hidden="true"><button>Hidden</button></div><div data-tooltip-skip="true"><button>Skip</button></div>');
    backfillTooltips(root, 'en', null);
    for (const button of Array.from(root.querySelectorAll('button'))) expect(button.hasAttribute('title')).toBe(false);
  });
});

describe('tooltip backfill: locale and the document translator', () => {
  it('generates titles in the active locale from already-translated text', () => {
    const root = mount('<button>Save</button>');
    translateSubtree(root, 'de', de);
    backfillTooltips(root, 'de', de);
    expect(root.querySelector('button')!.title).toBe('Speichern');
    const toggle = mount('<label><input type="checkbox" checked> Name</label>');
    backfillTooltips(toggle, 'de', de);
    expect(toggle.querySelector('input')!.title).toBe('Name: Derzeit an.');
  });

  it('the document translator leaves generated titles alone (no stale cached source text)', () => {
    const root = mount('<button>Save</button>');
    backfillTooltips(root, 'de', de);
    const button = root.querySelector('button')!;
    translateSubtree(root, 'de', de);
    expect(button.hasAttribute('data-armada-i18n-orig-title')).toBe(false);
    // The translator translated the text; the backfill follows it.
    backfillTooltips(root, 'de', de);
    expect(button.title).toBe('Speichern');
    // Back to English: the regenerated title is English again, not a pinned German one.
    translateSubtree(root, 'en', de);
    backfillTooltips(root, 'en', de);
    expect(button.title).toBe('Save');
  });

  it('the document translator still translates author titles', () => {
    const root = mount('<button title="Save">x</button>');
    backfillTooltips(root, 'de', de);
    translateSubtree(root, 'de', de);
    expect(root.querySelector('button')!.title).toBe('Speichern');
  });
});

describe('tooltip backfill: observer', () => {
  let frames: FrameRequestCallback[] = [];
  beforeEach(() => {
    frames = [];
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation((cb) => { frames.push(cb); return frames.length; });
    vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(() => {});
  });
  afterEach(() => vi.restoreAllMocks());

  async function flush() {
    await Promise.resolve();
    const pending = frames;
    frames = [];
    for (const cb of pending) cb(0);
  }

  it('backfills added elements and follows text and value changes, batched per frame', async () => {
    const root = mount('');
    const dispose = installTooltipBackfill(root, () => 'en', () => null);

    const table = document.createElement('table');
    table.innerHTML = '<tbody>' + Array.from({ length: 50 }, (_, i) => `<tr><td>row ${i}</td></tr>`).join('') + '</tbody>';
    root.appendChild(table);
    const label = document.createElement('label');
    label.innerHTML = 'Kind <select><option value="a">Embedding</option><option value="b">Inference</option></select>';
    root.appendChild(label);
    await Promise.resolve();
    // Both insertions (51 rows and a label) are handled by one scheduled frame.
    expect(frames.length).toBe(1);
    await flush();
    expect(table.querySelectorAll('td')[49].title).toBe('row 49');
    const select = label.querySelector('select')!;
    expect(select.title).toBe('Kind: Currently Embedding.');

    // Text change inside a cell.
    table.querySelectorAll('td')[3].firstChild!.nodeValue = 'changed';
    await flush();
    expect(table.querySelectorAll('td')[3].title).toBe('changed');

    // A select's value changes without a DOM mutation; the change event drives it.
    select.value = 'b';
    select.dispatchEvent(new Event('change', { bubbles: true }));
    await flush();
    expect(select.title).toBe('Kind: Currently Inference.');

    // Settles: our own title writes do not loop forever.
    await flush();
    await flush();
    expect(frames.length).toBe(0);
    dispose();
  });
});

describe('tooltip backfill: catalog', () => {
  const catalog = JSON.parse(catalogSource) as I18nCatalog;
  const keys = Array.from(backfillSource.matchAll(/\btr\(ctx, '((?:[^'\\]|\\.)*)'/g)).map((m) => m[1]);

  it('finds the generated tooltip templates', () => {
    expect(keys).toContain('{{label}}: {{detail}}');
    expect(keys).not.toContain('{{label}}: type a value.');
    expect(keys.length).toBeGreaterThan(10);
  });

  it('has a translation with intact placeholders for every template in every locale', () => {
    const problems: string[] = [];
    for (const meta of catalog.supportedLocales) {
      if (meta.code === catalog.defaultLocale) continue;
      const phrases = catalog.locales[meta.code]?.phrases ?? {};
      for (const key of keys) {
        const value = phrases[key];
        if (!value) { problems.push(`${meta.code}: missing ${key}`); continue; }
        const want = (key.match(/\{\{\w+\}\}/g) ?? []).sort().join(',');
        const got = (value.match(/\{\{\w+\}\}/g) ?? []).sort().join(',');
        if (want !== got) problems.push(`${meta.code}: placeholders ${key}`);
      }
    }
    expect(problems).toEqual([]);
  });
});
