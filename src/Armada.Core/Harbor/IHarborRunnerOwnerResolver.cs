namespace Armada.Core.Harbor
{
    using Armada.Core.Models;

    /// <summary>
    /// Resolves the authoritative verified owner of a Harbor runner identifier.
    /// Implementations must read durable enrollment or equivalent owner state that
    /// is independent of the live session registry.
    /// </summary>
    public interface IHarborRunnerOwnerResolver
    {
        /// <summary>
        /// Resolve a runner's enrolled owner authentication context.
        /// </summary>
        /// <param name="runnerId">Runner identifier.</param>
        /// <param name="owner">Authoritative verified owner when enrolled.</param>
        /// <returns>True when the runner is enrolled.</returns>
        bool TryGetOwner(string runnerId, out AuthContext? owner);
    }
}
