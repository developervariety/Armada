import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import WorkflowProfiles from './WorkflowProfiles';
import { listFleets, listVessels, listWorkflowProfiles } from '../api/client';

vi.mock('../api/client', async () => (await import('../test/clientMock')).withAllPages({
  listWorkflowProfiles: vi.fn(),
  createWorkflowProfile: vi.fn(),
  updateWorkflowProfile: vi.fn(),
  deleteWorkflowProfile: vi.fn(),
  listFleets: vi.fn(),
  listVessels: vi.fn(),
}));

vi.mock('../context/AuthContext', () => ({
  useAuth: () => ({ isAdmin: true, isTenantAdmin: false }),
}));

vi.mock('../context/LocaleContext', () => {
  const locale = { t: (text: string) => text };
  return { useLocale: () => locale };
});

vi.mock('../context/NotificationContext', () => {
  const notifications = { pushToast: vi.fn() };
  return { useNotifications: () => notifications };
});

describe('Workflow profiles form', () => {
  beforeEach(() => {
    vi.mocked(listWorkflowProfiles).mockResolvedValue({ objects: [] } as never);
    vi.mocked(listFleets).mockResolvedValue({ objects: [] } as never);
    vi.mocked(listVessels).mockResolvedValue({ objects: [] } as never);
  });

  it('puts each checkbox on its own row in the edit form', async () => {
    render(
      <MemoryRouter>
        <WorkflowProfiles />
      </MemoryRouter>,
    );

    fireEvent.click(await screen.findByRole('button', { name: '+ Workflow Profile' }));

    const defaultLabel = screen.getByText('Default for this scope').closest('label');
    const activeLabel = screen.getAllByText('Active').map(node => node.closest('label')).find(label => label?.classList.contains('checkbox-row'));
    expect(defaultLabel).toHaveClass('checkbox-row');
    expect(activeLabel).toHaveClass('checkbox-row');
    expect(defaultLabel).not.toBe(activeLabel);
  });
});
