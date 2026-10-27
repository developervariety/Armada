import { readFileSync } from 'node:fs';

const css = readFileSync('./src/App.css', 'utf8');
const page = readFileSync('./src/pages/Captains.tsx', 'utf8');

function rule(selector: string): string {
  const start = css.indexOf(`${selector} {`);
  expect(start).toBeGreaterThanOrEqual(0);
  return css.slice(start, css.indexOf('}', start));
}

describe('Captains header toolbar layout', () => {
  it('wraps the user filter and the action buttons, including + Captain, in the toolbar row', () => {
    expect(page).toContain('<div className="captains-toolbar">');
    const toolbar = page.slice(page.indexOf('className="captains-toolbar"'));
    const body = toolbar.slice(0, toolbar.indexOf(')}\n      />'));
    expect(body).toContain('<UserScopeFilter');
    expect(body).toContain("{t('Stop All')}");
    expect(body).toContain("+ {t('Captain')}");
    expect(body.trimEnd().endsWith('</div>')).toBe(true);
  });

  it('keeps the toolbar on one centered row', () => {
    const row = rule('.captains-toolbar');
    expect(row).toMatch(/display:\s*flex/);
    expect(row).toMatch(/align-items:\s*center/);
    expect(row).toMatch(/flex-wrap:\s*nowrap/);
  });

  it('gives every select and button the same height', () => {
    const shared = rule('.captains-toolbar > select,\n.captains-toolbar > .btn');
    expect(shared).toMatch(/height:\s*var\(--captains-toolbar-control-height\)/);
    expect(shared).toMatch(/box-sizing:\s*border-box/);
    const select = rule('.captains-toolbar > select');
    expect(select).toMatch(/width:\s*auto/);
    expect(select).toMatch(/padding:\s*0 0\.6rem/);
  });
});
