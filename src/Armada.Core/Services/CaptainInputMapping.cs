namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The one definition of which captain fields a create or update request may set.
    /// Configuration fields come from the caller. Server-owned fields (identity, state,
    /// assignment, process, recovery, heartbeat, quarantine and timestamps) change only
    /// through the server's own lifecycle and the operator tools that own them (stop,
    /// quarantine/bench, unquarantine/unbench). A request that sends a server-owned field
    /// with a value other than its default (create) or its stored value (update) is refused
    /// with an error that names every such field; nothing is written.
    /// </summary>
    public static class CaptainInputMapping
    {
        #region Public-Members

        /// <summary>
        /// Stable error code carried at the start of every server-owned field refusal.
        /// </summary>
        public const string ServerOwnedFieldErrorCode = "captain_server_owned_field";

        /// <summary>
        /// Captain properties a create or update request may set.
        /// </summary>
        public static readonly IReadOnlyList<string> CallerSettableFieldNames = new List<string>
        {
            nameof(Captain.Name),
            nameof(Captain.Runtime),
            nameof(Captain.Model),
            nameof(Captain.ModelEndpointId),
            nameof(Captain.ApiKey),
            nameof(Captain.ApiBaseUrl),
            nameof(Captain.SystemInstructions),
            nameof(Captain.AllowedPersonas),
            nameof(Captain.PreferredPersona),
            nameof(Captain.RuntimeOptionsJson),
            nameof(Captain.Tier),
            nameof(Captain.PreferenceRank),
            nameof(Captain.DefaultPlaybooks)
        };

        /// <summary>
        /// Captain properties only the server writes.
        /// </summary>
        public static readonly IReadOnlyList<string> ServerOwnedFieldNames = new List<string>
        {
            nameof(Captain.Id),
            nameof(Captain.TenantId),
            nameof(Captain.UserId),
            nameof(Captain.State),
            nameof(Captain.CurrentMissionId),
            nameof(Captain.CurrentDockId),
            nameof(Captain.ProcessId),
            nameof(Captain.ProcessStartedUtc),
            nameof(Captain.RecoveryAttempts),
            nameof(Captain.LastHeartbeatUtc),
            nameof(Captain.LastProcessAliveUtc),
            nameof(Captain.QuarantineUntilUtc),
            nameof(Captain.QuarantineReason),
            nameof(Captain.CreatedUtc),
            nameof(Captain.LastUpdateUtc)
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build a new captain from a create request. Only caller-settable fields are taken
        /// from <paramref name="input"/>; every server-owned field starts at its default, so the
        /// captain is Idle, unassigned and not quarantined. The caller stamps tenant and user.
        /// </summary>
        /// <param name="input">Captain as parsed from the request.</param>
        /// <returns>New captain.</returns>
        public static Captain ForCreate(Captain input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            Captain captain = new Captain();
            CopyConfiguration(input, captain);
            return captain;
        }

        /// <summary>
        /// Build the updated captain row. Caller-settable fields are taken from
        /// <paramref name="input"/>; every server-owned field is kept from <paramref name="existing"/>,
        /// and the update timestamp is refreshed.
        /// </summary>
        /// <param name="existing">Stored captain.</param>
        /// <param name="input">Captain as parsed from the request.</param>
        /// <returns>Captain to persist.</returns>
        public static Captain ForUpdate(Captain existing, Captain input)
        {
            if (existing == null) throw new ArgumentNullException(nameof(existing));
            if (input == null) throw new ArgumentNullException(nameof(input));
            Captain captain = new Captain();
            captain.Id = existing.Id;
            captain.TenantId = existing.TenantId;
            captain.UserId = existing.UserId;
            captain.State = existing.State;
            captain.CurrentMissionId = existing.CurrentMissionId;
            captain.CurrentDockId = existing.CurrentDockId;
            captain.ProcessId = existing.ProcessId;
            captain.ProcessStartedUtc = existing.ProcessStartedUtc;
            captain.RecoveryAttempts = existing.RecoveryAttempts;
            captain.LastHeartbeatUtc = existing.LastHeartbeatUtc;
            captain.LastProcessAliveUtc = existing.LastProcessAliveUtc;
            captain.QuarantineUntilUtc = existing.QuarantineUntilUtc;
            captain.QuarantineReason = existing.QuarantineReason;
            captain.CreatedUtc = existing.CreatedUtc;
            captain.LastUpdateUtc = DateTime.UtcNow;
            CopyConfiguration(input, captain);
            return captain;
        }

        /// <summary>
        /// Copy only the caller-settable fields of a captain into a new instance, for surfaces
        /// that edit a stored captain field by field before calling <see cref="ForUpdate"/>.
        /// </summary>
        /// <param name="source">Captain to copy.</param>
        /// <returns>New captain carrying the configuration only.</returns>
        public static Captain ConfigurationOf(Captain source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            Captain captain = new Captain();
            CopyConfiguration(source, captain);
            return captain;
        }

        /// <summary>
        /// Build a create or update request body that carries only the caller-settable fields of
        /// <paramref name="captain"/>, so a client can send a whole captain object (including one
        /// read back from the server) without sending its server-owned fields.
        /// </summary>
        /// <param name="captain">Captain whose configuration is sent.</param>
        /// <returns>Request body keyed by caller-settable field name.</returns>
        public static Dictionary<string, object?> ToRequestBody(Captain captain)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            Dictionary<string, object?> body = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (string field in CallerSettableFieldNames)
            {
                System.Reflection.PropertyInfo property = typeof(Captain).GetProperty(field)
                    ?? throw new InvalidOperationException("Captain has no caller-settable property named " + field + ".");
                body[field] = property.GetValue(captain);
            }
            return body;
        }

        /// <summary>
        /// Name every server-owned field the request tries to write. A field counts as a write
        /// when it is present and differs from its default (create, <paramref name="existing"/> is null)
        /// or from the stored value (update). Returns null when the request writes none.
        /// </summary>
        /// <param name="submitted">Server-owned fields parsed from the request body; null when absent.</param>
        /// <param name="existing">Stored captain for an update; null for a create.</param>
        /// <returns>Error message naming the refused fields, or null.</returns>
        public static string? FindServerOwnedFieldViolation(CaptainServerOwnedFields? submitted, Captain? existing)
        {
            if (submitted == null) return null;

            List<string> refused = new List<string>();
            AddIfStringWrite(refused, nameof(Captain.Id), submitted.Id, existing?.Id);
            AddIfStringWrite(refused, nameof(Captain.TenantId), submitted.TenantId, existing?.TenantId);
            AddIfStringWrite(refused, nameof(Captain.UserId), submitted.UserId, existing?.UserId);
            if (submitted.State.HasValue && submitted.State.Value != (existing?.State ?? CaptainStateEnum.Idle))
                refused.Add(nameof(Captain.State));
            AddIfStringWrite(refused, nameof(Captain.CurrentMissionId), submitted.CurrentMissionId, existing?.CurrentMissionId);
            AddIfStringWrite(refused, nameof(Captain.CurrentDockId), submitted.CurrentDockId, existing?.CurrentDockId);
            if (submitted.ProcessId.HasValue && submitted.ProcessId != existing?.ProcessId)
                refused.Add(nameof(Captain.ProcessId));
            AddIfTimeWrite(refused, nameof(Captain.ProcessStartedUtc), submitted.ProcessStartedUtc, existing?.ProcessStartedUtc);
            if (submitted.RecoveryAttempts.HasValue && submitted.RecoveryAttempts.Value != (existing?.RecoveryAttempts ?? 0))
                refused.Add(nameof(Captain.RecoveryAttempts));
            AddIfTimeWrite(refused, nameof(Captain.LastHeartbeatUtc), submitted.LastHeartbeatUtc, existing?.LastHeartbeatUtc);
            AddIfTimeWrite(refused, nameof(Captain.LastProcessAliveUtc), submitted.LastProcessAliveUtc, existing?.LastProcessAliveUtc);
            AddIfTimeWrite(refused, nameof(Captain.QuarantineUntilUtc), submitted.QuarantineUntilUtc, existing?.QuarantineUntilUtc);
            AddIfStringWrite(refused, nameof(Captain.QuarantineReason), submitted.QuarantineReason, existing?.QuarantineReason);
            AddIfTimeWrite(refused, nameof(Captain.CreatedUtc), submitted.CreatedUtc, existing?.CreatedUtc);
            AddIfTimeWrite(refused, nameof(Captain.LastUpdateUtc), submitted.LastUpdateUtc, existing?.LastUpdateUtc);

            if (refused.Count == 0) return null;

            return ServerOwnedFieldErrorCode + ": captain field" + (refused.Count == 1 ? " " : "s ") + String.Join(", ", refused)
                + (refused.Count == 1 ? " is" : " are") + " server-owned and cannot be set by a create or update request. "
                + "Send only " + String.Join(", ", CallerSettableFieldNames) + ". "
                + "Change captain state with the stop, quarantine (bench) or unquarantine (unbench) operations.";
        }

        #endregion

        #region Private-Methods

        private static void CopyConfiguration(Captain source, Captain target)
        {
            target.Name = source.Name;
            target.Runtime = source.Runtime;
            target.Model = source.Model;
            target.ModelEndpointId = source.ModelEndpointId;
            target.ApiKey = source.ApiKey;
            target.ApiBaseUrl = source.ApiBaseUrl;
            target.SystemInstructions = source.SystemInstructions;
            target.AllowedPersonas = source.AllowedPersonas;
            target.PreferredPersona = source.PreferredPersona;
            target.RuntimeOptionsJson = source.RuntimeOptionsJson;
            target.Tier = source.Tier;
            target.PreferenceRank = source.PreferenceRank;
            target.DefaultPlaybooks = source.DefaultPlaybooks;
        }

        private static void AddIfStringWrite(List<string> refused, string field, string? submitted, string? baseline)
        {
            if (String.IsNullOrEmpty(submitted)) return;
            if (String.Equals(submitted, baseline, StringComparison.Ordinal)) return;
            refused.Add(field);
        }

        private static void AddIfTimeWrite(List<string> refused, string field, DateTime? submitted, DateTime? baseline)
        {
            if (!submitted.HasValue) return;
            if (baseline.HasValue && Math.Abs((ToUtc(submitted.Value) - ToUtc(baseline.Value)).TotalMilliseconds) < 1) return;
            refused.Add(field);
        }

        private static DateTime ToUtc(DateTime value)
        {
            return value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
                : value.ToUniversalTime();
        }

        #endregion
    }
}
