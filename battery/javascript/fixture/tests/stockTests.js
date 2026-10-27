'use strict';

const assert = require('node:assert/strict');
const { Item } = require('../src/item');
const { Stock } = require('../src/stock');
const { UnknownSkuError } = require('../src/errors');

/**
 * Adding the same SKU twice merges the quantities.
 */
function stockAddSameSkuMergesQuantity() {
  const stock = new Stock();
  stock.add(new Item('A-1', 'Anchor', 1, 2));
  stock.add(new Item('A-1', 'Anchor', 1, 3));
  assert.equal(stock.count, 1);
  assert.equal(stock.totalUnits(), 5);
}

/**
 * Removing units lowers the quantity.
 */
function stockRemoveReducesQuantity() {
  const stock = new Stock();
  stock.add(new Item('A-1', 'Anchor', 1, 5));
  stock.remove('A-1', 2);
  assert.equal(stock.find('A-1').quantity, 3);
}

/**
 * Removing an unknown SKU throws.
 */
function stockRemoveUnknownSkuThrows() {
  const stock = new Stock();
  assert.throws(() => stock.remove('missing', 1), UnknownSkuError);
}

module.exports = { stockAddSameSkuMergesQuantity, stockRemoveReducesQuantity, stockRemoveUnknownSkuThrows };
