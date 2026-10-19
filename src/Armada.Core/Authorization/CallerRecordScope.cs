namespace Armada.Core.Authorization
{
    using Armada.Core.Enums;

    /// <summary>
    /// The records one caller may read, as <see cref="CallerScopedRead"/> resolves it: every record, the
    /// records of one tenant, or the records one user owns in one tenant.
    /// </summary>
    public sealed class CallerRecordScope
    {
        #region Public-Members

        /// <summary>
        /// How far the scope reaches.
        /// </summary>
        public CallerRecordScopeKindEnum Kind { get; }

        /// <summary>
        /// Tenant the scope is limited to, or null for a global or empty scope.
        /// </summary>
        public string? TenantId { get; }

        /// <summary>
        /// Owner the scope is limited to, or null unless <see cref="Kind"/> is <see cref="CallerRecordScopeKindEnum.User"/>.
        /// </summary>
        public string? UserId { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="kind">How far the scope reaches.</param>
        /// <param name="tenantId">Tenant the scope is limited to.</param>
        /// <param name="userId">Owner the scope is limited to.</param>
        public CallerRecordScope(CallerRecordScopeKindEnum kind, string? tenantId, string? userId)
        {
            Kind = kind;
            TenantId = tenantId;
            UserId = userId;
        }

        #endregion
    }
}
