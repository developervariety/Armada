import { useState } from 'react';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import PlaybookSelector from './PlaybookSelector';
import type { SelectedPlaybook } from '../../types/models';

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
      {
        id: 'plb_456',
        tenantId: null,
        userId: null,
        fileName: 'release.md',
        description: 'Release checklist',
        content: '# Release',
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

function Harness() {
  const [value, setValue] = useState<SelectedPlaybook[]>([]);
  return (
    <MemoryRouter>
      <PlaybookSelector value={value} onChange={setValue} />
    </MemoryRouter>
  );
}

describe('PlaybookSelector', () => {
  it('shows availability counts after playbooks load', async () => {
    render(
      <MemoryRouter>
        <PlaybookSelector value={[]} onChange={() => undefined} />
      </MemoryRouter>,
    );

    expect(await screen.findByText('Playbook')).toBeInTheDocument();
    expect(screen.getByText('Delivery Mode')).toBeInTheDocument();
    expect(screen.getByRole('combobox', { name: 'Add Playbook' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Manage playbooks' })).toHaveAttribute('href', '/playbooks');
  });

  it('adds and removes playbooks with the compact row workflow', async () => {
    const user = userEvent.setup();

    render(<Harness />);

    await screen.findByRole('combobox', { name: 'Add Playbook' });
    await user.selectOptions(screen.getByRole('combobox', { name: 'Add Playbook' }), 'plb_123');
    await user.click(screen.getByRole('button', { name: 'Add playbook' }));

    expect(screen.getByRole('combobox', { name: 'Playbook 1' })).toHaveValue('plb_123');

    await user.click(screen.getByRole('button', { name: 'Remove playbook 1' }));

    expect(screen.queryByRole('combobox', { name: 'Playbook 1' })).not.toBeInTheDocument();
  });
});
