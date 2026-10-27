<?php

declare(strict_types=1);

namespace Inventory;

use InvalidArgumentException;

/**
 * A stock-keeping item.
 */
final class Item
{
    private string $sku;
    private string $name;
    private float $unitPrice;
    private int $quantity;

    /**
     * Create an item.
     *
     * @param string $sku Stock-keeping unit code.
     * @param string $name Display name.
     * @param float $unitPrice Price of one unit.
     * @param int $quantity Units on hand.
     * @throws InvalidArgumentException When unitPrice or quantity is negative.
     */
    public function __construct(string $sku, string $name, float $unitPrice, int $quantity)
    {
        if ($unitPrice < 0) {
            throw new InvalidArgumentException('unitPrice must not be negative');
        }
        if ($quantity < 0) {
            throw new InvalidArgumentException('quantity must not be negative');
        }

        $this->sku = $sku;
        $this->name = $name;
        $this->unitPrice = $unitPrice;
        $this->quantity = $quantity;
    }

    /**
     * Stock-keeping unit code.
     */
    public function getSku(): string
    {
        return $this->sku;
    }

    /**
     * Display name.
     */
    public function getName(): string
    {
        return $this->name;
    }

    /**
     * Price of one unit.
     */
    public function getUnitPrice(): float
    {
        return $this->unitPrice;
    }

    /**
     * Units on hand.
     */
    public function getQuantity(): int
    {
        return $this->quantity;
    }

    /**
     * Set the units on hand.
     *
     * @param int $quantity Units on hand.
     * @throws InvalidArgumentException When quantity is negative.
     */
    public function setQuantity(int $quantity): void
    {
        if ($quantity < 0) {
            throw new InvalidArgumentException('quantity must not be negative');
        }

        $this->quantity = $quantity;
    }
}
