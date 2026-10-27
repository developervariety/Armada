namespace Inventory.Tests
{
    using System;

    /// <summary>
    /// Item tests.
    /// </summary>
    public static class ItemTests
    {
        /// <summary>
        /// The constructor keeps its arguments.
        /// </summary>
        public static void Item_Constructor_SetsValues()
        {
            Item item = new Item("A-1", "Anchor", 12.50m, 4);
            Check.Equal("A-1", item.Sku);
            Check.Equal("Anchor", item.Name);
            Check.Equal(12.50m, item.UnitPrice);
            Check.Equal(4, item.Quantity);
        }

        /// <summary>
        /// A negative price is refused.
        /// </summary>
        public static void Item_Constructor_NegativePrice_Throws()
        {
            Check.Throws<ArgumentOutOfRangeException>(() => new Item("A-1", "Anchor", -1m, 1));
        }
    }
}
