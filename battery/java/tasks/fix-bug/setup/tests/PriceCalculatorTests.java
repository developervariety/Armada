package tests;

import inventory.PriceCalculator;

/**
 * Tests of PriceCalculator.
 */
public final class PriceCalculatorTests {
    private PriceCalculatorTests() {
    }

    /**
     * A line total is the unit price times the quantity.
     */
    public static void priceCalculatorLineTotalMultiplies() {
        Check.equal(7.5, PriceCalculator.lineTotal(2.5, 3));
    }

    /**
     * Ten percent off 80.00 is 72.00.
     */
    public static void priceCalculatorApplyDiscountTenPercent() {
        Check.equal(72.0, PriceCalculator.applyDiscount(80, 10));
    }

    /**
     * No discount keeps the price.
     */
    public static void priceCalculatorApplyDiscountZeroKeepsPrice() {
        Check.equal(19.99, PriceCalculator.applyDiscount(19.99, 0));
    }
}
