import { fireEvent, render, screen } from '@testing-library/react';
import { beforeEach, expect, test, vi } from 'vitest';
import MissionActionRunDialog from './MissionActionRunDialog';

vi.mock('../../context/LocaleContext', () => ({ useLocale: () => ({ t: (value: string) => value }) }));
const action = { id: 'act_1', name: 'Fix a bug', pipelineId: 'pipe_saved', promptTemplate: 'Fix {{vessel}}', active: true } as never;
const vessels = [{ id: 'vsl_1', name: 'Alpha', active: true }, { id: 'vsl_2', name: 'Beta', active: true }] as never;

beforeEach(() => { vi.stubGlobal('crypto', { randomUUID: vi.fn().mockReturnValue('req_stable') }); });

test('reviews targets and reuses the request ID after an ambiguous submission failure', async () => {
  const start = vi.fn().mockRejectedValueOnce(new Error('Connection lost')).mockResolvedValueOnce(undefined);
  render(<MissionActionRunDialog action={action} vessels={vessels} onClose={vi.fn()} onStart={start} />);
  fireEvent.click(screen.getByLabelText(/Alpha/));
  expect(screen.getByText('Fix {{vessel}}')).toBeInTheDocument();
  expect(screen.getByText(/Selected targets: 1\/50/)).toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Start run' }));
  await screen.findByRole('alert');
  fireEvent.click(screen.getByRole('button', { name: 'Start run' }));
  await screen.findByText(/Selected targets: 1\/50/);
  expect(start).toHaveBeenNthCalledWith(1, { requestId: 'req_stable', vesselIds: ['vsl_1'] });
  expect(start).toHaveBeenNthCalledWith(2, { requestId: 'req_stable', vesselIds: ['vsl_1'] });
});
