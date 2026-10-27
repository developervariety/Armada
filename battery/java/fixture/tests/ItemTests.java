package tests;

import inventory.Item;

/**
 * Tests of Item.
 */
public final class ItemTests {
    private ItemTests() {
    }

    /**
     * The constructor keeps its arguments.
     */
    public static void itemConstructorSetsValues() {
        Item item = new Item("A-1", "Anchor", 12.5, 4);
        Check.equal("A-1", item.getSku());
        Check.equal("Anchor", item.getName());
        Check.equal(12.5, item.getUnitPrice());
        Check.equal(4, item.getQuantity());
    }

    /**
     * A negative price is refused.
     */
    public static void itemConstructorNegativePriceThrows() {
        Check.throwsException(IllegalArgumentException.class, () -> new Item("A-1", "Anchor", -1, 1));
    }
}
