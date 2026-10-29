namespace Inventory.Grading
{
    using System;

    /// <summary>
    /// Hidden check for the add-member task: Item.TotalValue is UnitPrice times Quantity and follows Quantity changes.
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// Entry point.
        /// </summary>
        /// <returns>0 when the member behaves as asked.</returns>
        public static int Main()
        {
            Item item = new Item("A-1", "Anchor", 2.50m, 4);
            decimal first = item.TotalValue;
            item.Quantity = 6;
            decimal second = item.TotalValue;
            Item empty = new Item("B-1", "Buoy", 9.99m, 0);
            bool ok = first == 10.00m && second == 15.00m && empty.TotalValue == 0m;
            Console.WriteLine(ok ? "PASS TotalValue" : "FAIL TotalValue: " + first + ", " + second + ", " + empty.TotalValue);
            return ok ? 0 : 1;
        }
    }
}
