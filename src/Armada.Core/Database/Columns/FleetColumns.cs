namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Models;

    /// <summary>
    /// The fleets row-to-model contract, shared by every provider. Every provider's schema carries the default
    /// pipeline and default playbook columns and every fleet write sets them, so they are read as required columns.
    /// </summary>
    internal static class FleetColumns
    {
        /// <summary>
        /// Read a fleets row.
        /// </summary>
        /// <param name="record">Reader positioned on a fleets row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The fleet.</returns>
        internal static Fleet Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "Fleet");
            Fleet fleet = new Fleet();
            fleet.Id = row.Text("id");
            fleet.TenantId = row.NullableText("tenant_id");
            fleet.UserId = row.NullableText("user_id");
            fleet.Name = row.Text("name");
            fleet.Description = row.NullableText("description");
            fleet.DefaultPipelineId = row.NullableText("default_pipeline_id");
            fleet.DefaultPlaybooks = row.NullableText("default_playbooks");
            fleet.Active = row.Bool("active");
            fleet.CreatedUtc = row.Utc("created_utc");
            fleet.LastUpdateUtc = row.Utc("last_update_utc");
            return fleet;
        }

        /// <summary>
        /// Bind every stored fleets column, each in the form its provider stores it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the fleets table.</param>
        /// <param name="fleet">Row to bind.</param>
        internal static void Write(StoredParameters parameters, Fleet fleet)
        {
            parameters
                .Text("id", fleet.Id)
                .Text("tenant_id", fleet.TenantId)
                .Text("user_id", fleet.UserId)
                .Text("name", fleet.Name)
                .Text("description", fleet.Description)
                .Text("default_pipeline_id", fleet.DefaultPipelineId)
                .Text("default_playbooks", fleet.DefaultPlaybooks)
                .Bool("active", fleet.Active)
                .Utc("created_utc", fleet.CreatedUtc)
                .Utc("last_update_utc", fleet.LastUpdateUtc);
        }
    }
}
