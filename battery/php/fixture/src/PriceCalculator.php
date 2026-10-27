<?php

declare(strict_types=1);

namespace Inventory;

use InvalidArgumentException;

/**
 * Price arithmetic.
 */
final class PriceCalculator
{
    /**
     * Apply a percentage discount to a price, rounded to cents.
     *
     * @param float $price Price.
     * @param float $percent Discount in percent, 0 to 100.
     * @return float The discounted price.
     * @throws InvalidArgumentException When price is negative or percent is outside 0 to 100.
     */
    public static function applyDiscount(float $price, float $percent): float
    {
        if ($price < 0) {
            throw new InvalidArgumentException('price must not be negative');
        }
        if ($percent < 0 || $percent > 100) {
            throw new InvalidArgumentException('percent must be 0 to 100');
        }

        return round(($price - $percent) * 100) / 100;
    }

    /**
     * Total of a line: unit price times quantity.
     *
     * @param float $unitPrice Unit price.
     * @param int $quantity Quantity.
     * @return float The line total.
     * @throws InvalidArgumentException When either value is negative.
     */
    public static function lineTotal(float $unitPrice, int $quantity): float
    {
        if ($unitPrice < 0) {
            throw new InvalidArgumentException('unitPrice must not be negative');
        }
        if ($quantity < 0) {
            throw new InvalidArgumentException('quantity must not be negative');
        }

        return $unitPrice * $quantity;
    }
}
