<?php

declare(strict_types=1);

namespace Inventory;

use InvalidArgumentException;

/**
 * Items on hand, keyed by SKU.
 */
final class Stock
{
    /** @var array<string, Item> */
    private array $items = [];

    /**
     * Number of distinct items.
     */
    public function count(): int
    {
        return count($this->items);
    }

    /**
     * Add an item, or add its quantity to the item with the same SKU.
     *
     * @param Item $item Item.
     */
    public function add(Item $item): void
    {
        $existing = $this->items[$item->getSku()] ?? null;
        if ($existing !== null) {
            $existing->setQuantity($existing->getQuantity() + $item->getQuantity());
            return;
        }

        $this->items[$item->getSku()] = $item;
    }

    /**
     * Remove units of an item.
     *
     * @param string $sku SKU.
     * @param int $quantity Units to remove.
     * @throws InvalidArgumentException When quantity is not positive.
     * @throws UnknownSkuException When the SKU is unknown.
     * @throws InsufficientStockException When fewer units are on hand than requested.
     */
    public function remove(string $sku, int $quantity): void
    {
        if ($quantity <= 0) {
            throw new InvalidArgumentException('quantity must be positive');
        }

        $existing = $this->items[$sku] ?? null;
        if ($existing === null) {
            throw new UnknownSkuException($sku);
        }
        if ($existing->getQuantity() < $quantity) {
            throw new InsufficientStockException($sku, $existing->getQuantity());
        }

        $existing->setQuantity($existing->getQuantity() - $quantity);
    }

    /**
     * Find an item.
     *
     * @param string $sku SKU.
     * @return Item|null The item, or null.
     */
    public function find(string $sku): ?Item
    {
        return $this->items[$sku] ?? null;
    }

    /**
     * Units on hand across every item.
     */
    public function totalUnits(): int
    {
        $total = 0;
        foreach ($this->items as $item) {
            $total += $item->getQuantity();
        }

        return $total;
    }
}
