package inventory;

import java.util.Objects;

/**
 * A stock-keeping item.
 */
public final class Item {
    private final String sku;
    private final String name;
    private final double unitPrice;
    private int quantity;

    /**
     * Create an item.
     * @param sku Stock-keeping unit code.
     * @param name Display name.
     * @param unitPrice Price of one unit.
     * @param quantity Units on hand.
     * @throws NullPointerException When sku or name is null.
     * @throws IllegalArgumentException When unitPrice or quantity is negative.
     */
    public Item(String sku, String name, double unitPrice, int quantity) {
        Objects.requireNonNull(sku, "sku");
        Objects.requireNonNull(name, "name");
        if (unitPrice < 0) throw new IllegalArgumentException("unitPrice must not be negative");
        if (quantity < 0) throw new IllegalArgumentException("quantity must not be negative");
        this.sku = sku;
        this.name = name;
        this.unitPrice = unitPrice;
        this.quantity = quantity;
    }

    /**
     * Stock-keeping unit code.
     * @return The SKU.
     */
    public String getSku() {
        return sku;
    }

    /**
     * Display name.
     * @return The name.
     */
    public String getName() {
        return name;
    }

    /**
     * Price of one unit.
     * @return The unit price.
     */
    public double getUnitPrice() {
        return unitPrice;
    }

    /**
     * Units on hand.
     * @return The quantity.
     */
    public int getQuantity() {
        return quantity;
    }

    /**
     * Set the units on hand.
     * @param quantity Units on hand.
     * @throws IllegalArgumentException When quantity is negative.
     */
    public void setQuantity(int quantity) {
        if (quantity < 0) throw new IllegalArgumentException("quantity must not be negative");
        this.quantity = quantity;
    }
}
