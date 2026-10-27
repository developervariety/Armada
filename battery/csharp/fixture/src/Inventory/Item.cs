namespace Inventory
{
    using System;

    /// <summary>
    /// A stock-keeping item.
    /// </summary>
    public class Item
    {
        /// <summary>
        /// Stock-keeping unit code.
        /// </summary>
        public string Sku { get; }

        /// <summary>
        /// Display name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Price of one unit.
        /// </summary>
        public decimal UnitPrice { get; }

        /// <summary>
        /// Units on hand.
        /// </summary>
        public int Quantity { get; set; }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="sku">Stock-keeping unit code.</param>
        /// <param name="name">Display name.</param>
        /// <param name="unitPrice">Price of one unit.</param>
        /// <param name="quantity">Units on hand.</param>
        /// <exception cref="ArgumentNullException">Thrown when sku or name is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when unitPrice or quantity is negative.</exception>
        public Item(string sku, string name, decimal unitPrice, int quantity)
        {
            if (unitPrice < 0) throw new ArgumentOutOfRangeException(nameof(unitPrice));
            if (quantity < 0) throw new ArgumentOutOfRangeException(nameof(quantity));
            Sku = sku ?? throw new ArgumentNullException(nameof(sku));
            Name = name ?? throw new ArgumentNullException(nameof(name));
            UnitPrice = unitPrice;
            Quantity = quantity;
        }
    }
}
