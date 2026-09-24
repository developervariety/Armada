namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Models;

    /// <summary>
    /// The credentials row-to-model contract, shared by every provider.
    /// </summary>
    internal static class CredentialColumns
    {
        /// <summary>
        /// Read a credentials row.
        /// </summary>
        /// <param name="record">Reader positioned on a credentials row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The credential.</returns>
        internal static Credential Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "Credential");
            Credential credential = new Credential();
            credential.Id = row.Text("id");
            credential.TenantId = row.Text("tenant_id");
            credential.UserId = row.Text("user_id");
            credential.Name = row.NullableText("name");
            credential.BearerToken = row.Text("bearer_token");
            credential.Active = row.Bool("active");
            credential.IsProtected = row.Bool("is_protected");
            credential.CreatedUtc = row.Utc("created_utc");
            credential.LastUpdateUtc = row.Utc("last_update_utc");
            return credential;
        }
    }
}
