package tests;

import inventory.Item;
import inventory.Stock;
import inventory.UnknownSkuException;

/**
 * Tests of Stock.
 */
public final class StockTests {
    private StockTests() {
    }

    /**
     * Adding the same SKU twice merges the quantities.
     */
    public static void stockAddSameSkuMergesQuantity() {
        Stock stock = new Stock();
        stock.add(new Item("A-1", "Anchor", 1, 2));
        stock.add(new Item("A-1", "Anchor", 1, 3));
        Check.equal(1, stock.getCount());
        Check.equal(5, stock.totalUnits());
    }

    /**
     * Removing units lowers the quantity.
     */
    public static void stockRemoveReducesQuantity() {
        Stock stock = new Stock();
        stock.add(new Item("A-1", "Anchor", 1, 5));
        stock.remove("A-1", 2);
        Check.equal(3, stock.find("A-1").getQuantity());
    }

    /**
     * Removing an unknown SKU throws.
     */
    public static void stockRemoveUnknownSkuThrows() {
        Stock stock = new Stock();
        Check.throwsException(UnknownSkuException.class, () -> stock.remove("missing", 1));
    }
}
