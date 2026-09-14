namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    /// <summary>
    /// Manages durable Harbor runner enrollment and resolves its live owner. Harbor remains disabled
    /// until a transport explicitly enables the session registry with this resolver.
    /// </summary>
    public sealed class HarborRunnerEnrollmentService : IHarborRunnerOwnerResolver, IHarborRunnerOwnerGenerationResolver, IHarborRunnerOwnerChangeNotifier
    {
        #region Private-Members

        private readonly IHarborRunnerEnrollmentMethods _Enrollments;
        private readonly ICredentialMethods _Credentials;
        private readonly IUserMethods _Users;
        private readonly ITenantMethods _Tenants;
        private string _LastResolutionFailure = "not_resolved";

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate the enrollment service.</summary>
        /// <param name="database">Durable database driver.</param>
        public HarborRunnerEnrollmentService(DatabaseDriver database)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            _Enrollments = database.HarborRunnerEnrollments;
            _Credentials = database.Credentials;
            _Users = database.Users;
            _Tenants = database.Tenants;
        }

        /// <summary>Instantiate with durable enrollment and credential stores.</summary>
        /// <param name="enrollments">Durable enrollment store.</param>
        /// <param name="credentials">Durable credential store.</param>
        /// <param name="users">Durable user store.</param>
        /// <param name="tenants">Durable tenant store.</param>
        public HarborRunnerEnrollmentService(
            IHarborRunnerEnrollmentMethods enrollments,
            ICredentialMethods credentials,
            IUserMethods users,
            ITenantMethods tenants)
        {
            _Enrollments = enrollments ?? throw new ArgumentNullException(nameof(enrollments));
            _Credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
            _Users = users ?? throw new ArgumentNullException(nameof(users));
            _Tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
        }

        #endregion

        #region Public-Members

        /// <summary>Stable reason for the most recent failed owner resolution, or empty after success.</summary>
        public string LastResolutionFailure
        {
            get { return Volatile.Read(ref _LastResolutionFailure); }
        }

        /// <summary>Raised after a durable enrollment generation changes.</summary>
        public event Action<string, long>? OwnerChanged;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Enroll a runner for a verified owner. Only a global administrator or a tenant administrator
        /// for the owner's tenant can create an enrollment.
        /// </summary>
        /// <param name="runnerId">Runner identifier.</param>
        /// <param name="owner">Verified principal that will own the runner.</param>
        /// <param name="administrator">Authenticated administrator performing the change.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The durable enrollment.</returns>
        /// <exception cref="UnauthorizedAccessException">Thrown when either principal is not authorized.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the runner is already active or a concurrent change wins.</exception>
        public async Task<HarborRunnerEnrollment> CreateAsync(
            string runnerId,
            AuthContext owner,
            AuthContext administrator,
            CancellationToken token = default)
        {
            HarborRunnerIdentity identity = ValidateOwner(runnerId, owner);
            await ValidateAuthorityOverOwnerAsync(administrator, identity.TenantId, identity.UserId, token).ConfigureAwait(false);
            HarborRunnerEnrollment? current = await _Enrollments.ReadAsync(identity.RunnerId, token).ConfigureAwait(false);
            if (current != null && current.Active)
                throw new InvalidOperationException("runner_already_enrolled");
            if (current != null)
                await ValidateAuthorityOverOwnerAsync(administrator, current.TenantId, current.UserId, token).ConfigureAwait(false);
            await ValidateCredentialAsync(identity, token).ConfigureAwait(false);

            long expectedGeneration = current?.Generation ?? 0;
            if (expectedGeneration < 0 || expectedGeneration == Int64.MaxValue)
                throw new InvalidOperationException("runner_enrollment_generation_invalid");
            HarborRunnerEnrollment proposed = new HarborRunnerEnrollment
            {
                RunnerId = identity.RunnerId,
                TenantId = identity.TenantId,
                UserId = identity.UserId,
                AuthMethod = identity.AuthMethod,
                CredentialId = identity.CredentialId,
                Generation = expectedGeneration + 1,
                Active = true,
                CreatedUtc = current?.CreatedUtc ?? DateTime.UtcNow,
                LastUpdateUtc = DateTime.UtcNow
            };

            bool changed = await _Enrollments.TryEnrollAsync(proposed, expectedGeneration, token).ConfigureAwait(false);
            if (!changed)
                throw new InvalidOperationException("runner_enrollment_conflict");

            HarborRunnerEnrollment? accepted = await _Enrollments.ReadAsync(identity.RunnerId, token).ConfigureAwait(false);
            if (accepted == null || !accepted.Active || accepted.Generation != proposed.Generation)
                throw new InvalidOperationException("runner_enrollment_unavailable");
            OwnerChanged?.Invoke(accepted.RunnerId, accepted.Generation);
            return accepted;
        }

        /// <summary>
        /// Revoke a runner enrollment. Revocation is atomic and invalidates the current generation.
        /// </summary>
        /// <param name="runnerId">Runner identifier.</param>
        /// <param name="administrator">Authenticated administrator performing the change.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when an active enrollment was revoked.</returns>
        public async Task<bool> RevokeAsync(string runnerId, AuthContext administrator, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(runnerId)) throw new ArgumentException("Runner identifier is required.", nameof(runnerId));
            ValidateAdministratorIdentity(administrator);
            HarborRunnerEnrollment? current = await _Enrollments.ReadAsync(runnerId.Trim(), token).ConfigureAwait(false);
            if (current == null) return false;
            await ValidateAuthorityOverOwnerAsync(administrator, current.TenantId, current.UserId, token).ConfigureAwait(false);
            if (!current.Active) return false;
            if (current.Generation <= 0 || current.Generation == Int64.MaxValue)
                throw new InvalidOperationException("runner_enrollment_generation_invalid");

            bool revoked = await _Enrollments.TryRevokeAsync(
                current.RunnerId,
                current.Generation,
                administrator.UserId!.Trim(),
                DateTime.UtcNow,
                token).ConfigureAwait(false);
            if (revoked) OwnerChanged?.Invoke(current.RunnerId, current.Generation + 1);
            return revoked;
        }

        /// <summary>
        /// Resolve a runner's current owner. This method re-reads durable enrollment and the bound
        /// credential on every call, so revocation is visible without a process restart or cache flush.
        /// </summary>
        /// <param name="runnerId">Runner identifier.</param>
        /// <param name="owner">Current owner context when valid.</param>
        /// <returns>True only when the runner and its credential are active.</returns>
        public bool TryGetOwner(string runnerId, out AuthContext? owner)
        {
            return TryGetOwner(runnerId, out owner, out _);
        }

        /// <summary>Resolve an owner and its current durable enrollment generation.</summary>
        /// <param name="runnerId">Runner identifier.</param>
        /// <param name="owner">Current owner context when valid.</param>
        /// <param name="generation">Durable enrollment generation when valid.</param>
        /// <returns>True only when the runner and its binding are active.</returns>
        public bool TryGetOwner(string runnerId, out AuthContext? owner, out long generation)
        {
            owner = null;
            generation = 0;
            if (String.IsNullOrWhiteSpace(runnerId)) return Fail("runner_id_invalid");

            HarborRunnerEnrollment? enrollment;
            using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            try
            {
                enrollment = _Enrollments.ReadAsync(runnerId.Trim(), timeout.Token)
                    .ConfigureAwait(false).GetAwaiter().GetResult();
                if (enrollment == null) return Fail("runner_owner_unknown");
                if (!enrollment.Active) return Fail("runner_enrollment_revoked");
                if (enrollment.Generation <= 0) return Fail("runner_enrollment_generation_invalid");
                TenantMetadata? tenant = _Tenants.ReadAsync(enrollment.TenantId, timeout.Token).ConfigureAwait(false).GetAwaiter().GetResult();
                UserMaster? user = _Users.ReadAsync(enrollment.TenantId, enrollment.UserId, timeout.Token).ConfigureAwait(false).GetAwaiter().GetResult();
                if (tenant == null || !tenant.Active || user == null || !user.Active) return Fail("runner_principal_inactive");
                if (!String.IsNullOrWhiteSpace(enrollment.CredentialId))
                {
                    Credential? credential = _Credentials.ReadByIdAsync(
                        enrollment.CredentialId,
                        timeout.Token).ConfigureAwait(false).GetAwaiter().GetResult();
                    if (credential == null || !credential.Active
                        || !String.Equals(credential.TenantId, enrollment.TenantId, StringComparison.Ordinal)
                        || !String.Equals(credential.UserId, enrollment.UserId, StringComparison.Ordinal)) return Fail("credential_revoked_or_mismatched");
                }
                else if (RequiresCredential(enrollment.AuthMethod)) return Fail("credential_binding_missing");

                owner = AuthContext.Authenticated(
                    enrollment.TenantId,
                    enrollment.UserId,
                    false,
                    false,
                    enrollment.AuthMethod,
                    enrollment.CredentialId);
                generation = enrollment.Generation;
                SetResolutionFailure(String.Empty);
                return true;
            }
            catch
            {
                owner = null;
                return Fail("runner_owner_unavailable");
            }
        }

        #endregion

        #region Private-Methods

        private static HarborRunnerIdentity ValidateOwner(string runnerId, AuthContext owner)
        {
            if (owner == null) throw new UnauthorizedAccessException("runner_identity_unverified");
            try
            {
                return HarborRunnerIdentity.FromVerifiedAuthContext(runnerId, owner);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new UnauthorizedAccessException("runner_identity_unverified", exception);
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException("runner_id_invalid", nameof(runnerId), exception);
            }
        }

        private static void ValidateAdministrator(AuthContext administrator, string tenantId)
        {
            ValidateAdministratorIdentity(administrator);
            if (administrator.IsAdmin) return;
            if (!administrator.IsTenantAdmin || !String.Equals(administrator.TenantId, tenantId, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("administrator_not_authorized");
        }

        /// <summary>
        /// One authority rule for every enrollment change: a global administrator may change any runner; a
        /// tenant administrator may change only runners whose owner is in the administrator's tenant and is not
        /// a global administrator. The rule applies to the new owner and to the previous owner of a revoked
        /// runner identifier.
        /// </summary>
        private async Task ValidateAuthorityOverOwnerAsync(AuthContext administrator, string tenantId, string userId, CancellationToken token)
        {
            ValidateAdministrator(administrator, tenantId);
            if (administrator.IsAdmin) return;
            UserMaster? owner = await _Users.ReadAsync(tenantId, userId, token).ConfigureAwait(false);
            if (owner != null && owner.IsAdmin)
                throw new UnauthorizedAccessException("administrator_not_authorized_for_owner");
        }

        private static void ValidateAdministratorIdentity(AuthContext administrator)
        {
            if (administrator == null || !administrator.IsAuthenticated || String.IsNullOrWhiteSpace(administrator.UserId))
                throw new UnauthorizedAccessException("administrator_identity_unverified");
        }

        private async Task ValidateCredentialAsync(HarborRunnerIdentity identity, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(identity.CredentialId))
            {
                if (RequiresCredential(identity.AuthMethod)) throw new UnauthorizedAccessException("credential_binding_missing");
                await ValidatePrincipalAsync(identity, token).ConfigureAwait(false);
                return;
            }
            Credential? credential = await _Credentials.ReadByIdAsync(identity.CredentialId, token).ConfigureAwait(false);
            TenantMetadata? tenant = await _Tenants.ReadAsync(identity.TenantId, token).ConfigureAwait(false);
            UserMaster? user = await _Users.ReadAsync(identity.TenantId, identity.UserId, token).ConfigureAwait(false);
            if (tenant == null || !tenant.Active || user == null || !user.Active
                || credential == null || !credential.Active
                || !String.Equals(credential.TenantId, identity.TenantId, StringComparison.Ordinal)
                || !String.Equals(credential.UserId, identity.UserId, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("credential_unverified_or_revoked");
        }

        private async Task ValidatePrincipalAsync(HarborRunnerIdentity identity, CancellationToken token)
        {
            TenantMetadata? tenant = await _Tenants.ReadAsync(identity.TenantId, token).ConfigureAwait(false);
            UserMaster? user = await _Users.ReadAsync(identity.TenantId, identity.UserId, token).ConfigureAwait(false);
            if (tenant == null || !tenant.Active || user == null || !user.Active)
                throw new UnauthorizedAccessException("principal_inactive");
        }

        private bool Fail(string reason)
        {
            SetResolutionFailure(reason);
            return false;
        }

        private void SetResolutionFailure(string reason)
        {
            Interlocked.Exchange(ref _LastResolutionFailure, reason);
        }

        private static bool RequiresCredential(string authMethod)
        {
            return String.Equals(authMethod, "Bearer", StringComparison.OrdinalIgnoreCase)
                || String.Equals(authMethod, "ApiKey", StringComparison.OrdinalIgnoreCase);
        }

        #endregion
    }
}
