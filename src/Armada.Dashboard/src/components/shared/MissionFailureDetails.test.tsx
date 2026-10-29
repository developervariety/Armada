import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import MissionFailureDetails from './MissionFailureDetails';
import type { Mission } from '../../types/models';

vi.mock('../../context/LocaleContext', () => {
  const locale = { t: (text: string) => text };
  return { useLocale: () => locale };
});

function mission(overrides: Partial<Mission>): Mission {
  return {
    id: 'msn_setup',
    title: 'Repository onboarding survey',
    status: 'Failed',
    failureReason: 'The captain exited before it wrote a report.',
    vesselId: 'vsl_1',
    voyageId: null,
    captainId: null,
    ...overrides,
  } as Mission;
}

describe('MissionFailureDetails', () => {
  it('shows the failure reason and a link to the mission', () => {
    render(
      <MemoryRouter>
        <MissionFailureDetails mission={mission({})} />
      </MemoryRouter>,
    );

    expect(screen.getByRole('alert')).toHaveTextContent('The captain exited before it wrote a report.');
    expect(screen.getByRole('link', { name: 'Open Mission' })).toHaveAttribute('href', '/missions/msn_setup');
  });

  it('says when no reason was recorded', () => {
    render(
      <MemoryRouter>
        <MissionFailureDetails mission={mission({ failureReason: '  ' })} />
      </MemoryRouter>,
    );

    expect(screen.getByRole('alert')).toHaveTextContent('No reason was recorded.');
  });
});
