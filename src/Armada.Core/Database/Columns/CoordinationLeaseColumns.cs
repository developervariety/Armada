namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Models;

    /// <summary>
    /// The coordination_leases row-to-model contract, shared by every provider. Every lease read selects the whole
    /// row, so each column is read as present.
    /// </summary>
    internal static class CoordinationLeaseColumns
    {
        /// <summary>
        /// Read a coordination_leases row.
        /// </summary>
        /// <param name="record">Reader positioned on a coordination_leases row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The lease.</returns>
        internal static CoordinationLease Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "CoordinationLease");
            CoordinationLease lease = new CoordinationLease();
            lease.Name = row.Text("name");
            lease.Holder = row.Text("holder");
            lease.TenantId = row.NullableText("tenant_id");
            lease.AcquiredUtc = row.Utc("acquired_utc");
            lease.ExpiresUtc = row.Utc("expires_utc");
            return lease;
        }
    }
}
