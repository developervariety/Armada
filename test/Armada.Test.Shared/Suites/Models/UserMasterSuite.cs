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
    /// Descriptors for <see cref="UserMaster"/>: identity and defaults, password hashing and
    /// verification, redaction, and setter validation. Ported from the retired unit suite plus
    /// added empty/null negatives for setters the legacy suite only covered on one edge.
    /// </summary>
    public sealed class UserMasterSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the UserMaster model suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("default_constructor_generates_id_with_prefix", "UserMaster default constructor generates id with prefix", TestTags.Positive, () =>
            {
                UserMaster user = new UserMaster();
                AssertStartsWith(Constants.UserIdPrefix, user.Id);
            }));

            cases.Add(Case("default_constructor_has_default_values", "UserMaster default constructor has default values", TestTags.Positive, () =>
            {
                UserMaster user = new UserMaster();
                AssertEqual(Constants.DefaultTenantId, user.TenantId);
                AssertEqual("admin@armada", user.Email);
                AssertFalse(user.IsAdmin);
                AssertTrue(user.Active);
                AssertNull(user.FirstName);
                AssertNull(user.LastName);
            }));

            cases.Add(Case("parameterized_constructor_sets_properties", "UserMaster parameterized constructor sets properties", TestTags.Positive, () =>
            {
                UserMaster user = new UserMaster("ten_abc", "alice@example.com", "secret123");
                AssertEqual("ten_abc", user.TenantId);
                AssertEqual("alice@example.com", user.Email);
                AssertNotEqual("secret123", user.PasswordSha256, "Password should be hashed, not stored plaintext");
            }));

            cases.Add(Case("parameterized_constructor_hashes_password", "UserMaster parameterized constructor hashes password", TestTags.Positive, () =>
            {
                UserMaster user = new UserMaster("ten_abc", "alice@example.com", "secret123");
                string expectedHash = UserMaster.ComputePasswordHash("secret123");
                AssertEqual(expectedHash, user.PasswordSha256);
            }));

            cases.Add(Case("compute_password_hash_returns_sha256_hex_lowercase", "ComputePasswordHash returns sha256 hex lowercase", TestTags.Positive, () =>
            {
                string hash = UserMaster.ComputePasswordHash("password");
                AssertEqual(64, hash.Length, "SHA256 hex should be 64 chars");
                AssertEqual(hash, hash.ToLowerInvariant(), "Should be lowercase");
            }));

            cases.Add(Case("compute_password_hash_deterministic_for_same_input", "ComputePasswordHash deterministic for same input", TestTags.Positive, () =>
            {
                string hash1 = UserMaster.ComputePasswordHash("mypassword");
                string hash2 = UserMaster.ComputePasswordHash("mypassword");
                AssertEqual(hash1, hash2);
            }));

            cases.Add(Case("compute_password_hash_differs_for_different_input", "ComputePasswordHash differs for different input", TestTags.Positive, () =>
            {
                string hash1 = UserMaster.ComputePasswordHash("password1");
                string hash2 = UserMaster.ComputePasswordHash("password2");
                AssertNotEqual(hash1, hash2);
            }));

            cases.Add(Case("compute_password_hash_null_throws", "ComputePasswordHash null throws", TestTags.Negative, () =>
            {
                AssertThrows<ArgumentNullException>(() => UserMaster.ComputePasswordHash(null!));
            }));

            cases.Add(Case("compute_password_hash_empty_throws", "ComputePasswordHash empty throws", TestTags.Negative, () =>
            {
                AssertThrows<ArgumentNullException>(() => UserMaster.ComputePasswordHash(""));
            }));

            cases.Add(Case("verify_password_correct_password_returns_true", "VerifyPassword correct password returns true", TestTags.Positive, () =>
            {
                UserMaster user = new UserMaster("ten_abc", "alice@example.com", "correct-password");
                AssertTrue(user.VerifyPassword("correct-password"));
            }));

            cases.Add(Case("verify_password_wrong_password_returns_false", "VerifyPassword wrong password returns false", TestTags.Negative, () =>
            {
                UserMaster user = new UserMaster("ten_abc", "alice@example.com", "correct-password");
                AssertFalse(user.VerifyPassword("wrong-password"));
            }));

            cases.Add(Case("verify_password_null_returns_false", "VerifyPassword null returns false", TestTags.Negative, () =>
            {
                UserMaster user = new UserMaster("ten_abc", "alice@example.com", "correct-password");
                AssertFalse(user.VerifyPassword(null!));
            }));

            cases.Add(Case("verify_password_empty_returns_false", "VerifyPassword empty returns false", TestTags.Negative, () =>
            {
                UserMaster user = new UserMaster("ten_abc", "alice@example.com", "correct-password");
                AssertFalse(user.VerifyPassword(""));
            }));

            cases.Add(Case("redact_replaces_password_with_stars", "Redact replaces password with stars", TestTags.Positive, () =>
            {
                UserMaster user = new UserMaster("ten_abc", "alice@example.com", "secret");
                UserMaster redacted = UserMaster.Redact(user);
                AssertEqual("********", redacted.PasswordSha256);
            }));

            cases.Add(Case("redact_preserves_other_fields", "Redact preserves other fields", TestTags.Positive, () =>
            {
                UserMaster user = new UserMaster("ten_abc", "alice@example.com", "secret");
                user.FirstName = "Alice";
                user.LastName = "Smith";
                user.IsAdmin = true;
                user.Active = false;

                UserMaster redacted = UserMaster.Redact(user);
                AssertEqual(user.Id, redacted.Id);
                AssertEqual(user.TenantId, redacted.TenantId);
                AssertEqual(user.Email, redacted.Email);
                AssertEqual("Alice", redacted.FirstName);
                AssertEqual("Smith", redacted.LastName);
                AssertTrue(redacted.IsAdmin);
                AssertFalse(redacted.Active);
                AssertEqual(user.CreatedUtc, redacted.CreatedUtc);
                AssertEqual(user.LastUpdateUtc, redacted.LastUpdateUtc);
            }));

            cases.Add(Case("redact_null_user_throws", "Redact null user throws", TestTags.Negative, () =>
            {
                AssertThrows<ArgumentNullException>(() => UserMaster.Redact(null!));
            }));

            cases.Add(Case("set_id_null_throws", "UserMaster set id null throws", TestTags.Negative, () =>
            {
                UserMaster user = new UserMaster();
                AssertThrows<ArgumentNullException>(() => user.Id = null!);
            }));

            cases.Add(Case("set_tenant_id_null_throws", "UserMaster set tenant id null throws", TestTags.Negative, () =>
            {
                UserMaster user = new UserMaster();
                AssertThrows<ArgumentNullException>(() => user.TenantId = null!);
            }));

            cases.Add(Case("set_email_null_throws", "UserMaster set email null throws", TestTags.Negative, () =>
            {
                UserMaster user = new UserMaster();
                AssertThrows<ArgumentNullException>(() => user.Email = null!);
            }));

            cases.Add(Case("set_password_sha256_empty_throws", "UserMaster set password sha256 empty throws", TestTags.Negative, () =>
            {
                UserMaster user = new UserMaster();
                AssertThrows<ArgumentNullException>(() => user.PasswordSha256 = "");
            }));

            // Added audit coverage: complete the empty/null edges the legacy suite only partially exercised.
            cases.Add(Case("set_id_empty_throws", "UserMaster set id empty throws", TestTags.Negative, () =>
            {
                UserMaster user = new UserMaster();
                AssertThrows<ArgumentNullException>(() => user.Id = "");
            }));

            cases.Add(Case("set_tenant_id_empty_throws", "UserMaster set tenant id empty throws", TestTags.Negative, () =>
            {
                UserMaster user = new UserMaster();
                AssertThrows<ArgumentNullException>(() => user.TenantId = "");
            }));

            cases.Add(Case("set_email_empty_throws", "UserMaster set email empty throws", TestTags.Negative, () =>
            {
                UserMaster user = new UserMaster();
                AssertThrows<ArgumentNullException>(() => user.Email = "");
            }));

            cases.Add(Case("set_password_sha256_null_throws", "UserMaster set password sha256 null throws", TestTags.Negative, () =>
            {
                UserMaster user = new UserMaster();
                AssertThrows<ArgumentNullException>(() => user.PasswordSha256 = null!);
            }));

            cases.Add(Case("serialization_round_trip", "UserMaster serialization round trip", TestTags.Positive, () =>
            {
                UserMaster user = new UserMaster("ten_test", "bob@example.com", "pass123");
                user.FirstName = "Bob";
                user.LastName = "Jones";
                user.IsAdmin = true;

                string json = JsonSerializer.Serialize(user);
                UserMaster deserialized = JsonSerializer.Deserialize<UserMaster>(json)!;

                AssertEqual(user.Id, deserialized.Id);
                AssertEqual(user.TenantId, deserialized.TenantId);
                AssertEqual(user.Email, deserialized.Email);
                AssertEqual(user.PasswordSha256, deserialized.PasswordSha256);
                AssertEqual(user.FirstName, deserialized.FirstName);
                AssertEqual(user.LastName, deserialized.LastName);
                AssertEqual(user.IsAdmin, deserialized.IsAdmin);
            }));

            return new TestSuiteDescriptor(
                suiteId: "Models.UserMaster",
                displayName: "UserMaster Model",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Models.UserMaster",
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
                suiteId: "Models.UserMaster",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
