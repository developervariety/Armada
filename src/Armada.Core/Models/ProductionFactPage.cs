namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Bounded result of a production fact query.
    /// </summary>
    /// <typeparam name="T">Fact type.</typeparam>
    public sealed class ProductionFactPage<T>
    {
        /// <summary>Facts in ascending creation order.</summary>
        public List<T> Items { get; set; } = new List<T>();

        /// <summary>True when more rows matched than the query limit returned.</summary>
        public bool Truncated { get; set; } = false;
    }
}
