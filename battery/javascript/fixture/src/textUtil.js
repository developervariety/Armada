'use strict';

/**
 * Shorten a value to at most maxLength characters, ending in "..." when it was cut.
 * @param {string} value Value.
 * @param {number} maxLength Maximum length, at least 3.
 * @returns {string} The value, or its shortened form.
 * @throws {TypeError} When value is null or undefined.
 * @throws {RangeError} When maxLength is less than 3.
 */
function truncate(value, maxLength) {
  if (value === null || value === undefined) throw new TypeError('value is required');
  if (maxLength < 3) throw new RangeError('maxLength must be at least 3');
  if (value.length <= maxLength) return value;
  return value.slice(0, maxLength - 3) + '...';
}

module.exports = { truncate };
