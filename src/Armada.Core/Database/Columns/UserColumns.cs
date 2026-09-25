namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Models;

    /// <summary>
    /// The users row-to-model contract, shared by every provider.
    /// </summary>
    internal static class UserColumns
    {
        /// <summary>
        /// Read a users row.
        /// </summary>
        /// <param name="record">Reader positioned on a users row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The user.</returns>
        internal static UserMaster Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "User");
            UserMaster user = new UserMaster();
            user.Id = row.Text("id");
            user.TenantId = row.Text("tenant_id");
            user.Email = row.Text("email");
            user.PasswordSha256 = row.Text("password_sha256");
            user.FirstName = row.NullableText("first_name");
            user.LastName = row.NullableText("last_name");
            user.IsAdmin = row.Bool("is_admin");
            user.IsTenantAdmin = row.Bool("is_tenant_admin");
            user.IsProtected = row.Bool("is_protected");
            user.Active = row.Bool("active");
            user.CreatedUtc = row.Utc("created_utc");
            user.LastUpdateUtc = row.Utc("last_update_utc");
            return user;
        }

        /// <summary>
        /// Bind every stored users column, each in the form its provider stores it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the users table.</param>
        /// <param name="user">Row to bind.</param>
        internal static void Write(StoredParameters parameters, UserMaster user)
        {
            parameters
                .Text("id", user.Id)
                .Text("tenant_id", user.TenantId)
                .Text("email", user.Email)
                .Text("first_name", user.FirstName)
                .Text("last_name", user.LastName)
                .Bool("is_admin", user.IsAdmin)
                .Bool("is_tenant_admin", user.IsTenantAdmin)
                .Bool("is_protected", user.IsProtected)
                .Bool("active", user.Active)
                .Utc("created_utc", user.CreatedUtc)
                .Utc("last_update_utc", user.LastUpdateUtc);
        }
    }
}
