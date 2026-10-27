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
}
