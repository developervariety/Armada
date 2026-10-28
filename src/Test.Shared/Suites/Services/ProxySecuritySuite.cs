namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Proxy;
    using Armada.Proxy.Services;
    using Armada.Proxy.Settings;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for the proxy's authentication hardening that sits below the HTTP surface: failed-login lockouts
    /// (window, lockout length, persistence across a restart, bounded stores) and the startup refusal of the built-in
    /// shared password. Time moves through an injected clock, never a sleep.
    /// </summary>
    public sealed class ProxySecuritySuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.ProxySecurity";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Proxy Security suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("login_limiter_locks_a_client_after_repeated_failures_until_the_lockout_ends", "LoginLimiter LocksAClientAfterRepeatedFailuresUntilTheLockoutEnds", TestTags.Negative, () =>
            {
                DateTime nowUtc = new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc);
                ProxySettings settings = new ProxySettings { LoginMaxFailures = 3, LoginLockoutSeconds = 60, LoginFailureWindowSeconds = 30 };
                ProxyLoginRateLimiter limiter = new ProxyLoginRateLimiter(settings, utcNow: () => nowUtc);

                AssertFalse(limiter.RecordFailure("10.0.0.1", out int _), "First failure does not lock");
                AssertFalse(limiter.RecordFailure("10.0.0.1", out int _), "Second failure does not lock");
                AssertTrue(limiter.RecordFailure("10.0.0.1", out int lockoutSeconds), "The third failure starts the lockout");
                AssertEqual(60, lockoutSeconds, "Lockout length comes from the setting");
                AssertTrue(limiter.IsLockedOut("10.0.0.1", out int retryAfter), "The client is locked out");
                AssertEqual(60, retryAfter, "Retry-After counts down from the lockout length");
                AssertFalse(limiter.IsLockedOut("10.0.0.2", out int _), "Another client is not locked out");

                nowUtc = nowUtc.AddSeconds(40);
                AssertTrue(limiter.IsLockedOut("10.0.0.1", out int remaining), "Still locked partway through");
                AssertEqual(20, remaining, "Retry-After shrinks with the clock");

                nowUtc = nowUtc.AddSeconds(21);
                AssertFalse(limiter.IsLockedOut("10.0.0.1", out int _), "The lockout ends when the clock passes it");
            }));

            cases.Add(Case("login_limiter_counts_failures_inside_the_window_and_clears_on_success", "LoginLimiter CountsFailuresInsideTheWindowAndClearsOnSuccess", TestTags.Positive, () =>
            {
                DateTime nowUtc = new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc);
                ProxySettings settings = new ProxySettings { LoginMaxFailures = 3, LoginFailureWindowSeconds = 30 };
                ProxyLoginRateLimiter limiter = new ProxyLoginRateLimiter(settings, utcNow: () => nowUtc);

                limiter.RecordFailure("10.0.0.1", out int _);
                limiter.RecordFailure("10.0.0.1", out int _);
                nowUtc = nowUtc.AddSeconds(31);
                AssertFalse(limiter.RecordFailure("10.0.0.1", out int _), "Failures older than the window are not counted");

                limiter.RecordFailure("10.0.0.1", out int _);
                limiter.RecordSuccess("10.0.0.1");
                AssertFalse(limiter.RecordFailure("10.0.0.1", out int _), "A successful login clears the failure history");
                AssertFalse(limiter.IsLockedOut("10.0.0.1", out int _), "Cleared client is not locked out");
            }));

            cases.Add(Case("login_limiter_restores_unexpired_lockouts_after_a_restart", "LoginLimiter RestoresUnexpiredLockoutsAfterARestart", TestTags.Positive, () =>
            {
                string directory = TestTemp.NewDirectory("proxy-lockouts");
                string statePath = ProxyLoginRateLimiter.DefaultStatePath(directory);
                DateTime nowUtc = new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc);
                ProxySettings settings = new ProxySettings { LoginMaxFailures = 2, LoginLockoutSeconds = 600 };

                ProxyLoginRateLimiter first = new ProxyLoginRateLimiter(settings, statePath, () => nowUtc);
                first.RecordFailure("10.0.0.1", out int _);
                AssertTrue(first.RecordFailure("10.0.0.1", out int _), "The second failure locks the client");
                first.RecordFailure("10.0.0.2", out int _);

                ProxyLoginRateLimiter restarted = new ProxyLoginRateLimiter(settings, statePath, () => nowUtc.AddSeconds(100));
                AssertTrue(restarted.IsLockedOut("10.0.0.1", out int retryAfter), "A restart does not lift an active lockout");
                AssertEqual(500, retryAfter, "The restored lockout keeps its original end time");
                AssertFalse(restarted.IsLockedOut("10.0.0.2", out int _), "Failure counts below the limit are not persisted");

                ProxyLoginRateLimiter muchLater = new ProxyLoginRateLimiter(settings, statePath, () => nowUtc.AddSeconds(601));
                AssertFalse(muchLater.IsLockedOut("10.0.0.1", out int _), "A lockout that ended before the restart is not restored");
            }));

            cases.Add(Case("login_limiter_reports_an_unreadable_state_file_and_starts_clean", "LoginLimiter ReportsAnUnreadableStateFileAndStartsClean", TestTags.Negative, () =>
            {
                string directory = TestTemp.NewDirectory("proxy-lockouts-bad");
                string statePath = ProxyLoginRateLimiter.DefaultStatePath(directory);
                File.WriteAllText(statePath, "{ this is not json");
                List<string> warnings = new List<string>();

                ProxyLoginRateLimiter limiter = new ProxyLoginRateLimiter(new ProxySettings(), statePath, warn: warnings.Add);

                AssertFalse(limiter.IsLockedOut("10.0.0.1", out int _), "An unreadable file restores nothing");
                AssertEqual(1, warnings.Count, "The loss is reported, not swallowed");
                AssertContains("lockout state", warnings[0], "The warning names what was lost");
            }));

            cases.Add(Case("login_limiter_bounds_its_failure_and_lockout_stores", "LoginLimiter BoundsItsFailureAndLockoutStores", TestTags.Negative, () =>
            {
                DateTime nowUtc = new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc);
                ProxySettings settings = new ProxySettings { LoginMaxFailures = 2, LoginLockoutSeconds = 3600, LoginFailureWindowSeconds = 3600 };
                ProxyLoginRateLimiter limiter = new ProxyLoginRateLimiter(settings, utcNow: () => nowUtc);

                for (int i = 0; i < ProxyLoginRateLimiter.MaxTrackedKeys + 50; i++)
                {
                    nowUtc = nowUtc.AddMilliseconds(1);
                    limiter.RecordFailure("flood-" + i, out int _);
                    limiter.RecordFailure("flood-" + i, out int _);
                }

                AssertTrue(limiter.LockedKeyCount <= ProxyLoginRateLimiter.MaxTrackedKeys, "A flood of distinct clients cannot grow the lockout store past its bound");
                AssertTrue(limiter.IsLockedOut("flood-" + (ProxyLoginRateLimiter.MaxTrackedKeys + 49), out int _), "The newest lockout is kept");

                for (int i = 0; i < ProxyLoginRateLimiter.MaxTrackedKeys + 50; i++)
                {
                    nowUtc = nowUtc.AddMilliseconds(1);
                    limiter.RecordFailure("single-" + i, out int _);
                }

                AssertFalse(limiter.IsLockedOut("single-0", out int _), "Single failures never lock");
            }));

            cases.Add(Case("startup_refuses_the_built_in_password_unless_allowed", "Startup RefusesTheBuiltInPasswordUnlessAllowed", TestTags.Negative, () =>
            {
                ProxySettings blank = new ProxySettings { Password = "  " };
                AssertTrue(blank.IsDefaultPassword, "A blank password normalizes to the built-in default");
                AssertNotNull(blank.GetStartupSecurityError(), "A blank password is refused");

                ProxySettings builtIn = new ProxySettings { Password = Constants.DefaultRemoteTunnelPassword };
                AssertNotNull(builtIn.GetStartupSecurityError(), "The built-in password is refused");

                builtIn.AllowDefaultPassword = true;
                AssertNull(builtIn.GetStartupSecurityError(), "The explicit allow lets a throwaway test run");

                ProxySettings real = new ProxySettings { Password = "real-secret" };
                AssertFalse(real.IsDefaultPassword);
                AssertNull(real.GetStartupSecurityError(), "A configured password starts");
            }));

            cases.Add(CaseAsync("start_throws_before_listening_with_the_built_in_password", "Start ThrowsBeforeListeningWithTheBuiltInPassword", TestTags.Negative, async () =>
            {
                string directory = TestTemp.NewDirectory("proxy-default-password");
                ProxySettings settings = new ProxySettings
                {
                    Hostname = "127.0.0.1",
                    DataDirectory = directory,
                    LogDirectory = Path.Combine(directory, "logs")
                };
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                using (ArmadaProxyServer proxy = new ArmadaProxyServer(logging, settings, quiet: true))
                {
                    await AssertThrowsAsync<InvalidOperationException>(() => proxy.StartAsync()).ConfigureAwait(false);
                }
            }));

            cases.Add(Case("environment_overrides_set_the_password_and_the_allow_flag", "Environment OverridesSetThePasswordAndTheAllowFlag", TestTags.Positive, () =>
            {
                Dictionary<string, string> environment = new Dictionary<string, string>
                {
                    [ProxySettings.PasswordEnvironmentVariable] = "from-the-environment",
                    [ProxySettings.AllowDefaultPasswordEnvironmentVariable] = "true"
                };
                ProxySettings settings = new ProxySettings { Password = "from-the-file" };
                settings.ApplyEnvironmentOverrides(name => environment.TryGetValue(name, out string? value) ? value : null);
                AssertEqual("from-the-environment", settings.Password, "The environment password wins over the file");
                AssertTrue(settings.AllowDefaultPassword, "The environment sets the allow flag");

                ProxySettings untouched = new ProxySettings { Password = "from-the-file" };
                untouched.ApplyEnvironmentOverrides(name => null);
                AssertEqual("from-the-file", untouched.Password, "Unset variables change nothing");
            }));

            cases.Add(Case("settings_load_reads_the_hardening_keys", "Settings LoadReadsTheHardeningKeys", TestTags.Positive, () =>
            {
                string directory = TestTemp.NewDirectory("proxy-settings");
                string path = Path.Combine(directory, "proxysettings.json");
                File.WriteAllText(path, "{ \"ArmadaProxy\": { \"password\": \"file-secret\", \"allowDefaultPassword\": false, \"trustForwardedHeaders\": true, \"secureCookie\": true, \"loginMaxFailures\": 4, \"loginFailureWindowSeconds\": 120, \"loginLockoutSeconds\": 300 } }");

                ProxySettings settings = ProxySettings.Load(path);

                AssertTrue(settings.TrustForwardedHeaders);
                AssertTrue(settings.SecureCookie);
                AssertEqual(4, settings.LoginMaxFailures);
                AssertEqual(120, settings.LoginFailureWindowSeconds);
                AssertEqual(300, settings.LoginLockoutSeconds);

                ProxySettings defaults = new ProxySettings();
                AssertFalse(defaults.TrustForwardedHeaders, "Forwarded headers are untrusted by default");
                AssertFalse(defaults.SecureCookie);
                AssertEqual(10, defaults.LoginMaxFailures);
                AssertEqual(900, defaults.LoginFailureWindowSeconds);
                AssertEqual(900, defaults.LoginLockoutSeconds);
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Proxy Security",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
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
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
