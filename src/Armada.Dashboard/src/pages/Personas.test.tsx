import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import Personas from './Personas';
import { createPersona, listPersonas, listPromptTemplates } from '../api/client';

const auth = vi.hoisted(() => ({ current: { isAdmin: false, isTenantAdmin: false, user: { user: { id: 'usr_1', tenantId: 'ten_a' } } } }));

vi.mock('../api/client', async () => (await import('../test/clientMock')).withAllPages({
  listPersonas: vi.fn(),
  listPromptTemplates: vi.fn(),
  createPersona: vi.fn(),
  updatePersona: vi.fn(),
  deletePersona: vi.fn(),
}));
vi.mock('../context/AuthContext', () => ({ useAuth: () => auth.current }));
vi.mock('../context/LocaleContext', () => {
  // One stable locale object, as the real provider supplies, so the page does not reload and clear its error on every render.
  const locale = { t: (text: string) => text, formatDateTime: (v: string) => v, formatRelativeTime: (v: string) => v };
  return { useLocale: () => locale };
});
vi.mock('../context/NotificationContext', () => ({ useNotifications: () => ({ pushToast: vi.fn() }) }));

const page = (objects: unknown[]) => ({ success: true, pageNumber: 1, pageSize: 9999, totalPages: 1, totalRecords: objects.length, totalMs: 1, objects });
const persona = {
  id: 'prs_1', tenantId: 'ten_a', userId: 'usr_2', ownershipScope: 'UserSpecific', name: 'Reviewer',
  description: null, promptTemplateName: 'persona.reviewer', isBuiltIn: false, active: true,
  createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z',
};

function renderPage() {
  return render(<MemoryRouter><Personas /></MemoryRouter>);
}

async function openRowMenu() {
  await screen.findByText('Reviewer');
  fireEvent.click(screen.getByTitle('Actions'));
}

describe('Personas ownership', () => {
  beforeEach(() => {
    vi.mocked(listPersonas).mockResolvedValue(page([persona]) as never);
    vi.mocked(listPromptTemplates).mockResolvedValue(page([{ name: 'persona.reviewer' }]) as never);
    vi.mocked(createPersona).mockReset();
  });

  it('shows the visibility of each persona', async () => {
    auth.current = { isAdmin: false, isTenantAdmin: false, user: { user: { id: 'usr_1', tenantId: 'ten_a' } } };
    renderPage();
    expect(await screen.findByText('Personal')).toBeInTheDocument();
    expect(screen.getByText('Visibility')).toBeInTheDocument();
  });

  it('offers no create, edit, or delete to a user the server refuses', async () => {
    auth.current = { isAdmin: false, isTenantAdmin: false, user: { user: { id: 'usr_2', tenantId: 'ten_a' } } };
    renderPage();
    await openRowMenu();
    expect(screen.queryByText('+ Persona')).not.toBeInTheDocument();
    expect(screen.queryByText('Edit')).not.toBeInTheDocument();
    expect(screen.queryByText('Delete')).not.toBeInTheDocument();
    expect(screen.getByText('View Detail')).toBeInTheDocument();
  });

  it('lets a tenant administrator create a persona with the chosen visibility', async () => {
    auth.current = { isAdmin: false, isTenantAdmin: true, user: { user: { id: 'usr_ta', tenantId: 'ten_a' } } };
    vi.mocked(createPersona).mockResolvedValue({ ...persona, name: 'Planner' } as never);
    renderPage();
    await openRowMenu();
    expect(screen.getByText('Edit')).toBeInTheDocument();
    fireEvent.click(screen.getByText('+ Persona'));
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Planner' } });
    fireEvent.change(screen.getByLabelText('Prompt Template Name'), { target: { value: 'persona.reviewer' } });
    fireEvent.change(screen.getByLabelText('Visibility'), { target: { value: 'UserSpecific' } });
    fireEvent.click(screen.getByText('Save'));
    await waitFor(() => expect(createPersona).toHaveBeenCalledWith({
      name: 'Planner', promptTemplateName: 'persona.reviewer', ownershipScope: 'UserSpecific',
    }));
  });

  it('shows the server refusal reason when a create is refused', async () => {
    auth.current = { isAdmin: false, isTenantAdmin: true, user: { user: { id: 'usr_ta', tenantId: 'ten_a' } } };
    vi.mocked(createPersona).mockRejectedValue(new Error('default_captain_persona_locked: captain cpt_1 does not allow persona Planner'));
    renderPage();
    await openRowMenu();
    fireEvent.click(screen.getByText('+ Persona'));
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Planner' } });
    fireEvent.change(screen.getByLabelText('Prompt Template Name'), { target: { value: 'persona.reviewer' } });
    fireEvent.click(screen.getByText('Save'));

    expect(await screen.findByText(/default_captain_persona_locked: captain cpt_1/)).toBeInTheDocument();
    expect(screen.queryByText('Save failed.')).not.toBeInTheDocument();
  });
});
