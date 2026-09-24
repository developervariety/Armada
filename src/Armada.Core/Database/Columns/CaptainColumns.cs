namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The captains row-to-model contract, shared by every provider. Every provider's schema carries every captain
    /// column and every captain read selects the whole row, so each column is read as present. The process
    /// identifier is read before its start time, because a new identifier clears the start time on the model.
    /// </summary>
    internal static class CaptainColumns
    {
        /// <summary>
        /// Read a captains row.
        /// </summary>
        /// <param name="record">Reader positioned on a captains row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The captain.</returns>
        internal static Captain Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "Captain");
            Captain captain = new Captain();
            captain.Id = row.Text("id");
            captain.TenantId = row.NullableText("tenant_id");
            captain.UserId = row.NullableText("user_id");
            captain.Name = row.Text("name");
            captain.Runtime = row.Enum<AgentRuntimeEnum>("runtime");
            captain.Model = row.NullableText("model");
            captain.ModelEndpointId = row.NullableText("model_endpoint_id");
            captain.ApiKey = row.NullableText("api_key");
            captain.ApiBaseUrl = row.NullableText("api_base_url");
            captain.SystemInstructions = row.NullableText("system_instructions");
            captain.AllowedPersonas = row.NullableText("allowed_personas");
            captain.PreferredPersona = row.NullableText("preferred_persona");
            captain.RuntimeOptionsJson = row.NullableText("runtime_options_json");
            captain.Tier = row.NullableEnum<CaptainTierEnum>("tier");
            captain.PreferenceRank = row.Int(TierRoutingPersistence.PreferenceRankColumn);
            captain.State = row.Enum<CaptainStateEnum>("state");
            captain.CurrentMissionId = row.NullableText("current_mission_id");
            captain.CurrentDockId = row.NullableText("current_dock_id");
            captain.ProcessId = row.NullableInt("process_id");
            captain.ProcessStartedUtc = row.NullableUtc("process_started_utc");
            captain.RecoveryAttempts = row.Int("recovery_attempts");
            captain.LastHeartbeatUtc = row.NullableUtc("last_heartbeat_utc");
            captain.LastProcessAliveUtc = row.NullableUtc("last_process_alive_utc");
            captain.QuarantineUntilUtc = row.NullableUtc("quarantine_until_utc");
            captain.QuarantineReason = row.NullableText("quarantine_reason");
            captain.DefaultPlaybooks = row.NullableText("default_playbooks");
            captain.CreatedUtc = row.Utc("created_utc");
            captain.LastUpdateUtc = row.Utc("last_update_utc");
            return captain;
        }
    }
}
