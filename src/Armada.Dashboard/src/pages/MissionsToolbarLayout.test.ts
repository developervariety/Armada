import { readFileSync } from 'node:fs';

const css = readFileSync('./src/App.css', 'utf8');
const page = readFileSync('./src/pages/Missions.tsx', 'utf8');

function rule(selector: string): string {
  const start = css.indexOf(`${selector} {`);
  expect(start).toBeGreaterThanOrEqual(0);
  return css.slice(start, css.indexOf('}', start));
}

describe('Missions header toolbar layout', () => {
  it('wraps the status filter, user filter and + Mission button in the toolbar row', () => {
    const toolbar = page.slice(page.indexOf('className="missions-toolbar"'));
    expect(page).toContain('<div className="missions-toolbar">');
    const end = toolbar.indexOf('</div>');
    const body = toolbar.slice(0, end);
    expect(body).toContain("aria-label={t('Filter by status')}");
    expect(body).toContain('<UserScopeFilter');
    expect(body).toContain("+ {t('Mission')}");
  });

  it('keeps the toolbar on one centered row', () => {
    const row = rule('.missions-toolbar');
    expect(row).toMatch(/display:\s*flex/);
    expect(row).toMatch(/align-items:\s*center/);
    expect(row).toMatch(/flex-wrap:\s*nowrap/);
  });

  it('gives both selects and the buttons the same height', () => {
    const shared = rule('.missions-toolbar > select,\n.missions-toolbar > .btn');
    expect(shared).toMatch(/height:\s*var\(--missions-toolbar-control-height\)/);
    expect(shared).toMatch(/box-sizing:\s*border-box/);
    const select = rule('.missions-toolbar > select');
    expect(select).toMatch(/width:\s*auto/);
    expect(select).toMatch(/padding:\s*0 0\.6rem/);
  });
});
