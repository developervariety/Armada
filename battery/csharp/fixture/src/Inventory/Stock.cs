namespace Inventory
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Items on hand, keyed by SKU.
    /// </summary>
    public class Stock
    {
        private readonly Dictionary<string, Item> _Items = new Dictionary<string, Item>(StringComparer.Ordinal);

        /// <summary>
        /// Number of distinct items.
        /// </summary>
        public int Count
        {
            get { return _Items.Count; }
        }

        /// <summary>
        /// Add an item, or add its quantity to the item with the same SKU.
        /// </summary>
        /// <param name="item">Item.</param>
        /// <exception cref="ArgumentNullException">Thrown when item is null.</exception>
        public void Add(Item item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (_Items.TryGetValue(item.Sku, out Item? existing))
            {
                existing.Quantity += item.Quantity;
                return;
            }

            _Items[item.Sku] = item;
        }

        /// <summary>
        /// Remove units of an item.
        /// </summary>
        /// <param name="sku">SKU.</param>
        /// <param name="quantity">Units to remove.</param>
        /// <exception cref="ArgumentNullException">Thrown when sku is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when quantity is not positive.</exception>
        /// <exception cref="KeyNotFoundException">Thrown when the SKU is unknown.</exception>
        /// <exception cref="InvalidOperationException">Thrown when fewer units are on hand than requested.</exception>
        public void Remove(string sku, int quantity)
        {
            if (sku == null) throw new ArgumentNullException(nameof(sku));
            if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
            if (!_Items.TryGetValue(sku, out Item? existing)) throw new KeyNotFoundException("Unknown SKU " + sku + ".");
            if (existing.Quantity < quantity) throw new InvalidOperationException("Only " + existing.Quantity + " units of " + sku + " on hand.");
            existing.Quantity -= quantity;
        }

        /// <summary>
        /// Find an item.
        /// </summary>
        /// <param name="sku">SKU.</param>
        /// <returns>The item, or null.</returns>
        /// <exception cref="ArgumentNullException">Thrown when sku is null.</exception>
        public Item? Find(string sku)
        {
            if (sku == null) throw new ArgumentNullException(nameof(sku));
            return _Items.TryGetValue(sku, out Item? item) ? item : null;
        }

        /// <summary>
        /// Units on hand across every item.
        /// </summary>
        /// <returns>Total units.</returns>
        public int TotalUnits()
        {
            int total = 0;
            foreach (Item item in _Items.Values) total += item.Quantity;
            return total;
        }
    }
}
