package inventory;

/**
 * Price arithmetic.
 */
public final class PriceCalculator {
    private PriceCalculator() {
    }

    /**
     * Apply a percentage discount to a price, rounded to cents.
     * @param price Price.
     * @param percent Discount in percent, 0 to 100.
     * @return The discounted price.
     * @throws IllegalArgumentException When price is negative or percent is outside 0 to 100.
     */
    public static double applyDiscount(double price, double percent) {
        if (price < 0) throw new IllegalArgumentException("price must not be negative");
        if (percent < 0 || percent > 100) throw new IllegalArgumentException("percent must be 0 to 100");
        return Math.round((price - percent) * 100) / 100.0;
    }

    /**
     * Total of a line: unit price times quantity.
     * @param unitPrice Unit price.
     * @param quantity Quantity.
     * @return The line total.
     * @throws IllegalArgumentException When either value is negative.
     */
    public static double lineTotal(double unitPrice, int quantity) {
        if (unitPrice < 0) throw new IllegalArgumentException("unitPrice must not be negative");
        if (quantity < 0) throw new IllegalArgumentException("quantity must not be negative");
        return unitPrice * quantity;
    }
}
