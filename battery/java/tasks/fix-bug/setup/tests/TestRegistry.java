package tests;

import java.util.Arrays;
import java.util.List;

/**
 * Every test that runs, in run order. A test method that is not listed here never runs (and its class is not even
 * compiled unless something else uses it).
 */
public final class TestRegistry {
    private TestRegistry() {
    }

    /**
     * The registered tests.
     * @return The tests, in run order.
     */
    public static List<TestCase> all() {
        return Arrays.asList(
            new TestCase("itemConstructorSetsValues", ItemTests::itemConstructorSetsValues),
            new TestCase("itemConstructorNegativePriceThrows", ItemTests::itemConstructorNegativePriceThrows),
            new TestCase("stockAddSameSkuMergesQuantity", StockTests::stockAddSameSkuMergesQuantity),
            new TestCase("stockRemoveReducesQuantity", StockTests::stockRemoveReducesQuantity),
            new TestCase("stockRemoveUnknownSkuThrows", StockTests::stockRemoveUnknownSkuThrows),
            new TestCase("priceCalculatorLineTotalMultiplies", PriceCalculatorTests::priceCalculatorLineTotalMultiplies),
            new TestCase("priceCalculatorApplyDiscountTenPercent", PriceCalculatorTests::priceCalculatorApplyDiscountTenPercent),
            new TestCase("priceCalculatorApplyDiscountZeroKeepsPrice", PriceCalculatorTests::priceCalculatorApplyDiscountZeroKeepsPrice)
        );
    }
}
