'use strict';

const { Item } = require('./item');
const { UnknownSkuError, InsufficientStockError } = require('./errors');

/**
 * Items on hand, keyed by SKU.
 */
class Stock {
  /**
   * Create an empty stock.
   */
  constructor() {
    this._items = new Map();
  }

  /**
   * Number of distinct items.
   * @returns {number} The count.
   */
  get count() {
    return this._items.size;
  }

  /**
   * Add an item, or add its quantity to the item with the same SKU.
   * @param {Item} item Item.
   * @throws {TypeError} When item is not an Item.
   */
  add(item) {
    if (!(item instanceof Item)) throw new TypeError('item must be an Item');
    const existing = this._items.get(item.sku);
    if (existing !== undefined) {
      existing.quantity += item.quantity;
      return;
    }

    this._items.set(item.sku, item);
  }

  /**
   * Remove units of an item.
   * @param {string} sku SKU.
   * @param {number} quantity Units to remove.
   * @throws {TypeError} When sku is not a string.
   * @throws {RangeError} When quantity is not a positive integer.
   * @throws {UnknownSkuError} When the SKU is unknown.
   * @throws {InsufficientStockError} When fewer units are on hand than requested.
   */
  remove(sku, quantity) {
    if (typeof sku !== 'string') throw new TypeError('sku must be a string');
    if (!Number.isInteger(quantity) || quantity <= 0) throw new RangeError('quantity must be a positive integer');
    const existing = this._items.get(sku);
    if (existing === undefined) throw new UnknownSkuError(sku);
    if (existing.quantity < quantity) throw new InsufficientStockError(sku, existing.quantity);
    existing.quantity -= quantity;
  }

  /**
   * Find an item.
   * @param {string} sku SKU.
   * @returns {Item|null} The item, or null.
   * @throws {TypeError} When sku is not a string.
   */
  find(sku) {
    if (typeof sku !== 'string') throw new TypeError('sku must be a string');
    const item = this._items.get(sku);
    return item === undefined ? null : item;
  }

  /**
   * Units on hand across every item.
   * @returns {number} Total units.
   */
  totalUnits() {
    let total = 0;
    for (const item of this._items.values()) total += item.quantity;
    return total;
  }
}

module.exports = { Stock };
