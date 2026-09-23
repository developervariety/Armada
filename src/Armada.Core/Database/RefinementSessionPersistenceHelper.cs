namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// The one read rule every database provider applies to objective refinement session rows.
    /// </summary>
    /// <remarks>
    /// A stored status that is not an <see cref="ObjectiveRefinementSessionStatusEnum"/> member raises
    /// <see cref="StoredRefinementSessionDataException"/> naming the row and the field. It never reads as
    /// <c>Created</c>. A single-row read raises the error; a list read skips the row with one warning that
    /// counts and names the skipped rows. A null or empty status reads as <c>Created</c>.
    /// </remarks>
    internal static class RefinementSessionPersistenceHelper
    {
        /// <summary>Stored column that holds the session status.</summary>
        internal const string StatusField = "status";

        /// <summary>
        /// Read a stored session status. Null or empty reads as <c>Created</c>; any other value must name a
        /// defined member.
        /// </summary>
        internal static ObjectiveRefinementSessionStatusEnum ParseStatus(object? value, string sessionId)
        {
            string? raw = value == null || value == DBNull.Value ? null : value.ToString();
            if (String.IsNullOrWhiteSpace(raw))
                return ObjectiveRefinementSessionStatusEnum.Created;

            if (Enum.TryParse<ObjectiveRefinementSessionStatusEnum>(raw, true, out ObjectiveRefinementSessionStatusEnum parsed)
                && Enum.IsDefined(parsed))
                return parsed;

            throw new StoredRefinementSessionDataException(sessionId, StatusField,
                "holds '" + raw + "', which is not a " + nameof(ObjectiveRefinementSessionStatusEnum) + " value");
        }

        /// <summary>
        /// Read every session row from <paramref name="reader"/>. A row whose stored data cannot be read is left out
        /// of the list and logged as one warning that counts the skipped rows and names each session and field.
        /// </summary>
        internal static async Task<List<ObjectiveRefinementSession>> ReadRowsAsync(
            DbDataReader reader,
            Func<ObjectiveRefinementSession> decodeCurrentRow,
            LoggingModule logging,
            CancellationToken token)
        {
            List<ObjectiveRefinementSession> results = new List<ObjectiveRefinementSession>();
            List<StoredRefinementSessionDataException> skipped = new List<StoredRefinementSessionDataException>();
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                try
                {
                    results.Add(decodeCurrentRow());
                }
                catch (StoredRefinementSessionDataException ex)
                {
                    skipped.Add(ex);
                }
            }

            if (skipped.Count > 0)
            {
                string names = String.Join("; ", skipped.Select(ex => ex.SessionId + " (" + ex.Field + ")"));
                logging.Warn("[ObjectiveRefinementSessions] skipped " + skipped.Count + " refinement session row(s) whose stored data cannot be read; "
                    + "the other " + results.Count + " row(s) are returned. Repair: " + names);
            }

            return results;
        }
    }
}
