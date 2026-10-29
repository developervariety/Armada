namespace Inventory.Tests
{
    using System.Collections.Generic;

    /// <summary>
    /// Stock tests.
    /// </summary>
    public static class StockTests
    {
        /// <summary>
        /// Adding the same SKU twice merges the quantities.
        /// </summary>
        public static void Stock_Add_SameSku_MergesQuantity()
        {
            Stock stock = new Stock();
            stock.Add(new Item("A-1", "Anchor", 1m, 2));
            stock.Add(new Item("A-1", "Anchor", 1m, 3));
            Check.Equal(1, stock.Count);
            Check.Equal(5, stock.TotalUnits());
        }

        /// <summary>
        /// Removing units lowers the quantity.
        /// </summary>
        public static void Stock_Remove_ReducesQuantity()
        {
            Stock stock = new Stock();
            stock.Add(new Item("A-1", "Anchor", 1m, 5));
            stock.Remove("A-1", 2);
            Check.Equal(3, stock.Find("A-1")!.Quantity);
        }

        /// <summary>
        /// Removing an unknown SKU throws.
        /// </summary>
        public static void Stock_Remove_UnknownSku_Throws()
        {
            Stock stock = new Stock();
            Check.Throws<KeyNotFoundException>(() => stock.Remove("missing", 1));
        }
    }
}
