package inventory;

/**
 * Thrown when a SKU is not in stock.
 */
public final class UnknownSkuException extends RuntimeException {
    private static final long serialVersionUID = 1L;

    private final String sku;

    /**
     * Create the exception.
     * @param sku The unknown SKU.
     */
    public UnknownSkuException(String sku) {
        super("Unknown SKU " + sku + ".");
        this.sku = sku;
    }

    /**
     * The unknown SKU.
     * @return The SKU.
     */
    public String getSku() {
        return sku;
    }
}
