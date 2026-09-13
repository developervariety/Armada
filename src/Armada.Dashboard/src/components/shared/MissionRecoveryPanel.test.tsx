import { render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import MissionRecoveryPanel from './MissionRecoveryPanel';
import type { MissionRecoveryReport } from '../../types/models';

vi.mock('../../context/LocaleContext', () => {
  const locale = {
    t: (text: string, params?: Record<string, string | number | null | undefined>) =>
      params
        ? Object.entries(params).reduce((current, [key, value]) => current.split(`{{${key}}}`).join(String(value ?? '')), text)
        : text,
    formatDateTime: (value: string | null | undefined) => value ?? '',
  };
  return { useLocale: () => locale };
});

function report(overrides: Partial<MissionRecoveryReport>): MissionRecoveryReport {
  return {
    missionId: 'msn_failed',
    status: 'Failed',
    failureReason: 'Build check failed',
    parentMissionId: null,
    isRescue: false,
    recoveryAttempts: 2,
    maxRecoveryAttempts: 3,
    recoveryBudgetExhausted: false,
    autonomousRecoveryEnabled: true,
    dispatchRescueMissions: true,
    landingRetryCount: 1,
    maxLandingRetries: 2,
    lastRecoveryActionUtc: '2026-09-13T11:00:00Z',
    rescues: [
      {
        missionId: 'msn_rescue_one',
        title: 'Rescue 1',
        status: 'Complete',
        voyageId: 'vyg_rescue',
        captainId: 'cpt_worker',
        commitHash: 'abcdef1234567890abcdef1234567890abcdef12',
        failureReason: null,
        createdUtc: '2026-09-13T10:30:00Z',
        completedUtc: '2026-09-13T10:50:00Z',
      },
    ],
    rescuesTruncated: false,
    rescuesUnavailableReason: null,
    incidents: [],
    incidentsTruncated: false,
    incidentsUnavailableReason: null,
    events: [],
    eventsWindowFull: false,
    eventsUnavailableReason: null,
    ...overrides,
  };
}

function renderPanel(props: { missionId: string | null; report: MissionRecoveryReport | null; error?: string | null; loading?: boolean }) {
  return render(
    <MemoryRouter>
      <MissionRecoveryPanel
        missionId={props.missionId}
        report={props.report}
        error={props.error ?? null}
        loading={props.loading ?? false}
      />
    </MemoryRouter>,
  );
}

describe('MissionRecoveryPanel', () => {
  it('says the incident names no mission when there is no mission id', () => {
    renderPanel({ missionId: null, report: null });

    expect(screen.getByRole('heading', { name: 'Mission Recovery' })).toBeInTheDocument();
    expect(screen.getByText('This incident names no mission, so no recovery report is available.')).toBeInTheDocument();
  });

  it('shows attempts against the budget, landing retries and the failure reason', () => {
    renderPanel({ missionId: 'msn_failed', report: report({}) });

    expect(screen.getByText('2 of 3')).toBeInTheDocument();
    expect(screen.getByText('1 of 2')).toBeInTheDocument();
    expect(screen.getByText('Build check failed')).toBeInTheDocument();
    expect(screen.queryByText('Recovery budget exhausted')).not.toBeInTheDocument();
  });

  it('links each rescue mission with its status and commit', () => {
    renderPanel({ missionId: 'msn_failed', report: report({}) });

    const link = screen.getByRole('link', { name: 'Rescue 1' });
    expect(link).toHaveAttribute('href', '/missions/msn_rescue_one');
    const row = link.closest('[data-rescue]') as HTMLElement;
    expect(within(row).getByText('Complete')).toBeInTheDocument();
    expect(within(row).getByText('abcdef123456')).toBeInTheDocument();
  });

  it('states exhausted budgets, truncated sections and unavailable reasons instead of hiding them', () => {
    renderPanel({
      missionId: 'msn_failed',
      report: report({
        recoveryBudgetExhausted: true,
        rescues: [],
        rescuesTruncated: true,
        rescuesUnavailableReason: 'the mission has no vessel',
      }),
    });

    expect(screen.getByText('Recovery budget exhausted')).toBeInTheDocument();
    expect(screen.getByText('the mission has no vessel')).toBeInTheDocument();
    expect(screen.getByText('More rescues exist than are listed.')).toBeInTheDocument();
  });

  it('shows a load error rather than an empty report', () => {
    renderPanel({ missionId: 'msn_failed', report: null, error: 'Recovery report unavailable: 404' });

    expect(screen.getByText('Recovery report unavailable: 404')).toBeInTheDocument();
  });
});
