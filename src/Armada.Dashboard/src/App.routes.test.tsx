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

  it('sends the notifications path to the inbox', async () => {
    renderAt('/notifications');
    expect(await screen.findByText('inbox page')).toBeInTheDocument();
    expect(screen.getByTestId('location')).toHaveTextContent('/inbox');
  });

  it('routes neither the code index nor the token usage path', async () => {
    for (const path of ['/code-index', '/token-usage']) {
      const { container, unmount } = renderAt(path);
      await new Promise((resolve) => setTimeout(resolve, 50));
      expect(window.location.pathname).toBe(`/dashboard${path}`);
      expect(screen.queryByTestId('location')).not.toBeInTheDocument();
      expect(container).toBeEmptyDOMElement();
      unmount();
    }
  });
});
