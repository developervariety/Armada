'use strict';

const assert = require('node:assert/strict');
const { lineTotal } = require('../src/priceCalculator');

/**
 * A line total is the unit price times the quantity.
 */
function priceCalculatorLineTotalMultiplies() {
  assert.equal(lineTotal(2.5, 3), 7.5);
}

module.exports = { priceCalculatorLineTotalMultiplies };
