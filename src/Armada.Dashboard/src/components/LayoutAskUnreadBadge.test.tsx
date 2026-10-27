import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { translateTemplate } from '../i18n/runtime';

const askUnread = { count: 0 };

vi.mock('../lib/useAskUnreadCount', () => ({ useAskUnreadCount: () => askUnread.count }));
vi.mock('../lib/useInboxCount', () => ({
  useInboxCount: () => ({ items: [], count: 0, hasCritical: false, hasWarning: false }),
}));
vi.mock('../context/LocaleContext', () => ({
  useLocale: () => ({
    t: (text: string, params?: Record<string, string | number | null | undefined>) => translateTemplate('en', text, null, params),
  }),
}));
vi.mock('../context/AuthContext', () => ({
  useAuth: () => ({
    user: { user: { id: 'usr_1', email: 'a@b.c' }, tenant: { name: 'Default' } },
    isAdmin: false,
    isTenantAdmin: false,
    logout: vi.fn(),
  }),
}));
vi.mock('../context/ThemeContext', () => ({ useTheme: () => ({ darkMode: false, toggleTheme: vi.fn() }) }));
vi.mock('../context/WebSocketContext', () => ({ useWebSocket: () => ({ connected: true, subscribe: () => () => {} }) }));
vi.mock('../context/NotificationContext', () => ({ useNotifications: () => ({ toasts: [], dismissToast: vi.fn() }) }));
vi.mock('../api/client', () => ({
  getHealth: () => new Promise(() => {}),
  listFleets: () => new Promise(() => {}),
  listVessels: () => new Promise(() => {}),
  listCaptains: () => new Promise(() => {}),
  getProxySessionContext: () => new Promise(() => {}),
  clearProxySessionInstance: vi.fn(),
  logoutProxy: vi.fn(),
}));
vi.mock('./SetupWizard', () => ({
  default: () => null,
  isSetupComplete: () => true,
  clearSetupComplete: vi.fn(),
}));
vi.mock('./DefaultCredentialsBanner', () => ({ default: () => null }));
vi.mock('./shared/LanguageSelector', () => ({ default: () => null }));
vi.mock('./shared/NotificationBell', () => ({ default: () => null }));
vi.mock('./shared/BackgroundActivityIndicator', () => ({ default: () => null }));
vi.mock('./shared/CommandPalette', () => ({ default: () => null }));

import Layout from './Layout';

function askLink(): HTMLElement {
  const label = screen.getAllByText('Ask Armada').find((el) => el.closest('a')?.getAttribute('href') === '/ask');
  if (!label) throw new Error('Ask Armada nav link not found');
  return label.closest('a') as HTMLElement;
}

function renderLayout() {
  return render(
    <MemoryRouter initialEntries={['/dashboard']}>
      <Layout />
    </MemoryRouter>,
  );
}

describe('Layout Ask Armada unread badge', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it('shows the number of unread conversations on the Ask Armada item', () => {
    askUnread.count = 3;
    renderLayout();
    const link = askLink();
    const badge = link.querySelector('.sidebar-nav-badge') as HTMLElement;
    expect(badge).not.toBeNull();
    expect(badge.textContent).toBe('3');
    expect(badge.className).toBe('sidebar-nav-badge');
    expect(badge.getAttribute('aria-label')).toBe('3 unread conversations');
    expect(badge.getAttribute('title')).toBe('3 conversations have unread messages. Click to open Ask Armada.');
    expect(link.className).toContain('has-attention');
  });

  it('hides the badge when no conversation is unread', () => {
    askUnread.count = 0;
    renderLayout();
    const link = askLink();
    expect(link.querySelector('.sidebar-nav-badge')).toBeNull();
    expect(link.className).not.toContain('has-attention');
  });

  it('caps the badge at 99+', () => {
    askUnread.count = 150;
    renderLayout();
    expect(askLink().querySelector('.sidebar-nav-badge')?.textContent).toBe('99+');
  });

  it('shows the badge when the sidebar is collapsed', () => {
    localStorage.setItem('armada_sidebar_collapsed', 'true');
    askUnread.count = 1;
    const { container } = renderLayout();
    expect(container.querySelector('aside.sidebar-collapsed')).not.toBeNull();
    expect(askLink().querySelector('.sidebar-nav-badge')?.textContent).toBe('1');
  });
});
