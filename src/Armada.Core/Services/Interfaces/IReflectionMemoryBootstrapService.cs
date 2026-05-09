namespace Armada.Core.Services.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Idempotent startup bootstrap that ensures every vessel has a learned-facts playbook
    /// attached through DefaultPlaybooks.
    /// </summary>
    public interface IReflectionMemoryBootstrapService
    {
        /// <summary>
        /// Run the bootstrap. Creates learned-facts playbooks for vessels that lack one
        /// and attaches them to DefaultPlaybooks. Safe to call repeatedly.
        /// </summary>
        Task BootstrapAsync(CancellationToken token = default);
    }
}
