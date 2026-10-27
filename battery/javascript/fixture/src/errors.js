'use strict';

/**
 * Thrown when a SKU is not in stock.
 */
class UnknownSkuError extends Error {
  /**
   * Create the error.
   * @param {string} sku The unknown SKU.
   */
  constructor(sku) {
    super('Unknown SKU ' + sku + '.');
    this.name = 'UnknownSkuError';
    this.sku = sku;
  }
}

/**
 * Thrown when fewer units are on hand than requested.
 */
class InsufficientStockError extends Error {
  /**
   * Create the error.
   * @param {string} sku The SKU.
   * @param {number} onHand Units on hand.
   */
  constructor(sku, onHand) {
    super('Only ' + onHand + ' units of ' + sku + ' on hand.');
    this.name = 'InsufficientStockError';
    this.sku = sku;
    this.onHand = onHand;
  }
}

module.exports = { UnknownSkuError, InsufficientStockError };
