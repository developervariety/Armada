namespace Inventory.Tests
{
    using System.Collections.Generic;

    /// <summary>
    /// Every test that runs. A test method that is not listed here never runs.
    /// </summary>
    public static class TestRegistry
    {
        /// <summary>
        /// The registered tests, in run order.
        /// </summary>
        /// <returns>Tests.</returns>
        public static List<TestCase> All()
        {
            return new List<TestCase>
            {
                new TestCase(nameof(ItemTests.Item_Constructor_SetsValues), ItemTests.Item_Constructor_SetsValues),
                new TestCase(nameof(ItemTests.Item_Constructor_NegativePrice_Throws), ItemTests.Item_Constructor_NegativePrice_Throws),
                new TestCase(nameof(StockTests.Stock_Add_SameSku_MergesQuantity), StockTests.Stock_Add_SameSku_MergesQuantity),
                new TestCase(nameof(StockTests.Stock_Remove_ReducesQuantity), StockTests.Stock_Remove_ReducesQuantity),
                new TestCase(nameof(StockTests.Stock_Remove_UnknownSku_Throws), StockTests.Stock_Remove_UnknownSku_Throws),
                new TestCase(nameof(PriceCalculatorTests.PriceCalculator_LineTotal_Multiplies), PriceCalculatorTests.PriceCalculator_LineTotal_Multiplies)
            };
        }
    }
}
