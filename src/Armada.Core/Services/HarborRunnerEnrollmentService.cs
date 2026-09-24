namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    /// <summary>
    /// Manages durable Harbor runner enrollment and resolves its live owner. Harbor remains disabled
    /// until a transport explicitly enables the session registry with this resolver.
    /// </summary>
    public sealed class HarborRunnerEnrollmentService : IHarborRunnerOwnerResolver, IHarborRunnerOwnerChangeNotifier, IHarborRunnerAuthority
    {
        #region Private-Members

        private readonly IHarborRunnerEnrollmentMethods _Enrollments;
        private readonly ICredentialMethods _Credentials;
        private readonly IUserMethods _Users;
        private readonly ITenantMethods _Tenants;
        private readonly TimeSpan _OwnerLookupTimeout;
        private string _LastResolutionFailure = "not_resolved";

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate the enrollment service.</summary>
        /// <param name="database">Durable database driver.</param>
        /// <param name="ownerLookupTimeout">Bound on one owner lookup; null uses <see cref="DefaultOwnerLookupTimeout"/>.</param>
        public HarborRunnerEnrollmentService(DatabaseDriver database, TimeSpan? ownerLookupTimeout = null)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            _Enrollments = database.HarborRunnerEnrollments;
            _Credentials = database.Credentials;
            _Users = database.Users;
            _Tenants = database.Tenants;
            _OwnerLookupTimeout = ValidateTimeout(ownerLookupTimeout);
        }

        /// <summary>Instantiate with durable enrollment and credential stores.</summary>
        /// <param name="enrollments">Durable enrollment store.</param>
        /// <param name="credentials">Durable credential store.</param>
        /// <param name="users">Durable user store.</param>
        /// <param name="tenants">Durable tenant store.</param>
        /// <param name="ownerLookupTimeout">Bound on one owner lookup; null uses <see cref="DefaultOwnerLookupTimeout"/>.</param>
        public HarborRunnerEnrollmentService(
            IHarborRunnerEnrollmentMethods enrollments,
            ICredentialMethods credentials,
            IUserMethods users,
            ITenantMethods tenants,
            TimeSpan? ownerLookupTimeout = null)
        {
            _Enrollments = enrollments ?? throw new ArgumentNullException(nameof(enrollments));
            _Credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
            _Users = users ?? throw new ArgumentNullException(nameof(users));
            _Tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
            _OwnerLookupTimeout = ValidateTimeout(ownerLookupTimeout);
        }

        #endregion

        #region Public-Members

        /// <summary>Owner lookup bound used when none is configured.</summary>
        public static readonly TimeSpan DefaultOwnerLookupTimeout = TimeSpan.FromSeconds(5);

        /// <summary>Refusal reason when an owner lookup does not finish within <see cref="OwnerLookupTimeout"/>.</summary>
        public const string OwnerLookupTimeoutReason = "runner_owner_lookup_timeout";

        /// <summary>Refusal reason when an owner lookup fails with an error.</summary>
        public const string OwnerUnavailableReason = "runner_owner_unavailable";

        /// <summary>Bound on one owner lookup, covering every durable read it makes.</summary>
        public TimeSpan OwnerLookupTimeout
        {
            get { return _OwnerLookupTimeout; }
        }

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
            // A runner id is unique across the admiral. An enrollment held by another tenant is reported only as
            // taken, never as active or revoked, so its state is not disclosed to that tenant's administrators.
            if (current != null && (current.Active || IsOtherTenant(administrator, current.TenantId)))
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
        /// Enroll a runner for the principal behind an existing active credential. Owner identity fields
        /// are read from durable credential, user and tenant records; callers cannot supply them.
        /// </summary>
        /// <param name="runnerId">Runner identifier.</param>
        /// <param name="credentialId">Durable credential identifier.</param>
        /// <param name="administrator">Authenticated administrator performing the change.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The durable enrollment.</returns>
        public async Task<HarborRunnerEnrollment> CreateForCredentialAsync(
            string runnerId,
            string credentialId,
            AuthContext administrator,
            CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(credentialId)) throw new ArgumentException("Credential identifier is required.", nameof(credentialId));
            Credential? credential = await _Credentials.ReadByIdAsync(credentialId.Trim(), token).ConfigureAwait(false);
            // Another tenant's credential reads exactly like a missing one, so its existence is not disclosed.
            if (credential == null || !credential.Active || IsOtherTenant(administrator, credential.TenantId))
                throw new UnauthorizedAccessException("credential_revoked_or_mismatched");
            UserMaster? user = await _Users.ReadByIdAsync(credential.UserId, token).ConfigureAwait(false);
            TenantMetadata? tenant = await _Tenants.ReadAsync(credential.TenantId, token).ConfigureAwait(false);
            if (user == null || !user.Active || tenant == null || !tenant.Active
                || !String.Equals(user.TenantId, credential.TenantId, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("credential_principal_inactive");
            AuthContext owner = AuthContext.Authenticated(
                credential.TenantId,
                credential.UserId,
                user.IsAdmin,
                user.IsAdmin || user.IsTenantAdmin,
                "Bearer",
                credential.Id,
                user.Email);
            return await CreateAsync(runnerId, owner, administrator, token).ConfigureAwait(false);
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
            // Another tenant's enrollment reads exactly like a missing one.
            if (current == null || IsOtherTenant(administrator, current.TenantId)) return false;
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
        /// Resolve a runner's current owner and durable enrollment generation. Every call re-reads durable enrollment,
        /// the principal and the bound credential, so revocation is visible without a process restart or cache flush.
        /// The whole lookup is bounded by <see cref="OwnerLookupTimeout"/>: a lookup that does not finish in time is
        /// refused as <c>runner_owner_lookup_timeout</c>, and a lookup that fails is refused as
        /// <c>runner_owner_unavailable</c>. Cancelling <paramref name="token"/> throws.
        /// </summary>
        /// <param name="runnerId">Runner identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The resolved owner and generation, or the named failure.</returns>
        public async Task<HarborRunnerOwnerResolution> ResolveOwnerAsync(string runnerId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(runnerId)) return Fail("runner_id_invalid");

            using (CancellationTokenSource lookup = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                Task<HarborRunnerOwnerResolution> resolution = ResolveOwnerCoreAsync(runnerId.Trim(), lookup.Token);
                try
                {
                    return await resolution.WaitAsync(_OwnerLookupTimeout, token).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    lookup.Cancel();
                    ObserveAbandoned(resolution);
                    return Fail(OwnerLookupTimeoutReason);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    ObserveAbandoned(resolution);
                    throw;
                }
                catch (Exception)
                {
                    return Fail(OwnerUnavailableReason);
                }
            }
        }

        #endregion

        /// <inheritdoc />
        public async Task<bool> HasAuthorityOverOwnerAsync(AuthContext caller, string ownerTenantId, string ownerUserId, CancellationToken token = default)
        {
            if (caller == null || !caller.IsAuthenticated || String.IsNullOrWhiteSpace(caller.UserId)) return false;
            if (String.IsNullOrWhiteSpace(ownerTenantId) || String.IsNullOrWhiteSpace(ownerUserId)) return false;
            if (caller.IsAdmin) return true;
            if (!caller.IsTenantAdmin || !String.Equals(caller.TenantId, ownerTenantId, StringComparison.Ordinal)) return false;
            UserMaster? owner = await _Users.ReadAsync(ownerTenantId, ownerUserId, token).ConfigureAwait(false);
            return owner == null || !owner.IsAdmin;
        }

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

        private async Task ValidateAuthorityOverOwnerAsync(AuthContext administrator, string tenantId, string userId, CancellationToken token)
        {
            ValidateAdministratorIdentity(administrator);
            if (!await HasAuthorityOverOwnerAsync(administrator, tenantId, userId, token).ConfigureAwait(false))
                throw new UnauthorizedAccessException("administrator_not_authorized_for_owner");
        }

        private static bool IsOtherTenant(AuthContext? administrator, string? tenantId)
        {
            if (administrator == null || administrator.IsAdmin) return false;
            return !OwnershipPolicy.SameTenant(administrator.TenantId, tenantId);
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

        private async Task<HarborRunnerOwnerResolution> ResolveOwnerCoreAsync(string runnerId, CancellationToken token)
        {
            HarborRunnerEnrollment? enrollment = await _Enrollments.ReadAsync(runnerId, token).ConfigureAwait(false);
            if (enrollment == null) return Fail("runner_owner_unknown");
            if (!enrollment.Active) return Fail("runner_enrollment_revoked");
            if (enrollment.Generation <= 0) return Fail("runner_enrollment_generation_invalid");
            TenantMetadata? tenant = await _Tenants.ReadAsync(enrollment.TenantId, token).ConfigureAwait(false);
            UserMaster? user = await _Users.ReadAsync(enrollment.TenantId, enrollment.UserId, token).ConfigureAwait(false);
            if (tenant == null || !tenant.Active || user == null || !user.Active) return Fail("runner_principal_inactive");
            if (!String.IsNullOrWhiteSpace(enrollment.CredentialId))
            {
                Credential? credential = await _Credentials.ReadByIdAsync(enrollment.CredentialId, token).ConfigureAwait(false);
                if (credential == null || !credential.Active
                    || !String.Equals(credential.TenantId, enrollment.TenantId, StringComparison.Ordinal)
                    || !String.Equals(credential.UserId, enrollment.UserId, StringComparison.Ordinal)) return Fail("credential_revoked_or_mismatched");
            }
            else if (RequiresCredential(enrollment.AuthMethod)) return Fail("credential_binding_missing");

            AuthContext owner = AuthContext.Authenticated(
                enrollment.TenantId,
                enrollment.UserId,
                false,
                false,
                enrollment.AuthMethod,
                enrollment.CredentialId);
            SetResolutionFailure(String.Empty);
            return HarborRunnerOwnerResolution.Success(owner, enrollment.Generation);
        }

        private static void ObserveAbandoned(Task<HarborRunnerOwnerResolution> resolution)
        {
            // The caller has already been answered; observe the abandoned lookup's fault so it is not rethrown later.
            resolution.ContinueWith(
                abandoned => { _ = abandoned.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private HarborRunnerOwnerResolution Fail(string reason)
        {
            SetResolutionFailure(reason);
            return HarborRunnerOwnerResolution.Failure(reason);
        }

        private static TimeSpan ValidateTimeout(TimeSpan? ownerLookupTimeout)
        {
            TimeSpan value = ownerLookupTimeout ?? DefaultOwnerLookupTimeout;
            if (value <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ownerLookupTimeout), "The owner lookup timeout must be positive.");
            return value;
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
