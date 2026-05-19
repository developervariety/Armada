import { canCaptainStartPlanning, isCaptainPlanningReadyState } from './captains';

describe('captain planning eligibility', () => {
  it('treats idle and available captains as planning-ready states', () => {
    expect(isCaptainPlanningReadyState('Idle')).toBe(true);
    expect(isCaptainPlanningReadyState('Available')).toBe(true);
    expect(isCaptainPlanningReadyState(' available ')).toBe(true);
  });

  it('rejects non-ready captain states', () => {
    expect(isCaptainPlanningReadyState('Working')).toBe(false);
    expect(isCaptainPlanningReadyState('Planning')).toBe(false);
    expect(isCaptainPlanningReadyState('')).toBe(false);
  });

  it('requires both runtime support and a ready state', () => {
    expect(canCaptainStartPlanning({ supportsPlanningSessions: true, state: 'Idle' })).toBe(true);
    expect(canCaptainStartPlanning({ supportsPlanningSessions: true, state: 'Available' })).toBe(true);
    expect(canCaptainStartPlanning({ supportsPlanningSessions: true, state: 'Working' })).toBe(false);
    expect(canCaptainStartPlanning({ supportsPlanningSessions: false, state: 'Idle' })).toBe(false);
    expect(canCaptainStartPlanning(null)).toBe(false);
  });
});
