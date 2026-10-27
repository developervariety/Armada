<?php

declare(strict_types=1);

namespace Inventory;

use RuntimeException;

/**
 * Thrown when fewer units are on hand than requested.
 */
final class InsufficientStockException extends RuntimeException
{
    private string $sku;
    private int $onHand;

    /**
     * Create the exception.
     *
     * @param string $sku The SKU.
     * @param int $onHand Units on hand.
     */
    public function __construct(string $sku, int $onHand)
    {
        parent::__construct('Only ' . $onHand . ' units of ' . $sku . ' on hand.');
        $this->sku = $sku;
        $this->onHand = $onHand;
    }

    /**
     * The SKU.
     */
    public function getSku(): string
    {
        return $this->sku;
    }

    /**
     * Units on hand.
     */
    public function getOnHand(): int
    {
        return $this->onHand;
    }
}
