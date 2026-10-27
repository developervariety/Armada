<?php

declare(strict_types=1);

namespace Inventory\Tests;

use InvalidArgumentException;
use Inventory\Item;

/**
 * Tests of Item.
 */
final class ItemTests
{
    /**
     * The constructor keeps its arguments.
     */
    public static function itemConstructorSetsValues(): void
    {
        $item = new Item('A-1', 'Anchor', 12.5, 4);
        assert($item->getSku() === 'A-1');
        assert($item->getName() === 'Anchor');
        assert($item->getUnitPrice() === 12.5);
        assert($item->getQuantity() === 4);
    }

    /**
     * A negative price is refused.
     */
    public static function itemConstructorNegativePriceThrows(): void
    {
        Expect::throws(InvalidArgumentException::class, static fn () => new Item('A-1', 'Anchor', -1.0, 1));
    }
}
