namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="AuthorizationService"/>: request authorization against the
    /// permission matrix and the Require* guard methods. Positive cases assert that valid
    /// identities are admitted; negative cases assert rejection (false / thrown
    /// <see cref="UnauthorizedAccessException"/>) for missing or insufficient privileges,
    /// including tenant-admin and null-context paths the legacy suite skipped.
    /// </summary>
    public sealed class AuthorizationServiceSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the AuthorizationService suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            // --- IsAuthorized tests ---

            cases.Add(Case("is_authorized_no_auth_required_unauthenticated_returns_true", "IsAuthorized NoAuthRequired Endpoint Unauthenticated ReturnsTrue", TestTags.Positive, () =>
            {
                AuthorizationService svc = new AuthorizationService();
                AuthContext ctx = new AuthContext();

                bool result = svc.IsAuthorized(ctx, "GET", "/api/v1/status/health");
                AssertTrue(result, "Unauthenticated request to NoAuthRequired endpoint should be authorized");
            }));

            cases.Add(Case("is_authorized_no_auth_required_authenticated_returns_true", "IsAuthorized NoAuthRequired Endpoint Authenticated ReturnsTrue", TestTags.Positive, () =>
            {
                AuthorizationService svc = new AuthorizationService();
                AuthContext ctx = AuthContext.Authenticated("ten_abc", "usr_xyz", false, false, "Session");

                bool result = svc.IsAuthorized(ctx, "GET", "/api/v1/status/health");
                AssertTrue(result, "Authenticated request to NoAuthRequired endpoint should be authorized");
            }));

            cases.Add(Case("is_authorized_authenticated_endpoint_authenticated_returns_true", "IsAuthorized Authenticated Endpoint Authenticated ReturnsTrue", TestTags.Positive, () =>
            {
                AuthorizationService svc = new AuthorizationService();
                AuthContext ctx = AuthContext.Authenticated("ten_abc", "usr_xyz", false, false, "Session");

                bool result = svc.IsAuthorized(ctx, "GET", "/api/v1/fleets");
                AssertTrue(result, "Authenticated request to Authenticated endpoint should be authorized");
            }));

            cases.Add(Case("is_authorized_authenticated_endpoint_unauthenticated_returns_false", "IsAuthorized Authenticated Endpoint Unauthenticated ReturnsFalse", TestTags.Negative, () =>
            {
                AuthorizationService svc = new AuthorizationService();
                AuthContext ctx = new AuthContext();

                bool result = svc.IsAuthorized(ctx, "GET", "/api/v1/fleets");
                AssertFalse(result, "Unauthenticated request to Authenticated endpoint should not be authorized");
            }));

            cases.Add(Case("is_authorized_admin_only_admin_returns_true", "IsAuthorized AdminOnly Endpoint Admin ReturnsTrue", TestTags.Positive, () =>
            {
                AuthorizationService svc = new AuthorizationService();
                AuthContext ctx = AuthContext.Authenticated("ten_abc", "usr_xyz", true, true, "Session");

                bool result = svc.IsAuthorized(ctx, "POST", "/api/v1/tenants");
                AssertTrue(result, "Admin request to AdminOnly endpoint should be authorized");
            }));

            cases.Add(Case("is_authorized_admin_only_non_admin_returns_false", "IsAuthorized AdminOnly Endpoint NonAdmin ReturnsFalse", TestTags.Negative, () =>
            {
                AuthorizationService svc = new AuthorizationService();
                AuthContext ctx = AuthContext.Authenticated("ten_abc", "usr_xyz", false, false, "Session");

                bool result = svc.IsAuthorized(ctx, "POST", "/api/v1/tenants");
                AssertFalse(result, "Non-admin request to AdminOnly endpoint should not be authorized");
            }));

            cases.Add(Case("is_authorized_admin_only_unauthenticated_returns_false", "IsAuthorized AdminOnly Endpoint Unauthenticated ReturnsFalse", TestTags.Negative, () =>
            {
                AuthorizationService svc = new AuthorizationService();
                AuthContext ctx = new AuthContext();

                bool result = svc.IsAuthorized(ctx, "POST", "/api/v1/tenants");
                AssertFalse(result, "Unauthenticated request to AdminOnly endpoint should not be authorized");
            }));

            // --- RequireAuth tests ---

            cases.Add(Case("require_auth_authenticated_does_not_throw", "RequireAuth Authenticated DoesNotThrow", TestTags.Positive, () =>
            {
                AuthorizationService svc = new AuthorizationService();
                AuthContext ctx = AuthContext.Authenticated("ten_abc", "usr_xyz", false, false, "Session");

                svc.RequireAuth(ctx);
            }));

            cases.Add(Case("require_auth_unauthenticated_throws", "RequireAuth Unauthenticated ThrowsUnauthorizedAccessException", TestTags.Negative, () =>
            {
                AuthorizationService svc = new AuthorizationService();
                AuthContext ctx = new AuthContext();

                AssertThrows<UnauthorizedAccessException>(() => svc.RequireAuth(ctx));
            }));

            // --- RequireAdmin tests ---

            cases.Add(Case("require_admin_admin_does_not_throw", "RequireAdmin Admin DoesNotThrow", TestTags.Positive, () =>
            {
                AuthorizationService svc = new AuthorizationService();
                AuthContext ctx = AuthContext.Authenticated("ten_abc", "usr_xyz", true, true, "Session");

                svc.RequireAdmin(ctx);
            }));

            cases.Add(Case("require_admin_non_admin_throws", "RequireAdmin NonAdmin ThrowsUnauthorizedAccessException", TestTags.Negative, () =>
            {
                AuthorizationService svc = new AuthorizationService();
                AuthContext ctx = AuthContext.Authenticated("ten_abc", "usr_xyz", false, false, "Session");

                AssertThrows<UnauthorizedAccessException>(() => svc.RequireAdmin(ctx));
            }));

            cases.Add(Case("require_admin_unauthenticated_throws", "RequireAdmin Unauthenticated ThrowsUnauthorizedAccessException", TestTags.Negative, () =>
            {
                AuthorizationService svc = new AuthorizationService();
                AuthContext ctx = new AuthContext();

                AssertThrows<UnauthorizedAccessException>(() => svc.RequireAdmin(ctx));
            }));

            cases.Add(Case("is_authorized_tenant_admin_endpoint_tenant_admin_returns_true", "IsAuthorized TenantAdmin Endpoint TenantAdmin ReturnsTrue", TestTags.Positive, () =>
            {
                AuthorizationService svc = new AuthorizationService();
                AuthContext ctx = AuthContext.Authenticated("ten_abc", "usr_xyz", false, true, "Session");

                bool result = svc.IsAuthorized(ctx, "POST", "/api/v1/fleets");
                AssertTrue(result, "Tenant admin request to TenantAdmin endpoint should be authorized");
            }));

            cases.Add(Case("is_authorized_tenant_admin_endpoint_regular_user_returns_false", "IsAuthorized TenantAdmin Endpoint RegularUser ReturnsFalse", TestTags.Negative, () =>
            {
                AuthorizationService svc = new AuthorizationService();
                AuthContext ctx = AuthContext.Authenticated("ten_abc", "usr_xyz", false, false, "Session");

                bool result = svc.IsAuthorized(ctx, "POST", "/api/v1/fleets");
                AssertFalse(result, "Regular user request to TenantAdmin endpoint should not be authorized");
            }));

            cases.Add(Case("require_tenant_admin_tenant_admin_does_not_throw", "RequireTenantAdmin TenantAdmin DoesNotThrow", TestTags.Positive, () =>
            {
                AuthorizationService svc = new AuthorizationService();
                AuthContext ctx = AuthContext.Authenticated("ten_abc", "usr_xyz", false, true, "Session");

                svc.RequireTenantAdmin(ctx);
            }));

            // --- Audit additions (confirmed against AuthorizationService source) ---

            cases.Add(Case("is_authorized_tenant_admin_endpoint_global_admin_returns_true", "IsAuthorized TenantAdmin Endpoint GlobalAdmin ReturnsTrue", TestTags.Positive, () =>
            {
                // A global admin satisfies TenantAdmin routes via the IsAdmin branch.
                AuthorizationService svc = new AuthorizationService();
                AuthContext ctx = AuthContext.Authenticated("ten_abc", "usr_xyz", true, false, "Session");

                bool result = svc.IsAuthorized(ctx, "POST", "/api/v1/fleets");
                AssertTrue(result, "Global admin request to TenantAdmin endpoint should be authorized");
            }));

            cases.Add(Case("require_tenant_admin_global_admin_does_not_throw", "RequireTenantAdmin GlobalAdmin DoesNotThrow", TestTags.Positive, () =>
            {
                // RequireTenantAdmin admits a global admin (IsAdmin) as well as a tenant admin.
                AuthorizationService svc = new AuthorizationService();
                AuthContext ctx = AuthContext.Authenticated("ten_abc", "usr_xyz", true, false, "Session");

                svc.RequireTenantAdmin(ctx);
            }));

            cases.Add(Case("require_tenant_admin_regular_user_throws", "RequireTenantAdmin RegularUser ThrowsUnauthorizedAccessException", TestTags.Negative, () =>
            {
                AuthorizationService svc = new AuthorizationService();
                AuthContext ctx = AuthContext.Authenticated("ten_abc", "usr_xyz", false, false, "Session");

                AssertThrows<UnauthorizedAccessException>(() => svc.RequireTenantAdmin(ctx));
            }));

            cases.Add(Case("require_tenant_admin_unauthenticated_throws", "RequireTenantAdmin Unauthenticated ThrowsUnauthorizedAccessException", TestTags.Negative, () =>
            {
                AuthorizationService svc = new AuthorizationService();
                AuthContext ctx = new AuthContext();

                AssertThrows<UnauthorizedAccessException>(() => svc.RequireTenantAdmin(ctx));
            }));

            cases.Add(Case("require_auth_null_context_throws", "RequireAuth NullContext ThrowsUnauthorizedAccessException", TestTags.Negative, () =>
            {
                // RequireAuth guards against a null context and rejects it.
                AuthorizationService svc = new AuthorizationService();
                AssertThrows<UnauthorizedAccessException>(() => svc.RequireAuth(null!));
            }));

            return new TestSuiteDescriptor(
                suiteId: "Services.AuthorizationService",
                displayName: "AuthorizationService",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.AuthorizationService",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag });
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.AuthorizationService",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
