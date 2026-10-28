import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { expect, test, vi } from 'vitest';
import PasswordChangeBanner from './PasswordChangeBanner';

vi.mock('../context/LocaleContext', () => ({
  useLocale: () => ({ t: (text: string) => text }),
}));

function renderBanner(passwordChangeRequired: boolean | undefined) {
  render(<MemoryRouter><PasswordChangeBanner passwordChangeRequired={passwordChangeRequired} /></MemoryRouter>);
}

test('links to the password change while the server asks for one', () => {
  renderBanner(true);
  expect(screen.getByRole('link', { name: 'Change password' })).toHaveAttribute('href', '/server?tab=users&changePassword=1');
});

test.each([false, undefined])('shows nothing when the change is not required (%s)', value => {
  renderBanner(value);
  expect(screen.queryByRole('link')).not.toBeInTheDocument();
  expect(screen.queryByRole('status')).not.toBeInTheDocument();
});
