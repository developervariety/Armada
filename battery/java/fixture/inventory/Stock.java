package inventory;

import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Objects;

/**
 * Items on hand, keyed by SKU.
 */
public final class Stock {
    private final Map<String, Item> items = new LinkedHashMap<>();

    /**
     * Create an empty stock.
     */
    public Stock() {
    }

    /**
     * Number of distinct items.
     * @return The count.
     */
    public int getCount() {
        return items.size();
    }

    /**
     * Add an item, or add its quantity to the item with the same SKU.
     * @param item Item.
     * @throws NullPointerException When item is null.
     */
    public void add(Item item) {
        Objects.requireNonNull(item, "item");
        Item existing = items.get(item.getSku());
        if (existing != null) {
            existing.setQuantity(existing.getQuantity() + item.getQuantity());
            return;
        }

        items.put(item.getSku(), item);
    }

    /**
     * Remove units of an item.
     * @param sku SKU.
     * @param quantity Units to remove.
     * @throws NullPointerException When sku is null.
     * @throws IllegalArgumentException When quantity is not positive.
     * @throws UnknownSkuException When the SKU is unknown.
     * @throws InsufficientStockException When fewer units are on hand than requested.
     */
    public void remove(String sku, int quantity) {
        Objects.requireNonNull(sku, "sku");
        if (quantity <= 0) throw new IllegalArgumentException("quantity must be positive");
        Item existing = items.get(sku);
        if (existing == null) throw new UnknownSkuException(sku);
        if (existing.getQuantity() < quantity) throw new InsufficientStockException(sku, existing.getQuantity());
        existing.setQuantity(existing.getQuantity() - quantity);
    }

    /**
     * Find an item.
     * @param sku SKU.
     * @return The item, or null.
     * @throws NullPointerException When sku is null.
     */
    public Item find(String sku) {
        Objects.requireNonNull(sku, "sku");
        return items.get(sku);
    }

    /**
     * Units on hand across every item.
     * @return Total units.
     */
    public int totalUnits() {
        int total = 0;
        for (Item item : items.values()) total += item.getQuantity();
        return total;
    }
}
