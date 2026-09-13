import { autoLandFormFromPredicate, autoLandPredicatePayload, describeAutoLand, EMPTY_AUTO_LAND_FORM } from './vesselAutoLand';

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

describe('vessel auto-land summary', () => {
  it('summarizes an enabled predicate with every rule', () => {
    const stored = '{"Enabled":true,"MaxFiles":5,"MaxAddedLines":100,"AllowPaths":["src/**","docs/**"],"DenyPaths":["infra/**"]}';

    expect(describeAutoLand(autoLandFormFromPredicate(stored))).toBe(
      'On: max 5 files, max 100 added lines; allow src/**, docs/**; deny infra/**',
    );
  });

  it('says an enabled predicate without rules has no limits', () => {
    expect(describeAutoLand(autoLandFormFromPredicate('{"enabled":true}'))).toBe('On: no limits');
  });

  it('shows a disabled predicate as off while naming the rules it keeps', () => {
    expect(describeAutoLand(autoLandFormFromPredicate('{"enabled":false,"maxFiles":3}'))).toBe('Off (rules kept: max 3 files)');
  });

  it('distinguishes a missing predicate from an unparsable one', () => {
    expect(describeAutoLand(autoLandFormFromPredicate(null))).toBe('Not configured');
    expect(describeAutoLand(autoLandFormFromPredicate('{not json'))).toBe('The stored predicate cannot be parsed');
  });
});
