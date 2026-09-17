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
  description: 'Reviews work', promptTemplateName: 'persona.judge', defaultCaptainId: null, specialist: false,
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

describe('PersonaDetail specialist flag', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(getPersona).mockResolvedValue(judge as never);
    vi.mocked(listPromptTemplates).mockResolvedValue(page([{ name: 'persona.judge' }]) as never);
    vi.mocked(listCaptains).mockResolvedValue(page([]) as never);
    vi.mocked(getPromptTemplate).mockResolvedValue({ name: 'persona.judge', description: '', content: 'x', category: 'Persona', isBuiltIn: true } as never);
    vi.mocked(updatePersona).mockResolvedValue({ ...judge, specialist: true } as never);
  });

  it('shows whether the persona is a specialist', async () => {
    vi.mocked(getPersona).mockResolvedValue({ ...judge, specialist: true } as never);
    renderDetail();
    expect(await screen.findByText('Yes (Premium captains only)')).toBeInTheDocument();
  });

  it('saves the specialist flag from the edit form', async () => {
    renderDetail();
    expect(await screen.findByRole('heading', { name: 'Judge' })).toBeInTheDocument();
    fireEvent.click(screen.getByTitle('Actions'));
    fireEvent.click(await screen.findByRole('button', { name: 'Edit' }));
    const form = screen.getByRole('heading', { name: 'Edit Persona' }).closest('form') as HTMLFormElement;
    const flag = screen.getByRole('checkbox', { name: 'Specialist (requires a Premium captain)' });
    expect(flag).not.toBeChecked();
    fireEvent.click(flag);
    fireEvent.submit(form);

    await waitFor(() => expect(updatePersona).toHaveBeenCalledTimes(1));
    expect(updatePersona).toHaveBeenCalledWith('Judge', expect.objectContaining({ specialist: true, promptTemplateName: 'persona.judge' }));
  });
});
