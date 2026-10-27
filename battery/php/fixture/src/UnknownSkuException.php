<?php

declare(strict_types=1);

namespace Inventory;

use RuntimeException;

/**
 * Thrown when a SKU is not in stock.
 */
final class UnknownSkuException extends RuntimeException
{
    private string $sku;

    /**
     * Create the exception.
     *
     * @param string $sku The unknown SKU.
     */
    public function __construct(string $sku)
    {
        parent::__construct('Unknown SKU ' . $sku . '.');
        $this->sku = $sku;
    }

    /**
     * The unknown SKU.
     */
    public function getSku(): string
    {
        return $this->sku;
    }
}
