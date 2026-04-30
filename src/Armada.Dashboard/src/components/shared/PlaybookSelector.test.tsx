import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import PlaybookSelector from './PlaybookSelector';

vi.mock('../../api/client', () => ({
  listPlaybooks: vi.fn().mockResolvedValue({
    objects: [
      {
        id: 'plb_123',
        tenantId: null,
        userId: null,
        fileName: 'engineering.md',
        description: 'Engineering standards',
        content: '# Engineering',
        active: true,
        createdUtc: '2026-04-29T00:00:00Z',
        lastUpdateUtc: '2026-04-29T00:00:00Z',
      },
    ],
  }),
}));

vi.mock('../../context/LocaleContext', () => ({
  useLocale: () => ({
    t: (value: string, vars?: Record<string, string | number>) => {
      if (!vars) return value;
      return value.replace(/\{\{(\w+)\}\}/g, (_, key: string) => String(vars[key] ?? ''));
    },
    formatRelativeTime: () => 'just now',
  }),
}));

describe('PlaybookSelector', () => {
  it('updates the collapsed summary counts after playbooks load', async () => {
    render(
      <MemoryRouter>
        <PlaybookSelector value={[]} onChange={() => undefined} />
      </MemoryRouter>,
    );

    expect(screen.getByText('Loading playbooks...')).toBeInTheDocument();
    expect(await screen.findByText('1 available')).toBeInTheDocument();
    expect(screen.getByText('0 selected')).toBeInTheDocument();
  });

  it('starts collapsed and can be expanded', async () => {
    const user = userEvent.setup();

    render(
      <MemoryRouter>
        <PlaybookSelector value={[]} onChange={() => undefined} />
      </MemoryRouter>,
    );

    expect(screen.getByRole('button', { name: 'Show' })).toHaveAttribute('aria-expanded', 'false');
    expect(screen.getByText('Expand this section to browse, order, and attach playbooks.')).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Available' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Show' }));

    expect(screen.getByRole('button', { name: 'Hide' })).toHaveAttribute('aria-expanded', 'true');
    expect(await screen.findByRole('heading', { name: 'Available' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Selected' })).toBeInTheDocument();
  });
});
