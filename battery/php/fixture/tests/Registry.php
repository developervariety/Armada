<?php

declare(strict_types=1);

namespace Inventory\Tests;

/**
 * Every test that runs, in run order. A test method that is not listed here never runs.
 */
final class Registry
{
    /**
     * The registered tests: name => callable.
     *
     * @return array<string, callable> The tests, in run order.
     */
    public static function all(): array
    {
        return [
            'itemConstructorSetsValues' => [ItemTests::class, 'itemConstructorSetsValues'],
            'itemConstructorNegativePriceThrows' => [ItemTests::class, 'itemConstructorNegativePriceThrows'],
            'stockAddSameSkuMergesQuantity' => [StockTests::class, 'stockAddSameSkuMergesQuantity'],
            'stockRemoveReducesQuantity' => [StockTests::class, 'stockRemoveReducesQuantity'],
            'stockRemoveUnknownSkuThrows' => [StockTests::class, 'stockRemoveUnknownSkuThrows'],
            'priceCalculatorLineTotalMultiplies' => [PriceCalculatorTests::class, 'priceCalculatorLineTotalMultiplies'],
        ];
    }
}
