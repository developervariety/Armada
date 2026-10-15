import { render, screen, waitFor, within } from '@testing-library/react';
import UserScopeFilter from './UserScopeFilter';
import { listUsers } from '../../api/client';

vi.mock('../../api/client', () => ({ listUsers: vi.fn() }));
vi.mock('../../context/AuthContext', () => ({ useAuth: () => ({ isAdmin: true, isTenantAdmin: true }) }));
vi.mock('../../context/LocaleContext', () => ({ useLocale: () => ({ t: (text: string) => text }) }));

describe('UserScopeFilter', () => {
  it('offers every user, not the first page of 10', async () => {
    const total = 1203;
    vi.mocked(listUsers).mockImplementation(async (params) => {
      const pageSize = Math.min(params?.pageSize ?? 10, 1000);
      const pageNumber = params?.pageNumber ?? 1;
      const start = (pageNumber - 1) * pageSize;
      const objects = Array.from({ length: Math.max(0, Math.min(pageSize, total - start)) }, (_, i) => ({
        id: `usr_${start + i}`, tenantId: 'ten', email: `user${start + i}@example.com`, firstName: null, lastName: null,
      }));
      return { success: true, pageNumber, pageSize, totalPages: 2, totalRecords: total, totalMs: 1, objects } as never;
    });
    render(<UserScopeFilter value="" onChange={() => {}} />);
    const select = screen.getByRole('combobox', { name: 'View records for a specific user' });
    await waitFor(() => expect(within(select).getAllByRole('option')).toHaveLength(total + 1));
  });
});
