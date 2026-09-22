import { expect, test } from 'vitest';
import { retainSelection } from './selection';

test('keeps selected ids whose rows are still present and drops the ids that vanished', () => {
  expect(retainSelection(['a', 'b', 'c'], ['c', 'a', 'd'])).toEqual(['a', 'c']);
});

test('returns the same array when every selected row is still present', () => {
  const selected = ['a', 'b'];
  expect(retainSelection(selected, ['b', 'a', 'z'])).toBe(selected);
});

test('clears the selection when none of the selected rows remain', () => {
  expect(retainSelection(['a'], [])).toEqual([]);
});
