namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Models;

    /// <summary>
    /// The events row-to-model contract, shared by every provider.
    /// </summary>
    internal static class EventColumns
    {
        /// <summary>
        /// Read an events row.
        /// </summary>
        /// <param name="record">Reader positioned on an events row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The event.</returns>
        internal static ArmadaEvent Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "Event");
            ArmadaEvent evt = new ArmadaEvent();
            evt.Id = row.Text("id");
            evt.TenantId = row.NullableText("tenant_id");
            evt.UserId = row.NullableText("user_id");
            evt.EventType = row.Text("event_type");
            evt.EntityType = row.NullableText("entity_type");
            evt.EntityId = row.NullableText("entity_id");
            evt.CaptainId = row.NullableText("captain_id");
            evt.MissionId = row.NullableText("mission_id");
            evt.VesselId = row.NullableText("vessel_id");
            evt.VoyageId = row.NullableText("voyage_id");
            evt.Message = row.Text("message");
            evt.Payload = row.NullableText("payload");
            evt.CreatedUtc = row.Utc("created_utc");
            return evt;
        }

        /// <summary>
        /// Bind every stored events column, each in the form its provider stores it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the events table.</param>
        /// <param name="armadaEvent">Row to bind.</param>
        internal static void Write(StoredParameters parameters, ArmadaEvent armadaEvent)
        {
            parameters
                .Text("id", armadaEvent.Id)
                .Text("tenant_id", armadaEvent.TenantId)
                .Text("user_id", armadaEvent.UserId)
                .Text("event_type", armadaEvent.EventType)
                .Text("entity_type", armadaEvent.EntityType)
                .Text("entity_id", armadaEvent.EntityId)
                .Text("captain_id", armadaEvent.CaptainId)
                .Text("mission_id", armadaEvent.MissionId)
                .Text("vessel_id", armadaEvent.VesselId)
                .Text("voyage_id", armadaEvent.VoyageId)
                .Text("message", armadaEvent.Message)
                .Text("payload", armadaEvent.Payload)
                .Utc("created_utc", armadaEvent.CreatedUtc);
        }
    }
}
