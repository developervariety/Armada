namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// In-memory credential-login rate limiter with lockout and exponential backoff (see
    /// <see cref="LoginRateLimitSettings"/>). Keys are accounts (tenant and email) and client addresses. The key store is
    /// bounded: stale keys are pruned and, when the store is still full, the least recently failed unlocked key is
    /// evicted, so a flood of distinct accounts cannot grow memory without limit. Thread-safe. State is lost on restart.
    /// </summary>
    public class LoginRateLimiter
    {
        #region Public-Members

        /// <summary>
        /// Most keys held at once.
        /// </summary>
        public const int MaxTrackedKeys = 10000;

        #endregion

        #region Private-Members

        private readonly LoginRateLimitSettings _Settings;
        private readonly Func<DateTime> _Clock;
        private readonly Dictionary<string, LoginFailureState> _States = new Dictionary<string, LoginFailureState>(StringComparer.Ordinal);
        private readonly object _Lock = new object();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="settings">Rate limit settings (read on every call, so changes apply immediately).</param>
        /// <param name="clock">Optional UTC clock (tests).</param>
        public LoginRateLimiter(LoginRateLimitSettings settings, Func<DateTime>? clock = null)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Clock = clock ?? (() => DateTime.UtcNow);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Remaining lockout for a password login by this account from this address, or null when allowed.
        /// </summary>
        /// <param name="tenantId">Tenant identifier.</param>
        /// <param name="email">Email address.</param>
        /// <param name="address">Client address.</param>
        /// <returns>Time until the lockout ends, or null.</returns>
        public TimeSpan? CheckPasswordLogin(string? tenantId, string? email, string? address)
        {
            if (!_Settings.Enabled) return null;
            TimeSpan? account = GetRetryAfter(AccountKey(tenantId, email));
            TimeSpan? addr = String.IsNullOrEmpty(address) ? null : GetRetryAfter(AddressKey(address));
            return Max(account, addr);
        }

        /// <summary>
        /// Record a failed password login for the account and the address.
        /// </summary>
        /// <param name="tenantId">Tenant identifier.</param>
        /// <param name="email">Email address.</param>
        /// <param name="address">Client address.</param>
        public void RecordPasswordFailure(string? tenantId, string? email, string? address)
        {
            if (!_Settings.Enabled) return;
            RecordFailure(AccountKey(tenantId, email), _Settings.MaxFailuresPerAccount);
            if (!String.IsNullOrEmpty(address)) RecordFailure(AddressKey(address), _Settings.MaxFailuresPerAddress);
        }

        /// <summary>
        /// Clear the account's counter after a successful password login.
        /// </summary>
        /// <param name="tenantId">Tenant identifier.</param>
        /// <param name="email">Email address.</param>
        public void RecordPasswordSuccess(string? tenantId, string? email)
        {
            lock (_Lock)
            {
                _States.Remove(AccountKey(tenantId, email));
            }
        }

        /// <summary>
        /// Number of keys currently tracked.
        /// </summary>
        public int TrackedKeyCount
        {
            get
            {
                lock (_Lock)
                {
                    return _States.Count;
                }
            }
        }

        /// <summary>
        /// Retry-After header value (whole seconds, at least 1) for a remaining lockout.
        /// </summary>
        /// <param name="retryAfter">Remaining lockout.</param>
        /// <returns>Seconds as text.</returns>
        public static string ToRetryAfterSeconds(TimeSpan retryAfter)
        {
            double seconds = Math.Ceiling(retryAfter.TotalSeconds);
            if (seconds < 1) seconds = 1;
            return ((long)seconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        #endregion

        #region Private-Methods

        private static string AccountKey(string? tenantId, string? email)
        {
            return "acct:" + (tenantId ?? String.Empty) + "|" + (email ?? String.Empty).Trim().ToLowerInvariant();
        }

        private static string AddressKey(string address)
        {
            return "addr:" + address;
        }

        private static TimeSpan? Max(TimeSpan? a, TimeSpan? b)
        {
            if (a == null) return b;
            if (b == null) return a;
            return a.Value >= b.Value ? a : b;
        }

        private TimeSpan? GetRetryAfter(string key)
        {
            DateTime now = _Clock();
            lock (_Lock)
            {
                if (!_States.TryGetValue(key, out LoginFailureState? state)) return null;
                if (state.LockedUntilUtc == null || state.LockedUntilUtc.Value <= now) return null;
                return state.LockedUntilUtc.Value - now;
            }
        }

        private void RecordFailure(string key, int maxFailures)
        {
            DateTime now = _Clock();
            TimeSpan window = TimeSpan.FromMinutes(_Settings.WindowMinutes);
            int maxLockoutMinutes = Math.Max(_Settings.MaxLockoutMinutes, _Settings.LockoutMinutes);

            lock (_Lock)
            {
                if (!_States.TryGetValue(key, out LoginFailureState? state))
                {
                    if (_States.Count >= MaxTrackedKeys) MakeRoom(now, maxLockoutMinutes);
                    if (_States.Count >= MaxTrackedKeys) return;

                    state = new LoginFailureState { WindowStartUtc = now };
                    _States[key] = state;
                }

                // A key that has been quiet (no failure, no lockout) for longer than the longest lockout starts over: its
                // backoff is forgotten.
                if (now - LastActivity(state) > TimeSpan.FromMinutes(maxLockoutMinutes))
                {
                    state.Lockouts = 0;
                    state.Failures = 0;
                    state.WindowStartUtc = now;
                }

                state.LastFailureUtc = now;
                if (state.LockedUntilUtc != null && state.LockedUntilUtc.Value > now) return;

                if (now - state.WindowStartUtc > window)
                {
                    state.Failures = 0;
                    state.WindowStartUtc = now;
                }

                state.Failures++;
                if (state.Failures < maxFailures) return;

                double minutes = _Settings.LockoutMinutes * Math.Pow(2, Math.Min(state.Lockouts, 16));
                if (minutes > maxLockoutMinutes) minutes = maxLockoutMinutes;
                state.LockedUntilUtc = now.AddMinutes(minutes);
                state.Lockouts++;
                state.Failures = 0;
                state.WindowStartUtc = now;
            }
        }

        private static DateTime LastActivity(LoginFailureState state)
        {
            if (state.LockedUntilUtc != null && state.LockedUntilUtc.Value > state.LastFailureUtc) return state.LockedUntilUtc.Value;
            return state.LastFailureUtc;
        }

        private void MakeRoom(DateTime now, int maxLockoutMinutes)
        {
            List<string> stale = new List<string>();
            string? oldestUnlocked = null;
            DateTime oldestFailure = DateTime.MaxValue;
            foreach (KeyValuePair<string, LoginFailureState> entry in _States)
            {
                if (now - LastActivity(entry.Value) > TimeSpan.FromMinutes(maxLockoutMinutes))
                {
                    stale.Add(entry.Key);
                    continue;
                }

                bool locked = entry.Value.LockedUntilUtc != null && entry.Value.LockedUntilUtc.Value > now;
                if (!locked && entry.Value.LastFailureUtc < oldestFailure)
                {
                    oldestFailure = entry.Value.LastFailureUtc;
                    oldestUnlocked = entry.Key;
                }
            }

            foreach (string key in stale) _States.Remove(key);
            if (stale.Count == 0 && oldestUnlocked != null) _States.Remove(oldestUnlocked);
        }

        #endregion
    }
}
