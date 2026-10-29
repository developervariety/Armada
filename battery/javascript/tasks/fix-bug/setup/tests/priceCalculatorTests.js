'use strict';

const assert = require('node:assert/strict');
const { applyDiscount, lineTotal } = require('../src/priceCalculator');

/**
 * A line total is the unit price times the quantity.
 */
function priceCalculatorLineTotalMultiplies() {
  assert.equal(lineTotal(2.5, 3), 7.5);
}

/**
 * Ten percent off 80.00 is 72.00.
 */
function priceCalculatorApplyDiscountTenPercent() {
  assert.equal(applyDiscount(80, 10), 72);
}

/**
 * No discount keeps the price.
 */
function priceCalculatorApplyDiscountZeroKeepsPrice() {
  assert.equal(applyDiscount(19.99, 0), 19.99);
}

module.exports = {
  priceCalculatorLineTotalMultiplies,
  priceCalculatorApplyDiscountTenPercent,
  priceCalculatorApplyDiscountZeroKeepsPrice
};
