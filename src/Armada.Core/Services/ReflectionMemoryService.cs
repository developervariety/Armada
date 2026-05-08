namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Reads the per-vessel learned-facts playbook and reflection rejection notes.
    /// </summary>
    public class ReflectionMemoryService : IReflectionMemoryService
    {
        #region Private-Members

        private readonly DatabaseDriver _Database;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        public ReflectionMemoryService(DatabaseDriver database)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<string> ReadLearnedPlaybookContentAsync(Vessel vessel, CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (String.IsNullOrEmpty(vessel.TenantId))
                return "# Vessel Learned Facts\n\nNo accepted reflection facts yet.";

            string fileName = "vessel-" + SanitizeName(vessel.Name) + "-learned.md";
            Playbook? playbook = await _Database.Playbooks.ReadByFileNameAsync(vessel.TenantId!, fileName, token).ConfigureAwait(false);
            return playbook?.Content ?? "# Vessel Learned Facts\n\nNo accepted reflection facts yet.";
        }

        /// <inheritdoc />
        public Task<List<string>> ReadRejectedProposalNotesAsync(Vessel vessel, CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));

            // Rejection persistence lands in a later milestone. Keep this as the shared
            // service seam so accept/reject can fill it without changing dispatcher callers.
            return Task.FromResult(new List<string>());
        }

        #endregion

        #region Private-Methods

        private static string SanitizeName(string name)
        {
            string lower = name.ToLowerInvariant();
            string replaced = Regex.Replace(lower, "[^a-z0-9]+", "-");
            return replaced.Trim('-');
        }

        #endregion
    }
}
