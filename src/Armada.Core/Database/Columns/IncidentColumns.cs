namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Models;

    /// <summary>
    /// The incidents row-to-model contract, shared by every provider. The incident itself reads from the stored
    /// snapshot payload; the other columns exist for the read filters and the order.
    /// </summary>
    internal static class IncidentColumns
    {
        /// <summary>
        /// Read an incidents row.
        /// </summary>
        /// <param name="record">Reader positioned on an incidents row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The projected incident.</returns>
        internal static IncidentProjection Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "Incident");
            string? tenantId = row.NullableText("tenant_id");
            string? userId = row.NullableText("user_id");
            Incident incident = IncidentSnapshotCodec.Deserialize(row.Text("payload"), tenantId, userId, row.Utc("last_update_utc"))
                ?? throw new System.InvalidOperationException("Stored incident " + row.Text("id") + " holds no incident payload.");
            return new IncidentProjection
            {
                Incident = incident,
                TenantId = tenantId,
                UserId = userId,
                SnapshotId = row.Text("snapshot_id")
            };
        }

        /// <summary>
        /// Bind every stored incidents column, each in the form its provider stores it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the incidents table.</param>
        /// <param name="projection">Row to bind.</param>
        internal static void Write(StoredParameters parameters, IncidentProjection projection)
        {
            Incident incident = projection.Incident;
            parameters
                .Text("id", incident.Id)
                .Text("tenant_id", projection.TenantId)
                .Text("user_id", projection.UserId)
                .Text("status", incident.Status.ToString())
                .Text("severity", incident.Severity.ToString())
                .Text("title", incident.Title)
                .Text("summary", incident.Summary)
                .Text("environment_id", incident.EnvironmentId)
                .Text("environment_name", incident.EnvironmentName)
                .Text("check_run_id", incident.CheckRunId)
                .Text("deployment_id", incident.DeploymentId)
                .Text("release_id", incident.ReleaseId)
                .Text("vessel_id", incident.VesselId)
                .Text("mission_id", incident.MissionId)
                .Text("voyage_id", incident.VoyageId)
                .Text("impact", incident.Impact)
                .Text("root_cause", incident.RootCause)
                .Text("recovery_notes", incident.RecoveryNotes)
                .Text("postmortem", incident.Postmortem)
                .Utc("detected_utc", incident.DetectedUtc)
                .Utc("last_update_utc", incident.LastUpdateUtc)
                .Text("snapshot_id", projection.SnapshotId)
                .Text("payload", IncidentSnapshotCodec.Serialize(incident));
        }
    }
}
