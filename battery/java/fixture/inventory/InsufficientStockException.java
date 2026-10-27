package inventory;

/**
 * Thrown when fewer units are on hand than requested.
 */
public final class InsufficientStockException extends RuntimeException {
    private static final long serialVersionUID = 1L;

    private final String sku;
    private final int onHand;

    /**
     * Create the exception.
     * @param sku The SKU.
     * @param onHand Units on hand.
     */
    public InsufficientStockException(String sku, int onHand) {
        super("Only " + onHand + " units of " + sku + " on hand.");
        this.sku = sku;
        this.onHand = onHand;
    }

    /**
     * The SKU.
     * @return The SKU.
     */
    public String getSku() {
        return sku;
    }

    /**
     * Units on hand.
     * @return The units on hand.
     */
    public int getOnHand() {
        return onHand;
    }
}
