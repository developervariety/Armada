namespace Armada.Test.Shared.Suites.Models
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Armada.Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="AuthContext"/>: the unauthenticated default, the
    /// <see cref="AuthContext.Authenticated"/> factory across admin/non-admin and auth methods,
    /// and direct property assignment.
    /// </summary>
    public sealed class AuthContextSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the AuthContext model suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("default_constructor_is_unauthenticated", "AuthContext default constructor is unauthenticated", TestTags.Positive, () =>
            {
                AuthContext ctx = new AuthContext();
                AssertFalse(ctx.IsAuthenticated);
                AssertNull(ctx.TenantId);
                AssertNull(ctx.UserId);
                AssertFalse(ctx.IsAdmin);
                AssertFalse(ctx.IsTenantAdmin);
                AssertNull(ctx.AuthMethod);
            }));

            cases.Add(Case("authenticated_factory_sets_all_properties", "AuthContext Authenticated factory sets all properties", TestTags.Positive, () =>
            {
                AuthContext ctx = AuthContext.Authenticated("ten_abc", "usr_xyz", true, true, "Bearer");
                AssertTrue(ctx.IsAuthenticated);
                AssertEqual("ten_abc", ctx.TenantId);
                AssertEqual("usr_xyz", ctx.UserId);
                AssertTrue(ctx.IsAdmin);
                AssertTrue(ctx.IsTenantAdmin);
                AssertEqual("Bearer", ctx.AuthMethod);
            }));

            cases.Add(Case("authenticated_non_admin", "AuthContext Authenticated non-admin", TestTags.Positive, () =>
            {
                AuthContext ctx = AuthContext.Authenticated("ten_abc", "usr_xyz", false, false, "Session");
                AssertTrue(ctx.IsAuthenticated);
                AssertFalse(ctx.IsAdmin);
                AssertFalse(ctx.IsTenantAdmin);
                AssertEqual("Session", ctx.AuthMethod);
            }));

            cases.Add(Case("authenticated_different_auth_methods", "AuthContext Authenticated different auth methods", TestTags.Positive, () =>
            {
                AuthContext bearer = AuthContext.Authenticated("ten_1", "usr_1", false, false, "Bearer");
                AuthContext session = AuthContext.Authenticated("ten_1", "usr_1", false, false, "Session");
                AuthContext apiKey = AuthContext.Authenticated("ten_1", "usr_1", false, false, "ApiKey");

                AssertEqual("Bearer", bearer.AuthMethod);
                AssertEqual("Session", session.AuthMethod);
                AssertEqual("ApiKey", apiKey.AuthMethod);
            }));

            cases.Add(Case("properties_are_settable", "AuthContext properties are settable", TestTags.Positive, () =>
            {
                AuthContext ctx = new AuthContext();
                ctx.IsAuthenticated = true;
                ctx.TenantId = "ten_test";
                ctx.UserId = "usr_test";
                ctx.IsAdmin = true;
                ctx.IsTenantAdmin = true;
                ctx.AuthMethod = "Custom";

                AssertTrue(ctx.IsAuthenticated);
                AssertEqual("ten_test", ctx.TenantId);
                AssertEqual("usr_test", ctx.UserId);
                AssertTrue(ctx.IsAdmin);
                AssertTrue(ctx.IsTenantAdmin);
                AssertEqual("Custom", ctx.AuthMethod);
            }));

            return new TestSuiteDescriptor(
                suiteId: "Models.AuthContext",
                displayName: "AuthContext Model",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Models.AuthContext",
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
                suiteId: "Models.AuthContext",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
