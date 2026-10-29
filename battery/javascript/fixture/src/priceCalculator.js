'use strict';

/**
 * Apply a percentage discount to a price, rounded to cents.
 * @param {number} price Price.
 * @param {number} percent Discount in percent, 0 to 100.
 * @returns {number} The discounted price.
 * @throws {RangeError} When price is negative or percent is outside 0 to 100.
 */
function applyDiscount(price, percent) {
  if (price < 0) throw new RangeError('price must not be negative');
  if (percent < 0 || percent > 100) throw new RangeError('percent must be 0 to 100');
  return Math.round((price - percent) * 100) / 100;
}

/**
 * Total of a line: unit price times quantity.
 * @param {number} unitPrice Unit price.
 * @param {number} quantity Quantity.
 * @returns {number} The line total.
 * @throws {RangeError} When either value is negative.
 */
function lineTotal(unitPrice, quantity) {
  if (unitPrice < 0) throw new RangeError('unitPrice must not be negative');
  if (quantity < 0) throw new RangeError('quantity must not be negative');
  return unitPrice * quantity;
}

module.exports = { applyDiscount, lineTotal };
