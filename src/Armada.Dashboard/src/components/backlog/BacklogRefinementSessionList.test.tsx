import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import BacklogRefinementSessionList from './BacklogRefinementSessionList';
import type { ObjectiveRefinementSession } from '../../types/models';

vi.mock('../shared/StatusBadge', () => ({
  default: ({ status }: { status: string }) => <span>{status}</span>,
}));

function createSession(overrides: Partial<ObjectiveRefinementSession> = {}): ObjectiveRefinementSession {
  return {
    id: 'ref_123',
    objectiveId: 'obj_123',
    tenantId: 'ten_123',
    userId: 'usr_123',
    captainId: 'cpt_123',
    fleetId: null,
    vesselId: null,
    title: 'Session Title',
    status: 'Active',
    processId: null,
    failureReason: null,
    createdUtc: '2026-05-01T00:00:00Z',
    startedUtc: '2026-05-01T00:01:00Z',
    completedUtc: null,
    lastUpdateUtc: '2026-05-01T00:02:00Z',
    ...overrides,
  };
}

describe('BacklogRefinementSessionList', () => {
  it('renders an empty state before any backlog refinement sessions exist', () => {
    render(
      <BacklogRefinementSessionList
        sessions={[]}
        activeSessionId=""
        captainNameById={new Map()}
        formatRelativeTime={() => 'just now'}
        onSelect={() => undefined}
      />,
    );

    expect(screen.getByText('No refinement sessions yet.')).toBeInTheDocument();
    expect(screen.getByText(/Choose a captain and start a lightweight refinement pass/)).toBeInTheDocument();
  });

  it('renders session metadata and selects the clicked session', () => {
    const onSelect = vi.fn();

    render(
      <BacklogRefinementSessionList
        sessions={[
          createSession({ id: 'ref_active', title: 'Active refinement', captainId: 'cpt_123', status: 'Active' }),
          createSession({ id: 'ref_done', title: 'Completed refinement', captainId: 'cpt_456', status: 'Completed' }),
        ]}
        activeSessionId="ref_active"
        captainNameById={new Map([
          ['cpt_123', 'Captain Alpha'],
          ['cpt_456', 'Captain Beta'],
        ])}
        formatRelativeTime={() => '2 minutes ago'}
        onSelect={onSelect}
      />,
    );

    expect(screen.getByRole('button', { name: /Active refinement/i })).toHaveClass('active');
    expect(screen.getByText('Captain Alpha')).toBeInTheDocument();
    expect(screen.getAllByText('2 minutes ago')).toHaveLength(2);

    fireEvent.click(screen.getByRole('button', { name: /Completed refinement/i }));
    expect(onSelect).toHaveBeenCalledWith('ref_done');
  });
});
