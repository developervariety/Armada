import { render, screen } from '@testing-library/react';
import { beforeEach, expect, test, vi } from 'vitest';
import WorkspaceTerminal from './WorkspaceTerminal';

const auth = vi.hoisted(() => ({ current: { isAdmin: true, isTenantAdmin: true } }));
vi.mock('../../context/AuthContext', () => ({ useAuth: () => auth.current }));
vi.mock('../../context/LocaleContext', () => ({
  useLocale: () => ({ t: (text: string) => text }),
}));
vi.mock('../../api/client', () => ({ execWorkspaceCommand: vi.fn() }));

beforeEach(() => {
  auth.current = { isAdmin: true, isTenantAdmin: true };
});

test('offers the command box to a global administrator', () => {
  render(<WorkspaceTerminal vesselId="vsl_1" />);
  expect(screen.getByPlaceholderText('Enter a command...')).toBeInTheDocument();
});

test('offers no command box to a tenant administrator, because the server runs commands for global administrators only', () => {
  auth.current = { isAdmin: false, isTenantAdmin: true };
  render(<WorkspaceTerminal vesselId="vsl_1" />);
  expect(screen.queryByPlaceholderText('Enter a command...')).not.toBeInTheDocument();
});
