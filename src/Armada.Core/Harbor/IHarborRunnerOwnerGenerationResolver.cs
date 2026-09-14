namespace Armada.Core.Harbor
{
    using Armada.Core.Models;

    /// <summary>Resolves an enrolled owner together with its durable enrollment generation.</summary>
    public interface IHarborRunnerOwnerGenerationResolver
    {
        /// <summary>
        /// Resolve the current owner and generation. A changed generation invalidates work from an
        /// earlier runner connection, even when the credential and principal are unchanged.
        /// </summary>
        /// <param name="runnerId">Runner identifier.</param>
        /// <param name="owner">Current owner when the enrollment is active.</param>
        /// <param name="generation">Current durable enrollment generation, or zero on failure.</param>
        /// <returns>True only when the enrollment and its principal are valid.</returns>
        bool TryGetOwner(string runnerId, out AuthContext? owner, out long generation);
    }
}
