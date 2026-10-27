namespace Inventory.Tests
{
    /// <summary>
    /// PriceCalculator tests.
    /// </summary>
    public static class PriceCalculatorTests
    {
        /// <summary>
        /// A line total is the unit price times the quantity.
        /// </summary>
        public static void PriceCalculator_LineTotal_Multiplies()
        {
            Check.Equal(7.50m, PriceCalculator.LineTotal(2.50m, 3));
        }

        /// <summary>
        /// Ten percent off 80.00 is 72.00.
        /// </summary>
        public static void PriceCalculator_ApplyDiscount_TenPercent()
        {
            Check.Equal(72.00m, PriceCalculator.ApplyDiscount(80.00m, 10m));
        }

        /// <summary>
        /// No discount keeps the price.
        /// </summary>
        public static void PriceCalculator_ApplyDiscount_Zero_KeepsPrice()
        {
            Check.Equal(19.99m, PriceCalculator.ApplyDiscount(19.99m, 0m));
        }
    }
}
