namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// Deletes subscription accounts. A delete is refused while the account lists captains, cancels a pending dashboard
    /// login, removes the account and every persona route that names it, saves through the settings save path, forgets
    /// the account's usage state, and deletes only the server-derived account folder. A homeDirectory that is not that
    /// folder is left in place. Credential file contents are never read, logged, or returned.
    /// </summary>
    public sealed class UsageAccountAdminService
    {
        #region Public-Members

        /// <summary>The account still lists captains, which would silently fall back to the shared login.</summary>
        public const string ReasonHasCaptains = "account_has_captains";

        /// <summary>The account is not saved in the usage routing policy.</summary>
        public const string ReasonNotFound = "account_not_found";

        /// <summary>The policy without the account did not pass validation, so nothing was changed.</summary>
        public const string ReasonPolicyInvalid = "account_delete_policy_invalid";

        /// <summary>The settings could not be saved, so the account was kept.</summary>
        public const string ReasonSaveFailed = "account_delete_save_failed";

        /// <summary>The server-derived account folder was deleted.</summary>
        public const string HomeDeleted = "account_home_deleted";

        /// <summary>No server-derived account folder existed.</summary>
        public const string HomeNotFound = "account_home_not_found";

        /// <summary>The account's homeDirectory is not the server-derived folder, so that folder was left in place.</summary>
        public const string HomeNotManaged = "account_home_not_managed";

        /// <summary>The server-derived account folder could not be deleted.</summary>
        public const string HomeDeleteFailed = "account_home_delete_failed";

        #endregion

        #region Private-Members

        private readonly ArmadaSettings _Settings;
        private readonly AccountLoginService _Logins;
        private readonly Func<Task> _SaveSettings;
        private readonly Func<CancellationToken, Task<List<Captain>>>? _LoadCaptains;
        private readonly Action<string> _Log;
        private readonly Action<string, string, string>? _EmitEvent;
        private readonly SemaphoreSlim _Lock = new SemaphoreSlim(1, 1);

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate.</summary>
        /// <param name="settings">Live Admiral settings; the policy is replaced on <see cref="ModelTierSettings.UsageRouting"/>.</param>
        /// <param name="logins">Account login service, for pending logins and the server-derived folder.</param>
        /// <param name="saveSettings">The settings save path, the same one the settings update route uses.</param>
        /// <param name="loadCaptains">Optional captain source for the captain binding check the settings update route runs.</param>
        /// <param name="log">Log sink. Receives account IDs, counts, and reason codes only.</param>
        /// <param name="emitEvent">Optional event sink: event type, message, account ID.</param>
        public UsageAccountAdminService(ArmadaSettings settings, AccountLoginService logins, Func<Task> saveSettings, Func<CancellationToken, Task<List<Captain>>>? loadCaptains = null, Action<string>? log = null, Action<string, string, string>? emitEvent = null)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logins = logins ?? throw new ArgumentNullException(nameof(logins));
            _SaveSettings = saveSettings ?? throw new ArgumentNullException(nameof(saveSettings));
            _LoadCaptains = loadCaptains;
            _Log = log ?? (_ => { });
            _EmitEvent = emitEvent;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Delete a saved account. Throws <see cref="AccountLoginException"/> with <see cref="ReasonNotFound"/> (404),
        /// <see cref="ReasonHasCaptains"/> (409), <see cref="ReasonPolicyInvalid"/> (400), or <see cref="ReasonSaveFailed"/> (500).
        /// </summary>
        /// <param name="accountId">Account identifier, matched exactly.</param>
        /// <param name="token">Cancellation token.</param>
        public async Task<UsageAccountDeleteResult> DeleteAsync(string accountId, CancellationToken token = default)
        {
            await _Lock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                UsageRoutingSettings current = _Settings.ModelTier.UsageRouting;
                UsageAccountSettings? account = current.Accounts.FirstOrDefault(a => a != null && String.Equals(a.Id, accountId, StringComparison.Ordinal));
                if (account == null)
                    throw new AccountLoginException(ReasonNotFound, 404, "No saved usage account has this ID.");
                if (account.CaptainIds != null && account.CaptainIds.Count > 0)
                    throw new AccountLoginException(ReasonHasCaptains, 409, "Unassign the account's " + account.CaptainIds.Count + " captains before deleting it; a captain left on a deleted account would launch with the shared login.");

                UsageAccountDeleteResult result = new UsageAccountDeleteResult { AccountId = account.Id };
                bool safeId = AccountLoginPaths.IsSafeAccountId(account.Id);
                if (safeId)
                {
                    AccountLoginSession? session = _Logins.GetSession(account.Id);
                    if (session != null && session.State == AccountLoginStateEnum.Pending)
                        result.LoginCancelled = _Logins.Cancel(account.Id)?.State == AccountLoginStateEnum.Cancelled;
                }

                UsageRoutingSettings next = WithoutAccount(current, account.Id, result);
                try
                {
                    List<Captain> captains = _LoadCaptains != null ? await _LoadCaptains(token).ConfigureAwait(false) : new List<Captain>();
                    SettingsCandidateValidator.ValidateUsageRouting(next, _Settings.DataDirectory, captains);
                }
                catch (ArgumentException ex)
                {
                    throw new AccountLoginException(ReasonPolicyInvalid, 400, ex.Message);
                }

                _Settings.ModelTier.UsageRouting = next;
                try
                {
                    await _SaveSettings().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    _Settings.ModelTier.UsageRouting = current;
                    throw new AccountLoginException(ReasonSaveFailed, 500, "The settings file could not be saved; the account was kept.");
                }

                UsageRoutingService.For(_Settings).ForgetAccountState(account.Id);
                DeleteHome(account, safeId, result);
                _Log("account " + account.Id + " deleted: routes removed " + result.RoutesRemoved + ", " + result.HomeReason);
                _EmitEvent?.Invoke("account.deleted", "Account " + account.Id + " deleted", account.Id);
                return result;
            }
            finally
            {
                _Lock.Release();
            }
        }

        #endregion

        #region Private-Methods

        /// <summary>A copy of the policy without the account and its persona routes; a persona left with no routes is dropped.</summary>
        private static UsageRoutingSettings WithoutAccount(UsageRoutingSettings current, string accountId, UsageAccountDeleteResult result)
        {
            // A serialized copy carries every policy field, so a field added later is never silently dropped here.
            UsageRoutingSettings next = JsonSerializer.Deserialize<UsageRoutingSettings>(JsonSerializer.Serialize(current))
                ?? throw new AccountLoginException(ReasonPolicyInvalid, 400, "The usage routing policy could not be copied.");
            next.Accounts = next.Accounts.Where(a => a == null || !String.Equals(a.Id, accountId, StringComparison.Ordinal)).ToList();
            Dictionary<string, List<UsageRouteSettings>> routes = new Dictionary<string, List<UsageRouteSettings>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, List<UsageRouteSettings>> pair in next.PersonaRoutes)
            {
                if (pair.Value == null)
                {
                    routes[pair.Key] = pair.Value!;
                    continue;
                }
                // Validation matches route account IDs without regard to case, so removal does too.
                List<UsageRouteSettings> kept = pair.Value.Where(r => r == null || !String.Equals(r.AccountId, accountId, StringComparison.OrdinalIgnoreCase)).ToList();
                int removed = pair.Value.Count - kept.Count;
                result.RoutesRemoved += removed;
                if (removed > 0 && kept.Count == 0) result.PersonasRemoved.Add(pair.Key);
                else routes[pair.Key] = kept;
            }
            next.PersonaRoutes = routes;
            return next;
        }

        private void DeleteHome(UsageAccountSettings account, bool safeId, UsageAccountDeleteResult result)
        {
            if (!safeId)
            {
                // An ID that is not a safe folder name never had a server-derived folder.
                result.HomeReason = String.IsNullOrWhiteSpace(account.HomeDirectory) ? HomeNotFound : HomeNotManaged;
                return;
            }
            string derived = _Logins.HomeFor(account.Id);
            bool configuredElsewhere = !String.IsNullOrWhiteSpace(account.HomeDirectory) && !AccountLoginPaths.IsSamePath(account.HomeDirectory, derived);
            try
            {
                DirectoryInfo folder = new DirectoryInfo(derived);
                if (folder.LinkTarget != null)
                {
                    // Remove the link itself; never follow it to a folder outside the accounts root.
                    if (OperatingSystem.IsWindows()) Directory.Delete(derived, false);
                    else File.Delete(derived);
                    result.HomeDeleted = true;
                }
                else if (folder.Exists)
                {
                    Directory.Delete(derived, true);
                    result.HomeDeleted = true;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                result.HomeReason = HomeDeleteFailed;
                return;
            }
            result.HomeReason = configuredElsewhere ? HomeNotManaged : result.HomeDeleted ? HomeDeleted : HomeNotFound;
        }

        #endregion
    }
}
