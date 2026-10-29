namespace Armada.Server.Routes
{
    using Armada.Core.Enums;

    /// <summary>
    /// Reads only the order field of a memory enumeration body, to tell an explicit order from the default value
    /// of <c>EnumerationQuery.Order</c>. A body without an order keeps the historical relevance order.
    /// </summary>
    internal class MemoryEnumerateOrderProbe
    {
        /// <summary>
        /// Order as supplied by the caller, or null when the body has none.
        /// </summary>
        public EnumerationOrderEnum? Order { get; set; } = null;
    }
}
