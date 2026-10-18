namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// The one rule for writing a pipeline reference: an objective's suggested pipeline and a vessel's or
    /// fleet's default pipeline may not be set to a pipeline the operator has deactivated. Only a changed
    /// reference is checked, so a record whose existing reference was retired later can still be edited in
    /// its other fields; dispatch refuses that reference by name when it resolves it.
    /// </summary>
    public static class PipelineReferenceRule
    {
        #region Public-Members

        /// <summary>Source label for an objective's suggested pipeline.</summary>
        public const string ObjectiveSource = "suggested pipeline";

        /// <summary>Source label for a vessel's default pipeline.</summary>
        public const string VesselDefaultSource = "vessel default pipeline";

        /// <summary>Source label for a fleet's default pipeline.</summary>
        public const string FleetDefaultSource = "fleet default pipeline";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Throw <see cref="PipelineInactiveException"/> when <paramref name="reference"/> changes the stored
        /// reference and names only inactive pipelines. A reference that names no pipeline is left to the
        /// existing missing-pipeline handling.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="reference">The pipeline id or name being written; null or empty clears it.</param>
        /// <param name="storedReference">The reference the record holds now; null for a new record.</param>
        /// <param name="source">Where the reference is written, for the refusal message.</param>
        /// <param name="token">Cancellation token.</param>
        public static async Task EnsureNotInactiveAsync(
            DatabaseDriver database,
            string? reference,
            string? storedReference,
            string source,
            CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            string? normalized = String.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
            if (normalized == null) return;
            string? stored = String.IsNullOrWhiteSpace(storedReference) ? null : storedReference.Trim();
            if (String.Equals(normalized, stored, StringComparison.Ordinal)) return;

            Pipeline? inactive = await FindInactiveAsync(database, normalized, token).ConfigureAwait(false);
            if (inactive != null) throw new PipelineInactiveException(inactive.Id, inactive.Name, source);
        }

        /// <summary>
        /// The refusal message for writing <paramref name="reference"/>, or null when the write may proceed.
        /// Same rule as <see cref="EnsureNotInactiveAsync"/>, for surfaces that report errors as values.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="reference">The pipeline id or name being written; null or empty clears it.</param>
        /// <param name="storedReference">The reference the record holds now; null for a new record.</param>
        /// <param name="source">Where the reference is written, for the refusal message.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The refusal message, or null.</returns>
        public static async Task<string?> RefusalAsync(
            DatabaseDriver database,
            string? reference,
            string? storedReference,
            string source,
            CancellationToken token = default)
        {
            try
            {
                await EnsureNotInactiveAsync(database, reference, storedReference, source, token).ConfigureAwait(false);
                return null;
            }
            catch (PipelineInactiveException inactive)
            {
                return inactive.Message;
            }
        }

        #endregion

        #region Private-Methods

        private static async Task<Pipeline?> FindInactiveAsync(DatabaseDriver database, string reference, CancellationToken token)
        {
            Pipeline? byId = await database.Pipelines.ReadAsync(reference, token).ConfigureAwait(false);
            if (byId != null) return byId.Active ? null : byId;

            List<Pipeline> named = (await database.Pipelines.EnumerateAsync(token).ConfigureAwait(false))
                .Where(pipeline => String.Equals(pipeline.Name, reference, StringComparison.Ordinal))
                .ToList();
            if (named.Count == 0 || named.Any(pipeline => pipeline.Active)) return null;
            return named[0];
        }

        #endregion
    }
}
