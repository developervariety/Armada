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
}
