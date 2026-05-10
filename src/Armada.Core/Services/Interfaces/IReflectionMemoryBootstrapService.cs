namespace Armada.Core.Services.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Idempotent startup bootstrap that ensures every vessel has a learned-facts playbook
    /// attached through DefaultPlaybooks. v2-F2 also bootstraps persona-learned playbooks
    /// for every registered persona.
    /// </summary>
    public interface IReflectionMemoryBootstrapService
    {
        /// <summary>
        /// Run the bootstrap. Creates learned-facts playbooks for vessels that lack one
        /// and attaches them to DefaultPlaybooks. v2-F2 also creates persona-learned
        /// playbooks for personas that lack one. Safe to call repeatedly.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        Task BootstrapAsync(CancellationToken token = default);

        /// <summary>
        /// Bootstrap a single persona's learned playbook (v2-F2). Late-arrival path for
        /// personas registered after install. Idempotent.
        /// </summary>
        /// <param name="persona">Persona to bootstrap.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Bootstrapped (or already-existing) persona-learned playbook id.</returns>
        Task<string> BootstrapPersonaAsync(Persona persona, CancellationToken token = default);
    }
}
