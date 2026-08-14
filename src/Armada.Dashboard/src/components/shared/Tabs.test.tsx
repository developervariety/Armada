import { describe, it, expect, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import Tabs, { type TabDef } from './Tabs';

vi.mock('../../context/LocaleContext', () => ({
  useLocale: () => ({ t: (text: string) => text }),
}));

function renderTabs(initialEntry: string, onRender?: { a: () => void; b: () => void }) {
  const tabs: TabDef[] = [
    { key: 'alpha', label: 'Alpha', render: () => { onRender?.a(); return <div>Alpha panel</div>; } },
    { key: 'beta', label: 'Beta', render: () => { onRender?.b(); return <div>Beta panel</div>; } },
  ];
  return render(
    <MemoryRouter initialEntries={[initialEntry]}>
      <Tabs tabs={tabs} defaultTabKey="alpha" />
    </MemoryRouter>,
  );
}

describe('Tabs', () => {
  it('renders the default tab panel and marks it selected', () => {
    renderTabs('/x');
    expect(screen.getByText('Alpha panel')).toBeInTheDocument();
    expect(screen.queryByText('Beta panel')).not.toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Alpha' })).toHaveAttribute('aria-selected', 'true');
  });

  it('honors the tab query param from the URL', () => {
    renderTabs('/x?tab=beta');
    expect(screen.getByText('Beta panel')).toBeInTheDocument();
    expect(screen.queryByText('Alpha panel')).not.toBeInTheDocument();
  });

  it('switches panels on click and only mounts the active panel', () => {
    const onRender = { a: vi.fn(), b: vi.fn() };
    renderTabs('/x', onRender);
    expect(onRender.a).toHaveBeenCalled();
    expect(onRender.b).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('tab', { name: 'Beta' }));
    expect(screen.getByText('Beta panel')).toBeInTheDocument();
    expect(onRender.b).toHaveBeenCalled();
  });
});
