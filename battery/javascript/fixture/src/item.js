'use strict';

/**
 * A stock-keeping item.
 */
class Item {
  /**
   * Create an item.
   * @param {string} sku Stock-keeping unit code.
   * @param {string} name Display name.
   * @param {number} unitPrice Price of one unit.
   * @param {number} quantity Units on hand.
   * @throws {TypeError} When sku or name is not a string.
   * @throws {RangeError} When unitPrice or quantity is negative.
   */
  constructor(sku, name, unitPrice, quantity) {
    if (typeof sku !== 'string') throw new TypeError('sku must be a string');
    if (typeof name !== 'string') throw new TypeError('name must be a string');
    if (unitPrice < 0) throw new RangeError('unitPrice must not be negative');
    if (quantity < 0) throw new RangeError('quantity must not be negative');

    /** Stock-keeping unit code. */
    this.sku = sku;

    /** Display name. */
    this.name = name;

    /** Price of one unit. */
    this.unitPrice = unitPrice;

    /** Units on hand. */
    this.quantity = quantity;
  }
}

module.exports = { Item };
