namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Harbor;

    /// <summary>
    /// The harbor_runner_enrollments row-to-model contract, shared by every provider. Columns are read by name, so a
    /// select list in another order cannot shift one value into another property.
    /// </summary>
    internal static class HarborRunnerEnrollmentColumns
    {
        /// <summary>
        /// Read a harbor_runner_enrollments row.
        /// </summary>
        /// <param name="record">Reader positioned on a harbor_runner_enrollments row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The enrollment.</returns>
        internal static HarborRunnerEnrollment Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "HarborRunnerEnrollment");
            return new HarborRunnerEnrollment
            {
                RunnerId = row.Text("runner_id"),
                TenantId = row.Text("tenant_id"),
                UserId = row.Text("user_id"),
                AuthMethod = row.Text("auth_method"),
                CredentialId = row.TextOrNull("credential_id"),
                Generation = row.Long("generation"),
                Active = row.Bool("active"),
                CreatedUtc = row.Utc("created_utc"),
                LastUpdateUtc = row.Utc("last_update_utc"),
                RevokedUtc = row.NullableUtc("revoked_utc"),
                RevokedByUserId = row.TextOrNull("revoked_by_user_id")
            };
        }

        /// <summary>
        /// Bind the columns an enrollment writes, each in the form its provider stores it. The active flag and the revocation columns are set by the enrollment and revocation statements themselves.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the harbor_runner_enrollments table.</param>
        /// <param name="enrollment">Row to bind.</param>
        internal static void Write(StoredParameters parameters, HarborRunnerEnrollment enrollment)
        {
            parameters
                .Text("runner_id", enrollment.RunnerId)
                .Text("tenant_id", enrollment.TenantId)
                .Text("user_id", enrollment.UserId)
                .Text("auth_method", enrollment.AuthMethod)
                .Text("credential_id", enrollment.CredentialId)
                .Long("generation", enrollment.Generation)
                .Utc("created_utc", enrollment.CreatedUtc)
                .Utc("last_update_utc", enrollment.LastUpdateUtc);
        }
    }
}
