import { readFileSync } from 'node:fs';

const css = readFileSync('./src/App.css', 'utf8');
const page = readFileSync('./src/pages/Vessels.tsx', 'utf8');

function rule(selector: string): string {
  const start = css.indexOf(`${selector} {`);
  expect(start).toBeGreaterThanOrEqual(0);
  return css.slice(start, css.indexOf('}', start));
}

describe('Vessels header toolbar layout', () => {
  it('wraps the user filter and + Vessel button in the toolbar row', () => {
    const toolbar = page.slice(page.indexOf('className="vessels-toolbar"'));
    expect(page).toContain('<div className="vessels-toolbar">');
    const end = toolbar.indexOf('</div>');
    const body = toolbar.slice(0, end);
    expect(body).toContain('<UserScopeFilter');
    expect(body).toContain("+ {t('Vessel')}");
  });

  it('keeps the toolbar on one centered row', () => {
    const row = rule('.vessels-toolbar');
    expect(row).toMatch(/display:\s*flex/);
    expect(row).toMatch(/align-items:\s*center/);
    expect(row).toMatch(/flex-wrap:\s*nowrap/);
  });

  it('gives the select and the buttons the same height', () => {
    const shared = rule('.vessels-toolbar > select,\n.vessels-toolbar > .btn');
    expect(shared).toMatch(/height:\s*var\(--vessels-toolbar-control-height\)/);
    expect(shared).toMatch(/box-sizing:\s*border-box/);
    const select = rule('.vessels-toolbar > select');
    expect(select).toMatch(/width:\s*auto/);
    expect(select).toMatch(/padding:\s*0 0\.6rem/);
  });
});
