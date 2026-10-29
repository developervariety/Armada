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
    }
}
