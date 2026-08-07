namespace Armada.Test.Shared.Suites.Models
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Models;
    using Armada.Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Armada.Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="Credential"/>: id/tenant/user/token validation, bearer-token
    /// generation and uniqueness, nullable name handling, and JSON serialization.
    /// </summary>
    public sealed class CredentialSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Credential model suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("default_constructor_generates_id_with_prefix", "Credential default constructor generates id with prefix", TestTags.Positive, () =>
            {
                Credential cred = new Credential();
                AssertStartsWith(Constants.CredentialIdPrefix, cred.Id);
            }));

            cases.Add(Case("default_constructor_has_default_values", "Credential default constructor has default values", TestTags.Positive, () =>
            {
                Credential cred = new Credential();
                AssertEqual(Constants.DefaultTenantId, cred.TenantId);
                AssertEqual(Constants.DefaultUserId, cred.UserId);
                AssertNull(cred.Name);
                AssertTrue(cred.Active);
            }));

            cases.Add(Case("default_constructor_generates_bearer_token", "Credential default constructor generates bearer token", TestTags.Positive, () =>
            {
                Credential cred = new Credential();
                AssertNotNull(cred.BearerToken);
                AssertEqual(64, cred.BearerToken.Length, "Bearer token should be 64 chars");
            }));

            cases.Add(Case("tenant_user_constructor_sets_properties", "Credential tenant/user constructor sets properties", TestTags.Positive, () =>
            {
                Credential cred = new Credential("ten_abc", "usr_xyz");
                AssertEqual("ten_abc", cred.TenantId);
                AssertEqual("usr_xyz", cred.UserId);
            }));

            cases.Add(Case("tenant_user_constructor_still_generates_token", "Credential tenant/user constructor still generates token", TestTags.Positive, () =>
            {
                Credential cred = new Credential("ten_abc", "usr_xyz");
                AssertNotNull(cred.BearerToken);
                AssertEqual(64, cred.BearerToken.Length);
            }));

            cases.Add(Case("generate_bearer_token_returns_64_char_alphanumeric", "GenerateBearerToken returns 64-char alphanumeric", TestTags.Positive, () =>
            {
                string token = Credential.GenerateBearerToken();
                AssertEqual(64, token.Length);

                foreach (char c in token)
                {
                    AssertTrue(char.IsLetterOrDigit(c), "Token char '" + c + "' should be alphanumeric");
                }
            }));

            cases.Add(Case("generate_bearer_token_unique_across_calls", "GenerateBearerToken unique across calls", TestTags.Positive, () =>
            {
                string t1 = Credential.GenerateBearerToken();
                string t2 = Credential.GenerateBearerToken();
                AssertNotEqual(t1, t2);
            }));

            cases.Add(Case("unique_tokens_across_instances", "Credential unique tokens across instances", TestTags.Positive, () =>
            {
                Credential c1 = new Credential();
                Credential c2 = new Credential();
                AssertNotEqual(c1.BearerToken, c2.BearerToken);
            }));

            cases.Add(Case("set_id_null_throws", "Credential set id null throws", TestTags.Negative, () =>
            {
                Credential cred = new Credential();
                AssertThrows<ArgumentNullException>(() => cred.Id = null!);
            }));

            cases.Add(Case("set_id_empty_throws", "Credential set id empty throws", TestTags.Negative, () =>
            {
                Credential cred = new Credential();
                AssertThrows<ArgumentNullException>(() => cred.Id = "");
            }));

            cases.Add(Case("set_tenant_id_null_throws", "Credential set tenant id null throws", TestTags.Negative, () =>
            {
                Credential cred = new Credential();
                AssertThrows<ArgumentNullException>(() => cred.TenantId = null!);
            }));

            cases.Add(Case("set_tenant_id_empty_throws", "Credential set tenant id empty throws", TestTags.Negative, () =>
            {
                Credential cred = new Credential();
                AssertThrows<ArgumentNullException>(() => cred.TenantId = "");
            }));

            cases.Add(Case("set_user_id_null_throws", "Credential set user id null throws", TestTags.Negative, () =>
            {
                Credential cred = new Credential();
                AssertThrows<ArgumentNullException>(() => cred.UserId = null!);
            }));

            cases.Add(Case("set_user_id_empty_throws", "Credential set user id empty throws", TestTags.Negative, () =>
            {
                Credential cred = new Credential();
                AssertThrows<ArgumentNullException>(() => cred.UserId = "");
            }));

            cases.Add(Case("set_bearer_token_null_throws", "Credential set bearer token null throws", TestTags.Negative, () =>
            {
                Credential cred = new Credential();
                AssertThrows<ArgumentNullException>(() => cred.BearerToken = null!);
            }));

            cases.Add(Case("set_bearer_token_empty_throws", "Credential set bearer token empty throws", TestTags.Negative, () =>
            {
                Credential cred = new Credential();
                AssertThrows<ArgumentNullException>(() => cred.BearerToken = "");
            }));

            cases.Add(Case("set_name_accepts_null", "Credential set name accepts null", TestTags.Positive, () =>
            {
                Credential cred = new Credential();
                cred.Name = "My API Key";
                AssertEqual("My API Key", cred.Name);
                cred.Name = null;
                AssertNull(cred.Name);
            }));

            cases.Add(Case("serialization_round_trip", "Credential serialization round trip", TestTags.Positive, () =>
            {
                Credential cred = new Credential("ten_test", "usr_test");
                cred.Name = "Test Credential";
                cred.Active = false;

                string json = JsonSerializer.Serialize(cred);
                Credential deserialized = JsonSerializer.Deserialize<Credential>(json)!;

                AssertEqual(cred.Id, deserialized.Id);
                AssertEqual(cred.TenantId, deserialized.TenantId);
                AssertEqual(cred.UserId, deserialized.UserId);
                AssertEqual(cred.BearerToken, deserialized.BearerToken);
                AssertEqual(cred.Name, deserialized.Name);
                AssertEqual(cred.Active, deserialized.Active);
            }));

            return new TestSuiteDescriptor(
                suiteId: "Models.Credential",
                displayName: "Credential Model",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Models.Credential",
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
                suiteId: "Models.Credential",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
