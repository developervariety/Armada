import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import CaptainQuarantineDialog from './CaptainQuarantineDialog';

const t = (value: string, params?: Record<string, string | number>) =>
  params ? value.replace(/\{\{(\w+)\}\}/g, (_, key: string) => String(params[key] ?? '')) : value;

describe('CaptainQuarantineDialog', () => {
  it('does not submit without a reason and shows the error', async () => {
    const user = userEvent.setup();
    const onSubmit = vi.fn();
    render(<CaptainQuarantineDialog open captainName="worker-1" t={t} onSubmit={onSubmit} onCancel={() => undefined} />);

    await user.click(screen.getByRole('button', { name: 'Quarantine' }));

    expect(onSubmit).not.toHaveBeenCalled();
    expect(screen.getByRole('alert')).toHaveTextContent('A reason is required.');
  });

  it('submits a duration hold with the entered reason', async () => {
    const user = userEvent.setup();
    const onSubmit = vi.fn();
    render(<CaptainQuarantineDialog open captainName="worker-1" t={t} onSubmit={onSubmit} onCancel={() => undefined} />);

    await user.type(screen.getByLabelText('Reason'), 'provider out of balance');
    await user.clear(screen.getByLabelText('Minutes'));
    await user.type(screen.getByLabelText('Minutes'), '30');
    await user.click(screen.getByRole('button', { name: 'Quarantine' }));

    expect(onSubmit).toHaveBeenCalledWith({ reason: 'provider out of balance', durationMinutes: 30 });
  });

  it('submits an indefinite hold and names the captain', async () => {
    const user = userEvent.setup();
    const onSubmit = vi.fn();
    render(<CaptainQuarantineDialog open captainName="worker-1" t={t} onSubmit={onSubmit} onCancel={() => undefined} />);

    expect(screen.getByText(/Hold "worker-1" out of assignment/)).toBeInTheDocument();
    await user.type(screen.getByLabelText('Reason'), 'hold');
    await user.selectOptions(screen.getByLabelText('Hold'), 'indefinite');
    await user.click(screen.getByRole('button', { name: 'Quarantine' }));

    expect(onSubmit).toHaveBeenCalledWith({ reason: 'hold' });
  });

  it('renders nothing when closed and cancels without submitting', async () => {
    const user = userEvent.setup();
    const onSubmit = vi.fn();
    const onCancel = vi.fn();
    const { rerender } = render(<CaptainQuarantineDialog open={false} captainName="worker-1" t={t} onSubmit={onSubmit} onCancel={onCancel} />);
    expect(screen.queryByRole('dialog')).toBeNull();

    rerender(<CaptainQuarantineDialog open captainName="worker-1" t={t} onSubmit={onSubmit} onCancel={onCancel} />);
    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(onCancel).toHaveBeenCalled();
    expect(onSubmit).not.toHaveBeenCalled();
  });
});
