import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import PersonaDetail from './PersonaDetail';
import { getPersona, getPromptTemplate, listCaptains, listPromptTemplates, updatePersona } from '../api/client';

vi.mock('../api/client', () => ({
  createPersona: vi.fn(),
  deletePersona: vi.fn(),
  getPersona: vi.fn(),
  getPromptTemplate: vi.fn(),
  listCaptains: vi.fn(),
  listPromptTemplates: vi.fn(),
  resetPromptTemplate: vi.fn(),
  updatePersona: vi.fn(),
  updatePromptTemplate: vi.fn(),
}));
vi.mock('../context/AuthContext', () => {
  const auth = { isAdmin: true, isTenantAdmin: true, user: { user: { id: 'usr_1', tenantId: 'ten_a' } } };
  return { useAuth: () => auth };
});
vi.mock('../context/LocaleContext', () => {
  const locale = { t: (text: string) => text, formatDateTime: (v: string) => v, formatRelativeTime: (v: string) => v };
  return { useLocale: () => locale };
});
vi.mock('../context/NotificationContext', () => {
  const notifications = { pushToast: vi.fn() };
  return { useNotifications: () => notifications };
});

const page = (objects: unknown[]) => ({ success: true, pageNumber: 1, pageSize: 9999, totalPages: 1, totalRecords: objects.length, totalMs: 1, objects });
const judge = {
  id: 'prs_judge', tenantId: 'ten_a', userId: 'usr_1', ownershipScope: 'TenantWide', name: 'Judge',
  description: 'Reviews work', promptTemplateName: 'persona.judge', defaultCaptainId: null, minimumTier: 'Premium',
  isBuiltIn: false, active: true, createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z',
};

function renderDetail() {
  return render(
    <MemoryRouter initialEntries={['/personas/Judge']}>
      <Routes>
        <Route path="/personas/:name" element={<PersonaDetail />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('PersonaDetail minimum tier', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(getPersona).mockResolvedValue(judge as never);
    vi.mocked(listPromptTemplates).mockResolvedValue(page([{ name: 'persona.judge' }]) as never);
    vi.mocked(listCaptains).mockResolvedValue(page([]) as never);
    vi.mocked(getPromptTemplate).mockResolvedValue({ name: 'persona.judge', description: '', content: 'x', category: 'Persona', isBuiltIn: true } as never);
    vi.mocked(updatePersona).mockResolvedValue({ ...judge, minimumTier: 'Standard' } as never);
  });

  it('shows the configured minimum tier', async () => {
    vi.mocked(getPersona).mockResolvedValue({ ...judge, minimumTier: 'Premium' } as never);
    renderDetail();
    expect(await screen.findByText('Premium')).toBeInTheDocument();
  });

  it('saves a changed minimum tier from the edit form', async () => {
    renderDetail();
    expect(await screen.findByRole('heading', { name: 'Judge' })).toBeInTheDocument();
    fireEvent.click(screen.getByTitle('Actions'));
    fireEvent.click(await screen.findByRole('button', { name: 'Edit' }));
    const form = screen.getByRole('heading', { name: 'Edit Persona' }).closest('form') as HTMLFormElement;
    const minimumTier = screen.getByRole('combobox', { name: 'Minimum capability tier' });
    expect(minimumTier).toHaveValue('Premium');
    fireEvent.change(minimumTier, { target: { value: 'Standard' } });
    fireEvent.submit(form);

    await waitFor(() => expect(updatePersona).toHaveBeenCalledTimes(1));
    expect(updatePersona).toHaveBeenCalledWith('Judge', expect.objectContaining({ minimumTier: 'Standard', promptTemplateName: 'persona.judge' }));
  });

  it('shows the server refusal reason when a save is refused', async () => {
    vi.mocked(updatePersona).mockRejectedValue(new Error('default_captain_not_found: no captain cpt_other exists for this persona\'s tenant'));
    renderDetail();
    expect(await screen.findByRole('heading', { name: 'Judge' })).toBeInTheDocument();
    fireEvent.click(screen.getByTitle('Actions'));
    fireEvent.click(await screen.findByRole('button', { name: 'Edit' }));
    const form = screen.getByRole('heading', { name: 'Edit Persona' }).closest('form') as HTMLFormElement;
    fireEvent.submit(form);

    expect(await screen.findByText(/default_captain_not_found: no captain cpt_other/)).toBeInTheDocument();
    expect(screen.queryByText('Save failed.')).not.toBeInTheDocument();
  });
});
