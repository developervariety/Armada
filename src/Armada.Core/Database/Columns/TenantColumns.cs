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
    }
}
