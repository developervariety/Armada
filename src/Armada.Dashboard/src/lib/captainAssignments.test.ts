import { effectiveAssignmentPersonas, WILDCARD_PERSONA } from './captainAssignments';

describe('effectiveAssignmentPersonas', () => {
  it('uses the pipeline steps when a pipeline is selected', () => {
    expect(effectiveAssignmentPersonas(['Worker', 'Judge'])).toEqual(['Worker', 'Judge']);
  });

  it('offers one wildcard assignment when the pipeline is inherited', () => {
    expect(WILDCARD_PERSONA).toBe('*');
    expect(effectiveAssignmentPersonas([])).toEqual(['*']);
  });
});
