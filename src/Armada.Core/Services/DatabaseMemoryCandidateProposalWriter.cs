namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Stores a D18 durable-lesson candidate as a row in the memory proposal store. The AI-Memory folder
    /// is read-only to the admiral, so the database is the only place a proposal is written; this writer
    /// takes no filesystem path at all. A subject that was already proposed, in any state, is not
    /// proposed again: an Open proposal is not duplicated and a Dismissed one is not revived. Writing
    /// never throws.
    /// </summary>
    public sealed class DatabaseMemoryCandidateProposalWriter : IMemoryCandidateProposalWriter
    {
        #region Private-Members

        private const string _Header = "[DatabaseMemoryCandidateProposalWriter] ";

        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create a database proposal writer.
        /// </summary>
        /// <param name="database">Database driver holding the proposal store.</param>
        /// <param name="logging">Logging module.</param>
        public DatabaseMemoryCandidateProposalWriter(DatabaseDriver database, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Fingerprint a subject key. Only the fingerprint is stored, so no id reaches the key column.
        /// </summary>
        /// <param name="source">Proposal source.</param>
        /// <param name="subjectKey">Subject key.</param>
        /// <returns>Lowercase hexadecimal SHA-256.</returns>
        public static string FingerprintSubject(string source, string? subjectKey)
        {
            byte[] bytes = Encoding.UTF8.GetBytes((source ?? String.Empty) + "\n" + (subjectKey ?? String.Empty));
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        /// <inheritdoc />
        public async Task<string?> WriteAsync(MemoryCandidateProposal proposal, CancellationToken token)
        {
            if (proposal == null) return null;

            try
            {
                string source = String.IsNullOrWhiteSpace(proposal.Source) ? MemoryProposal.SourcePapercutSweep : proposal.Source.Trim();
                string sourceKey = FingerprintSubject(source, proposal.GroupKey);

                MemoryProposal? existing = await _Database.MemoryProposals.ReadBySourceKeyAsync(sourceKey, token).ConfigureAwait(false);
                if (existing != null) return existing.Id;

                MemoryProposal row = new MemoryProposal
                {
                    TenantId = Constants.DefaultTenantId,
                    Source = source,
                    SourceKey = sourceKey,
                    Title = proposal.Title ?? String.Empty,
                    Body = BuildBody(proposal),
                    TargetHint = proposal.Scope ?? String.Empty,
                    Confidence = proposal.DurableLesson,
                    RelatedRecordIds = new List<string>(proposal.RelatedRecordIds ?? new List<string>()),
                    CreatedUtc = proposal.CreatedUtc,
                    LastUpdateUtc = proposal.CreatedUtc
                };

                MemoryProposal stored = await _Database.MemoryProposals.CreateAsync(row, token).ConfigureAwait(false);
                return stored.Id;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to store memory proposal: " + ex.Message);
                return null;
            }
        }

        #endregion

        #region Private-Methods

        private static string BuildBody(MemoryCandidateProposal proposal)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(String.IsNullOrWhiteSpace(proposal.Detail) ? "(no detail)" : proposal.Detail.Trim());
            sb.Append("\n\nCategory: ").Append(String.IsNullOrWhiteSpace(proposal.Category) ? "(none)" : proposal.Category.Trim());
            if (proposal.Count > 0)
            {
                sb.Append("\nReports: ").Append(proposal.Count.ToString(CultureInfo.InvariantCulture));
                sb.Append("\nDistinct captains: ").Append(proposal.DistinctCaptainCount.ToString(CultureInfo.InvariantCulture));
            }
            if (!String.IsNullOrWhiteSpace(proposal.Vessels))
                sb.Append("\nVessel context: ").Append(proposal.Vessels.Trim());
            return sb.ToString();
        }

        #endregion
    }
}
