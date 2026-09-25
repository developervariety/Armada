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

        /// <summary>
        /// Bind every stored credentials column, each in the form its provider stores it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the credentials table.</param>
        /// <param name="credential">Row to bind.</param>
        internal static void Write(StoredParameters parameters, Credential credential)
        {
            parameters
                .Text("id", credential.Id)
                .Text("tenant_id", credential.TenantId)
                .Text("user_id", credential.UserId)
                .Text("name", credential.Name)
                .Text("bearer_token", credential.BearerToken)
                .Bool("active", credential.Active)
                .Bool("is_protected", credential.IsProtected)
                .Utc("created_utc", credential.CreatedUtc)
                .Utc("last_update_utc", credential.LastUpdateUtc);
        }
    }
}
