import { render, screen, waitFor } from '@testing-library/react';
import Credentials from './Credentials';
import Tenants from './Tenants';
import Users from './Users';
import { listCredentials, listTenants, listUsers } from '../../api/client';

// The admin lists used to call their routes with no page size, so the server's default page of 10 was shown as the
// whole set ("10 records"). Each list must now read every page and count the true total.

const translate = (text: string, params?: Record<string, string | number | null | undefined>) => {
  if (!params) return text;
  return Object.entries(params).reduce(
    (current, [key, value]) => current.split(`{{${key}}}`).join(value == null ? '' : String(value)),
    text,
  );
};
const localeValue = { t: translate, formatDateTime: (v: string | null | undefined) => v ?? '', formatRelativeTime: (v: string | null | undefined) => v ?? '' };

vi.mock('../../api/client', () => ({
  listCredentials: vi.fn(), createCredential: vi.fn(), updateCredential: vi.fn(), deleteCredential: vi.fn(),
  listUsers: vi.fn(), createUser: vi.fn(), updateUser: vi.fn(), deleteUser: vi.fn(),
  listTenants: vi.fn(), createTenant: vi.fn(), updateTenant: vi.fn(), deleteTenant: vi.fn(),
}));
vi.mock('../../context/LocaleContext', () => ({ useLocale: () => localeValue }));
vi.mock('../../context/NotificationContext', () => ({ useNotifications: () => ({ pushToast: vi.fn() }) }));
vi.mock('../../lib/useAutoRefresh', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../lib/useAutoRefresh')>()),
  useAutoRefresh: () => ({ seconds: 0, setSeconds: vi.fn() }),
}));
vi.mock('../../context/AuthContext', () => ({ useAuth: () => ({ user: { id: 'usr_0', email: 'u0@x' }, isAdmin: true, isTenantAdmin: true }) }));
vi.mock('../../lib/useProxySessionContext', () => ({ useProxySessionContext: () => null }));

/** A fake enumerate route over `total` generated rows, clamped at 1000 per page like the server. */
function pagedRoute<T>(total: number, make: (i: number) => T) {
  return async (params?: { pageNumber?: number; pageSize?: number }) => {
    const pageSize = Math.min(params?.pageSize ?? 10, 1000);
    const pageNumber = params?.pageNumber ?? 1;
    const start = (pageNumber - 1) * pageSize;
    const objects = Array.from({ length: Math.max(0, Math.min(pageSize, total - start)) }, (_, i) => make(start + i));
    return { success: true, pageNumber, pageSize, totalPages: Math.ceil(total / pageSize), totalRecords: total, totalMs: 1, objects } as never;
  };
}

const tenant = (i: number) => ({ id: `ten_${i}`, name: `Tenant ${i}`, active: true, isProtected: false, createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z' });
const user = (i: number) => ({ id: `usr_${i}`, tenantId: 'ten_0', email: `u${i}@x`, firstName: 'U', lastName: `${i}`, isAdmin: false, isTenantAdmin: false, isProtected: false, active: true, createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z' });
const credential = (i: number) => ({ id: `crd_${i}`, tenantId: 'ten_0', userId: 'usr_0', name: `Token ${i}`, bearerToken: '****', isProtected: false, active: true, createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z' });

beforeEach(() => {
  localStorage.clear();
  vi.mocked(listTenants).mockReset().mockImplementation(pagedRoute(1205, tenant));
  vi.mocked(listUsers).mockReset().mockImplementation(pagedRoute(1205, user));
  vi.mocked(listCredentials).mockReset().mockImplementation(pagedRoute(1205, credential));
});

describe('admin lists load every record', () => {
  it('Tenants counts all 1,205 tenants across pages', async () => {
    render(<Tenants />);
    await waitFor(() => expect(screen.getByText(/1,205 records/)).toBeInTheDocument());
    expect(listTenants).toHaveBeenCalledWith(expect.objectContaining({ pageNumber: 2, pageSize: 1000 }));
  });

  it('Users counts all 1,205 users across pages', async () => {
    render(<Users />);
    await waitFor(() => expect(screen.getByText(/1,205 records/)).toBeInTheDocument());
    expect(listUsers).toHaveBeenCalledWith(expect.objectContaining({ pageNumber: 2, pageSize: 1000 }));
  });

  it('Credentials counts all 1,205 credentials and reads every user and tenant for the name maps', async () => {
    render(<Credentials />);
    await waitFor(() => expect(screen.getByText(/1,205 records/)).toBeInTheDocument());
    expect(listCredentials).toHaveBeenCalledWith(expect.objectContaining({ pageNumber: 2 }));
    expect(listUsers).toHaveBeenCalledWith(expect.objectContaining({ pageNumber: 2 }));
    expect(listTenants).toHaveBeenCalledWith(expect.objectContaining({ pageNumber: 2 }));
  });
});
