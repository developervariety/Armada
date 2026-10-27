'use strict';

const itemTests = require('./itemTests');
const stockTests = require('./stockTests');
const priceCalculatorTests = require('./priceCalculatorTests');

/**
 * Every test that runs, in run order. A test function that is not listed here never runs.
 * @returns {Array<{name: string, body: Function}>} The registered tests.
 */
function all() {
  return [
    { name: 'itemConstructorSetsValues', body: itemTests.itemConstructorSetsValues },
    { name: 'itemConstructorNegativePriceThrows', body: itemTests.itemConstructorNegativePriceThrows },
    { name: 'stockAddSameSkuMergesQuantity', body: stockTests.stockAddSameSkuMergesQuantity },
    { name: 'stockRemoveReducesQuantity', body: stockTests.stockRemoveReducesQuantity },
    { name: 'stockRemoveUnknownSkuThrows', body: stockTests.stockRemoveUnknownSkuThrows },
    { name: 'priceCalculatorLineTotalMultiplies', body: priceCalculatorTests.priceCalculatorLineTotalMultiplies },
    { name: 'priceCalculatorApplyDiscountTenPercent', body: priceCalculatorTests.priceCalculatorApplyDiscountTenPercent },
    { name: 'priceCalculatorApplyDiscountZeroKeepsPrice', body: priceCalculatorTests.priceCalculatorApplyDiscountZeroKeepsPrice }
  ];
}

module.exports = { all };
