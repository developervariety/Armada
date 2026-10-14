import { render, screen } from '@testing-library/react';
import type { ReactNode } from 'react';
import { Outlet, useLocation } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import App from './App';

const { passThrough } = vi.hoisted(() => ({
  passThrough: ({ children }: { children: ReactNode }) => children,
}));
vi.mock('./context/LocaleContext', () => ({ LocaleProvider: passThrough }));
vi.mock('./context/ThemeContext', () => ({ ThemeProvider: passThrough }));
vi.mock('./context/AuthContext', () => ({ AuthProvider: passThrough }));
vi.mock('./context/WebSocketContext', () => ({ WebSocketProvider: passThrough }));
vi.mock('./context/NotificationContext', () => ({ NotificationProvider: passThrough }));
vi.mock('./components/ProtectedRoute', () => ({ default: passThrough }));
vi.mock('./components/shared/LoadingIndicator', () => ({ default: () => <div>loading</div> }));
vi.mock('./pages/Inbox', () => ({ default: () => <div>inbox page</div> }));
vi.mock('./pages/Notifications', () => ({ default: () => <div>notifications page</div> }));
vi.mock('./pages/CodeIndex', () => ({ default: () => <div>code index page</div> }));
vi.mock('./pages/Activity', () => ({ default: () => <div>activity page</div> }));
vi.mock('./pages/Dashboard', () => ({ default: () => <div>home page</div> }));

function LocationProbe() {
  const location = useLocation();
  return <div data-testid="location">{location.pathname + location.search}</div>;
}

vi.mock('./components/Layout', () => ({
  default: () => (
    <>
      <LocationProbe />
      <Outlet />
    </>
  ),
}));

function renderAt(path: string) {
  window.history.pushState({}, '', `/dashboard${path}`);
  return render(<App />);
}

describe('App routes', () => {
  afterEach(() => window.history.pushState({}, '', '/'));

  it('keeps notification history separate from the inbox', async () => {
    renderAt('/notifications');
    expect(await screen.findByText('notifications page')).toBeInTheDocument();
    expect(screen.getByTestId('location')).toHaveTextContent('/notifications');
  });

  it('opens the code index route', async () => {
    renderAt('/code-index');
    expect(await screen.findByText('code index page')).toBeInTheDocument();
    expect(screen.getByTestId('location')).toHaveTextContent('/code-index');
  });

  it('redirects token usage links to the tokens activity source', async () => {
    renderAt('/token-usage');
    expect(await screen.findByText('activity page')).toBeInTheDocument();
    expect(screen.getByTestId('location')).toHaveTextContent('/activity?source=tokens');
  });
});
