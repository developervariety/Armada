import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { deferred } from '../../test/routeRace';
import { useDeleteProgress } from '../../lib/useDeleteProgress';
import DeleteProgressDialog from './DeleteProgressDialog';

vi.mock('../../context/LocaleContext', () => {
  const locale = { t: (text: string) => text };
  return { useLocale: () => locale };
});

function Harness({ task }: { task: () => Promise<void> }) {
  const progress = useDeleteProgress();
  return (
    <div>
      <button onClick={() => void progress.run('Deleting vessel', task)}>delete</button>
      <DeleteProgressDialog state={progress.state} onRetry={progress.retry} onDismiss={progress.dismiss} />
    </div>
  );
}

describe('DeleteProgressDialog', () => {
  it('shows the wait notice while the delete runs, ignores a second delete, and closes on success', async () => {
    const pendingDelete = deferred<void>();
    const task = vi.fn(() => pendingDelete.promise);
    render(<Harness task={task} />);

    fireEvent.click(screen.getByText('delete'));
    expect(await screen.findByText('This may take some time.')).toBeTruthy();
    expect(screen.queryByText('Dismiss')).toBeNull();
    fireEvent.click(screen.getByText('delete'));
    expect(task).toHaveBeenCalledTimes(1);

    pendingDelete.resolve();
    await waitFor(() => expect(screen.queryByText('This may take some time.')).toBeNull());
  });

  it('turns into an error with Retry and Dismiss on failure, and Retry runs the delete again', async () => {
    const task = vi.fn()
      .mockRejectedValueOnce(new Error('Vessel is busy'))
      .mockResolvedValueOnce(undefined);
    render(<Harness task={task} />);

    fireEvent.click(screen.getByText('delete'));
    expect(await screen.findByText('Vessel is busy')).toBeTruthy();
    expect(screen.queryByText('This may take some time.')).toBeNull();

    fireEvent.click(screen.getByText('Retry'));
    await waitFor(() => expect(screen.queryByText('Vessel is busy')).toBeNull());
    expect(task).toHaveBeenCalledTimes(2);
  });

  it('closes the error with Dismiss', async () => {
    render(<Harness task={() => Promise.reject(new Error('nope'))} />);
    fireEvent.click(screen.getByText('delete'));
    fireEvent.click(await screen.findByText('Dismiss'));
    await waitFor(() => expect(screen.queryByText('nope')).toBeNull());
  });
});
