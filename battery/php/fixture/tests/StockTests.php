<?php

declare(strict_types=1);

namespace Inventory\Tests;

use Inventory\Item;
use Inventory\Stock;
use Inventory\UnknownSkuException;

/**
 * Tests of Stock.
 */
final class StockTests
{
    /**
     * Adding the same SKU twice merges the quantities.
     */
    public static function stockAddSameSkuMergesQuantity(): void
    {
        $stock = new Stock();
        $stock->add(new Item('A-1', 'Anchor', 1.0, 2));
        $stock->add(new Item('A-1', 'Anchor', 1.0, 3));
        assert($stock->count() === 1);
        assert($stock->totalUnits() === 5);
    }

    /**
     * Removing units lowers the quantity.
     */
    public static function stockRemoveReducesQuantity(): void
    {
        $stock = new Stock();
        $stock->add(new Item('A-1', 'Anchor', 1.0, 5));
        $stock->remove('A-1', 2);
        assert($stock->find('A-1')->getQuantity() === 3);
    }

    /**
     * Removing an unknown SKU throws.
     */
    public static function stockRemoveUnknownSkuThrows(): void
    {
        $stock = new Stock();
        Expect::throws(UnknownSkuException::class, static fn () => $stock->remove('missing', 1));
    }
}
