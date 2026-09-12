import { describe, expect, it } from 'vitest';
import { parseObjectivePreparationJson } from './preparationUtils';

describe('parseObjectivePreparationJson', () => {
  it('accepts the complete preparation shape', () => {
    const result = parseObjectivePreparationJson(JSON.stringify({
      requiredForDispatch: true,
      requiredClaimKinds: ['SourcePath'],
      requiredSiblingInputs: [{
        vesselRef: 'ReferenceSourceA',
        relativePath: '../ReferenceSourceA',
        requiredArtifactPaths: ['output/extracted-artifacts'],
      }],
      source: null,
      target: null,
      claims: [],
    }));

    expect(result.requiredSiblingInputs[0].vesselRef).toBe('ReferenceSourceA');
  });

  it.each(['null', '[]', '{}'])(
    'rejects incomplete preparation JSON: %s',
    (raw) => expect(() => parseObjectivePreparationJson(raw)).toThrow(),
  );
});
