namespace Armada.Core.Enums
{
    /// <summary>Durable owner of a Harbor checkout cleanup intent.</summary>
    public enum HarborCheckoutLifecyclePurposeEnum
    {
        /// <summary>Mission or operator reclaim.</summary>
        Reclaim = 0,
        /// <summary>Explicit dock deletion.</summary>
        Delete = 1,
        /// <summary>Explicit dock purge.</summary>
        Purge = 2
    }
}
