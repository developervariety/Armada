import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import PlanningSessionListCard from './PlanningSessionListCard';

vi.mock('../shared/StatusBadge', () => ({
  default: ({ status }: { status: string }) => <span>{status}</span>,
}));

const t = (value: string) => value;

describe('PlanningSessionListCard', () => {
  it('allows ending an active session without triggering row selection', async () => {
    const user = userEvent.setup();
    const onSelect = vi.fn();
    const onEndSession = vi.fn();

    render(
      <PlanningSessionListCard
        t={t}
        sessions={[
          {
            id: 'psn_active',
            tenantId: null,
            userId: null,
            captainId: 'cpt_1',
            vesselId: 'ves_1',
            fleetId: 'flt_1',
            dockId: 'dok_1',
            branchName: 'armada/planning/psn_active',
            title: 'Active planning session',
            status: 'Active',
            pipelineId: null,
            processId: null,
            failureReason: null,
            selectedPlaybooks: [],
            createdUtc: '2026-05-17T00:00:00Z',
            startedUtc: '2026-05-17T00:00:00Z',
            completedUtc: null,
            lastUpdateUtc: '2026-05-17T00:05:00Z',
          },
        ]}
        activeSessionId={undefined}
        endingSessionId={null}
        formatRelativeTime={() => 'just now'}
        resolveCaptainName={() => 'Planner'}
        resolveVesselName={() => 'Repository'}
        resolvePipelineName={() => '-'}
        onSelect={onSelect}
        onEndSession={onEndSession}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'End Session' }));

    expect(onEndSession).toHaveBeenCalledTimes(1);
    expect(onEndSession.mock.calls[0][0].id).toBe('psn_active');
    expect(onSelect).not.toHaveBeenCalled();
  });

  it('shows ending state for sessions already stopping', () => {
    render(
      <PlanningSessionListCard
        t={t}
        sessions={[
          {
            id: 'psn_stopping',
            tenantId: null,
            userId: null,
            captainId: 'cpt_1',
            vesselId: 'ves_1',
            fleetId: 'flt_1',
            dockId: 'dok_1',
            branchName: 'armada/planning/psn_stopping',
            title: 'Stopping session',
            status: 'Stopping',
            pipelineId: null,
            processId: null,
            failureReason: null,
            selectedPlaybooks: [],
            createdUtc: '2026-05-17T00:00:00Z',
            startedUtc: '2026-05-17T00:00:00Z',
            completedUtc: null,
            lastUpdateUtc: '2026-05-17T00:05:00Z',
          },
        ]}
        activeSessionId={undefined}
        endingSessionId={null}
        formatRelativeTime={() => 'just now'}
        resolveCaptainName={() => 'Planner'}
        resolveVesselName={() => 'Repository'}
        resolvePipelineName={() => '-'}
        onSelect={() => undefined}
        onEndSession={() => undefined}
      />,
    );

    expect(screen.getByRole('button', { name: 'Ending...' })).toBeDisabled();
  });
});
