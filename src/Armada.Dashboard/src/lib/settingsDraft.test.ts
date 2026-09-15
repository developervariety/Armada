import { describe, expect, it } from 'vitest';
import { baseAfterSave, changedKeys, isDirty, mergeDraft } from './settingsDraft';

describe('settings drafts', () => {
  it('adopts server changes to fields the operator did not edit and keeps edited fields', () => {
    const base = { maxCaptains: 4, heartbeat: 30 };
    const draft = { maxCaptains: 9, heartbeat: 30 };
    const next = { maxCaptains: 4, heartbeat: 45 };
    expect(mergeDraft(base, draft, next)).toEqual({ maxCaptains: 9, heartbeat: 45 });
  });

  it('merges nested objects field by field and compares lists whole', () => {
    const base = { remote: { enabled: false, url: 'a' }, models: ['m1'] };
    const draft = { remote: { enabled: true, url: 'a' }, models: ['m1'] };
    const next = { remote: { enabled: false, url: 'b' }, models: ['m1', 'm2'] };
    expect(mergeDraft(base, draft, next)).toEqual({ remote: { enabled: true, url: 'b' }, models: ['m1', 'm2'] });
  });

  it('reports only the fields that changed', () => {
    const base = { mid: 'a', high: 'b', providers: '{}' };
    expect(changedKeys(base, { ...base, mid: 'a\nb' })).toEqual(['mid']);
    expect(isDirty(base, { ...base })).toBe(false);
  });

  it('lets saved fields adopt the stored value while unsaved fields stay drafts', () => {
    const base = { mid: 'a', high: 'b' };
    const draft = { mid: 'a2', high: 'b2' };
    const response = { mid: 'A2', high: 'b' };
    const merged = mergeDraft(baseAfterSave(base, { mid: draft.mid }), draft, response);
    expect(merged).toEqual({ mid: 'A2', high: 'b2' });
  });
});
