<?php

declare(strict_types=1);

namespace Inventory\Tests;

use Inventory\PriceCalculator;

/**
 * Tests of PriceCalculator.
 */
final class PriceCalculatorTests
{
    /**
     * A line total is the unit price times the quantity.
     */
    public static function priceCalculatorLineTotalMultiplies(): void
    {
        assert(PriceCalculator::lineTotal(2.5, 3) === 7.5);
    }

    /**
     * Ten percent off 80.00 is 72.00.
     */
    public static function priceCalculatorApplyDiscountTenPercent(): void
    {
        assert(PriceCalculator::applyDiscount(80.0, 10.0) === 72.0);
    }

    /**
     * No discount keeps the price.
     */
    public static function priceCalculatorApplyDiscountZeroKeepsPrice(): void
    {
        assert(PriceCalculator::applyDiscount(19.99, 0.0) === 19.99);
    }
}
