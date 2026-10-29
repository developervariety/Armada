import { fireEvent, render, screen } from '@testing-library/react';
import { deferred } from '../../test/routeRace';
import RefreshButton from './RefreshButton';

vi.mock('../../context/LocaleContext', () => {
  const locale = { t: (text: string) => text };
  return { useLocale: () => locale };
});

describe('RefreshButton', () => {
  it('spins the icon and leaves the button still while a refresh runs', async () => {
    const pending = deferred<void>();
    render(<RefreshButton onRefresh={() => pending.promise} />);

    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));

    const button = screen.getByRole('button', { name: 'Refresh' });
    const icon = button.querySelector('.refresh-btn-icon');
    expect(button.className).toContain('refreshing');
    expect(icon).not.toBeNull();
    expect(button.className).not.toContain('refresh-btn-icon');
    pending.resolve();
  });
});
