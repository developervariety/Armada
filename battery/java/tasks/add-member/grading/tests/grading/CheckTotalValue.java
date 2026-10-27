package tests.grading;

import inventory.Item;

/**
 * Hidden check for the add-member task: Item.getTotalValue() is unitPrice times quantity and follows quantity changes.
 */
public final class CheckTotalValue {
    private CheckTotalValue() {
    }

    /**
     * Entry point.
     * @param args Ignored.
     */
    public static void main(String[] args) {
        Item item = new Item("A-1", "Anchor", 2.5, 4);
        double first = item.getTotalValue();
        item.setQuantity(6);
        double second = item.getTotalValue();
        Item empty = new Item("B-1", "Buoy", 9.99, 0);
        boolean ok = first == 10.0 && second == 15.0 && empty.getTotalValue() == 0.0;
        System.out.println(ok ? "PASS getTotalValue" : "FAIL getTotalValue: " + first + ", " + second + ", " + empty.getTotalValue());
        System.exit(ok ? 0 : 1);
    }
}
