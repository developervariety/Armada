namespace Armada.Core.Services.Interfaces
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Reads the current learned memory state used to build reflection briefs.
    /// </summary>
    public interface IReflectionMemoryService
    {
        /// <summary>
        /// Read the current learned-facts playbook content for a vessel.
        /// </summary>
        /// <param name="vessel">Vessel whose learned playbook should be read.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Current learned playbook markdown, or an empty marker when none exists.</returns>
        Task<string> ReadLearnedPlaybookContentAsync(Vessel vessel, CancellationToken token = default);

        /// <summary>
        /// Read recently rejected reflection proposal notes for a vessel.
        /// </summary>
        /// <param name="vessel">Vessel whose rejected proposals should be read.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Rejected proposal notes. Empty until rejection persistence is available.</returns>
        Task<List<string>> ReadRejectedProposalNotesAsync(Vessel vessel, CancellationToken token = default);
    }
}
