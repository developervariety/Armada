namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Models;

    /// <summary>
    /// The tenants row-to-model contract, shared by every provider.
    /// </summary>
    internal static class TenantColumns
    {
        /// <summary>
        /// Read a tenants row.
        /// </summary>
        /// <param name="record">Reader positioned on a tenants row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The tenant.</returns>
        internal static TenantMetadata Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "Tenant");
            TenantMetadata tenant = new TenantMetadata();
            tenant.Id = row.Text("id");
            tenant.Name = row.Text("name");
            tenant.Active = row.Bool("active");
            tenant.IsProtected = row.Bool("is_protected");
            tenant.CreatedUtc = row.Utc("created_utc");
            tenant.LastUpdateUtc = row.Utc("last_update_utc");
            return tenant;
        }

        /// <summary>
        /// Bind every stored tenants column, each in the form its provider stores it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the tenants table.</param>
        /// <param name="tenant">Row to bind.</param>
        internal static void Write(StoredParameters parameters, TenantMetadata tenant)
        {
            parameters
                .Text("id", tenant.Id)
                .Text("name", tenant.Name)
                .Bool("active", tenant.Active)
                .Bool("is_protected", tenant.IsProtected)
                .Utc("created_utc", tenant.CreatedUtc)
                .Utc("last_update_utc", tenant.LastUpdateUtc);
        }
    }
}
