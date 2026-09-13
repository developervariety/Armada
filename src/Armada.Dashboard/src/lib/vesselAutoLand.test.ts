import { autoLandFormFromPredicate, autoLandPredicatePayload, EMPTY_AUTO_LAND_FORM } from './vesselAutoLand';

describe('vessel auto-land predicate mapping', () => {
  it('loads a stored predicate string into the form, whatever the key casing', () => {
    const stored = '{"Enabled":true,"MaxFiles":5,"MaxAddedLines":100,"AllowPaths":["src/**","docs/**"],"DenyPaths":["infra/**"]}';

    expect(autoLandFormFromPredicate(stored)).toEqual({
      enabled: true,
      maxFiles: '5',
      maxAddedLines: '100',
      allowPaths: 'src/**\ndocs/**',
      denyPaths: 'infra/**',
      unparsable: false,
    });
    expect(autoLandFormFromPredicate('{"enabled":false,"maxFiles":2}').maxFiles).toBe('2');
  });

  it('sends the same predicate back for an unchanged form, so saving does not clear it', () => {
    const stored = '{"Enabled":true,"MaxFiles":5,"MaxAddedLines":100,"AllowPaths":["src/**"],"DenyPaths":[]}';

    expect(autoLandPredicatePayload(autoLandFormFromPredicate(stored))).toEqual({
      enabled: true,
      maxFiles: 5,
      maxAddedLines: 100,
      allowPaths: ['src/**'],
      denyPaths: [],
    });
  });

  it('keeps a disabled predicate that still has rules instead of dropping them', () => {
    expect(autoLandPredicatePayload({ ...EMPTY_AUTO_LAND_FORM, enabled: false, maxFiles: '3' })).toEqual({
      enabled: false,
      maxFiles: 3,
      maxAddedLines: null,
      allowPaths: [],
      denyPaths: [],
    });
  });

  it('sends null only when auto-land is off and no rule is set', () => {
    expect(autoLandFormFromPredicate(null)).toEqual(EMPTY_AUTO_LAND_FORM);
    expect(autoLandPredicatePayload(EMPTY_AUTO_LAND_FORM)).toBeNull();
  });

  it('flags a stored predicate that cannot be parsed instead of showing it as disabled', () => {
    const form = autoLandFormFromPredicate('{not json');

    expect(form.unparsable).toBe(true);
    expect(form.enabled).toBe(false);
  });
});
