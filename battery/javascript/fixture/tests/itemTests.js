'use strict';

const assert = require('node:assert/strict');
const { Item } = require('../src/item');

/**
 * The constructor keeps its arguments.
 */
function itemConstructorSetsValues() {
  const item = new Item('A-1', 'Anchor', 12.5, 4);
  assert.equal(item.sku, 'A-1');
  assert.equal(item.name, 'Anchor');
  assert.equal(item.unitPrice, 12.5);
  assert.equal(item.quantity, 4);
}

/**
 * A negative price is refused.
 */
function itemConstructorNegativePriceThrows() {
  assert.throws(() => new Item('A-1', 'Anchor', -1, 1), RangeError);
}

module.exports = { itemConstructorSetsValues, itemConstructorNegativePriceThrows };
