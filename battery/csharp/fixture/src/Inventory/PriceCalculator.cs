namespace Inventory
{
    using System;

    /// <summary>
    /// Price arithmetic.
    /// </summary>
    public static class PriceCalculator
    {
        /// <summary>
        /// Apply a percentage discount to a price, rounded to cents.
        /// </summary>
        /// <param name="price">Price.</param>
        /// <param name="percent">Discount in percent, 0 to 100.</param>
        /// <returns>The discounted price.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when price is negative or percent is outside 0 to 100.</exception>
        public static decimal ApplyDiscount(decimal price, decimal percent)
        {
            if (price < 0) throw new ArgumentOutOfRangeException(nameof(price));
            if (percent < 0 || percent > 100) throw new ArgumentOutOfRangeException(nameof(percent));
            return Math.Round(price - percent, 2, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Total of a line: unit price times quantity.
        /// </summary>
        /// <param name="unitPrice">Unit price.</param>
        /// <param name="quantity">Quantity.</param>
        /// <returns>The line total.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when either value is negative.</exception>
        public static decimal LineTotal(decimal unitPrice, int quantity)
        {
            if (unitPrice < 0) throw new ArgumentOutOfRangeException(nameof(unitPrice));
            if (quantity < 0) throw new ArgumentOutOfRangeException(nameof(quantity));
            return unitPrice * quantity;
        }
    }
}
