import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { expect, test, vi } from 'vitest';
import MissionActionRunCard from './MissionActionRunCard';

vi.mock('../../context/LocaleContext', () => ({ useLocale: () => ({ t: (value: string) => value }) }));
function renderRun(runStatus: string, targetStatus: string, voyageId: string | null) {
  const run = { id: 'run_1', snapshot: { actionName: 'Fix a bug', targets: [{ targetId: 'target_1', vesselId: 'vsl_1', vesselNameSnapshot: 'Alpha', renderedPrompt: 'Fix issue', resolvedPipelineId: 'pipe_frozen', resolvedPipelineHash: 'hash_1' }] }, state: { status: runStatus, targetStates: [{ targetId: 'target_1', status: targetStatus, voyageId, reason: null, error: null }] } } as never;
  render(<MemoryRouter><MissionActionRunCard run={run} onCancel={vi.fn()} /></MemoryRouter>);
}

test('shows produced work as distinct from delivery and links the exact voyage', () => {
  renderRun('PartiallyCompleted', 'WorkProduced', 'voy_exact');
  expect(screen.getByText('Work was produced. Delivery is not confirmed.')).toBeInTheDocument();
  expect(screen.getByRole('link', { name: 'voy_exact' })).toHaveAttribute('href', '/voyages/voy_exact');
  expect(screen.queryByText('Delivered')).not.toBeInTheDocument();
});

test('uncertain run is blocked and offers no retry action', () => {
  renderRun('Uncertain', 'Uncertain', null);
  expect(screen.getByText('The run outcome is unknown. It is blocked and will not be retried automatically.')).toBeInTheDocument();
  expect(screen.getByText('The outcome is unknown. Do not retry this target automatically.')).toBeInTheDocument();
  expect(screen.queryByRole('button', { name: /retry/i })).not.toBeInTheDocument();
});

test('completed voyage without source work evidence does not claim delivery', () => {
  renderRun('Completed', 'Completed', null);
  expect(screen.getByText('Voyage completed. Source delivery is not confirmed.')).toBeInTheDocument();
  expect(screen.queryByText(/Work was produced/)).not.toBeInTheDocument();
});
