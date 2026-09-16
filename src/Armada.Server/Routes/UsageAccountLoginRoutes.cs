namespace Armada.Server.Routes
{
    using System.IO;
    using System.Text.Json;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;
    using Armada.Server;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>
    /// REST routes that log a subscription account in from the dashboard. Every route needs settings write permission
    /// (administrator), like the settings routes. Account folders are derived on the server; no route accepts a path.
    /// These routes are never captured in request history, because their bodies carry keys and codes.
    /// </summary>
    public class UsageAccountLoginRoutes
    {
        #region Public-Members

        /// <summary>The saved account does not use the server-derived folder or key file, so a dashboard login cannot target it.</summary>
        public const string ReasonNotManaged = "account_login_target_not_managed";

        /// <summary>The account is not saved in the usage routing policy, or has no runtime.</summary>
        public const string ReasonAccountNotConfigured = "account_not_configured";

        #endregion

        #region Private-Members

        private readonly ArmadaSettings _Settings;
        private readonly AccountLoginService _Logins;
        private readonly JsonSerializerOptions _JsonOptions;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate.</summary>
        /// <param name="settings">Application settings.</param>
        /// <param name="logins">Account login service.</param>
        /// <param name="jsonOptions">JSON serializer options.</param>
        public UsageAccountLoginRoutes(ArmadaSettings settings, AccountLoginService logins, JsonSerializerOptions jsonOptions)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logins = logins ?? throw new ArgumentNullException(nameof(logins));
            _JsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
        }

        #endregion

        #region Public-Methods

        /// <summary>True when the caller may manage account logins: the same permission as writing settings.</summary>
        public static bool IsPermitted(AuthContext ctx, IAuthorizationService authz)
        {
            if (ctx == null || authz == null) return false;
            return authz.IsAuthorized(ctx, "PUT", "/api/v1/settings");
        }

        /// <summary>Register routes with the application.</summary>
        /// <param name="app">Webserver.</param>
        /// <param name="authenticate">Authentication middleware.</param>
        /// <param name="authz">Authorization service.</param>
        public void Register(
            Webserver app,
            Func<WatsonWebserver.Core.HttpContextBase, Task<AuthContext>> authenticate,
            IAuthorizationService authz)
        {
            app.Post("/api/v1/usage-accounts/{accountId}/login/home", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!IsPermitted(ctx, authz)) return Refuse(req, ctx);
                return Run(req, () => _Logins.EnsureHome(req.Parameters["accountId"]));
            },
            api => api
                .WithTag("Settings")
                .WithSummary("Create a subscription account folder")
                .WithDescription("Creates <data directory>/accounts/<accountId> with owner-only permissions and returns the folder and Cursor key file path to put in the account policy. Idempotent.")
                .WithParameter(OpenApiParameterMetadata.Path("accountId", "Usage account ID: letters, digits, hyphens, underscores"))
                .WithResponse(200, OpenApiJson.For<AccountLoginHomeResult>("Account folder"))
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/usage-accounts/{accountId}/login/start", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!IsPermitted(ctx, authz)) return Refuse(req, ctx);
                string accountId = req.Parameters["accountId"];
                try
                {
                    UsageAccountSettings account = RequireAccount(accountId);
                    AgentRuntimeEnum runtime = account.Runtime!.Value;
                    if (runtime != AgentRuntimeEnum.Cursor) RequireManagedHome(account);
                    return (object)await _Logins.StartAsync(accountId, runtime).ConfigureAwait(false);
                }
                catch (AccountLoginException ex) { return Error(req, ex); }
            },
            api => api
                .WithTag("Settings")
                .WithSummary("Start a browser login for a subscription account")
                .WithDescription("Runs the runtime's login command in the account folder (Codex device login, Claude Code sign-in, Cursor login) and returns only the verification URL and user code. One login per account; a pending login ends after 15 minutes.")
                .WithParameter(OpenApiParameterMetadata.Path("accountId", "Usage account ID"))
                .WithResponse(200, OpenApiJson.For<AccountLoginSession>("Login session"))
                .WithSecurity("ApiKey"));

            app.Post<AccountLoginCodeRequest>("/api/v1/usage-accounts/{accountId}/login/code", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!IsPermitted(ctx, authz)) return Refuse(req, ctx);
                string accountId = req.Parameters["accountId"];
                try
                {
                    AccountLoginCodeRequest body = ReadBody<AccountLoginCodeRequest>(req);
                    return (object)await _Logins.SubmitCodeAsync(accountId, body.Code).ConfigureAwait(false);
                }
                catch (AccountLoginException ex) { return Error(req, ex); }
            },
            api => api
                .WithTag("Settings")
                .WithSummary("Paste a sign-in code back into a pending login")
                .WithDescription("Writes the code to the pending Claude Code login process. The code is never stored or returned.")
                .WithParameter(OpenApiParameterMetadata.Path("accountId", "Usage account ID"))
                .WithRequestBody(OpenApiJson.BodyFor<AccountLoginCodeRequest>("Sign-in code", true))
                .WithResponse(200, OpenApiJson.For<AccountLoginSession>("Login session"))
                .WithSecurity("ApiKey"));

            app.Post<AccountLoginKeyRequest>("/api/v1/usage-accounts/{accountId}/login/key", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!IsPermitted(ctx, authz)) return Refuse(req, ctx);
                string accountId = req.Parameters["accountId"];
                try
                {
                    AccountLoginKeyRequest body = ReadBody<AccountLoginKeyRequest>(req);
                    UsageAccountSettings account = RequireAccount(accountId);
                    AgentRuntimeEnum runtime = account.Runtime!.Value;
                    if (runtime == AgentRuntimeEnum.OpenCode) RequireManagedHome(account);
                    if (runtime == AgentRuntimeEnum.Cursor) RequireManagedKeyFile(account);
                    return (object)_Logins.SubmitKey(accountId, runtime, body.ApiKey);
                }
                catch (AccountLoginException ex) { return Error(req, ex); }
            },
            api => api
                .WithTag("Settings")
                .WithSummary("Store an API key for a subscription account")
                .WithDescription("OpenCode: merges the opencode-go API entry into the account's opencode/auth.json. Cursor: writes the account's key file. Files are owner-only. The key is never logged, returned, or stored in settings.")
                .WithParameter(OpenApiParameterMetadata.Path("accountId", "Usage account ID"))
                .WithRequestBody(OpenApiJson.BodyFor<AccountLoginKeyRequest>("API key", true))
                .WithResponse(200, OpenApiJson.For<AccountLoginSession>("Login session"))
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/usage-accounts/{accountId}/login/status", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!IsPermitted(ctx, authz)) return Refuse(req, ctx);
                return Run(req, () => BuildStatus(req.Parameters["accountId"]));
            },
            api => api
                .WithTag("Settings")
                .WithSummary("Get a subscription account's login status")
                .WithDescription("Returns the last dashboard login (Pending, Succeeded, Failed, Expired, Cancelled with a safe reason) and the server's own login check for the saved account.")
                .WithParameter(OpenApiParameterMetadata.Path("accountId", "Usage account ID"))
                .WithResponse(200, OpenApiJson.For<AccountLoginStatus>("Login status"))
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/usage-accounts/{accountId}/login/cancel", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!IsPermitted(ctx, authz)) return Refuse(req, ctx);
                return Run(req, () => BuildStatusAfterCancel(req.Parameters["accountId"]));
            },
            api => api
                .WithTag("Settings")
                .WithSummary("Cancel a pending subscription account login")
                .WithDescription("Stops the pending login process for the account and returns the login status.")
                .WithParameter(OpenApiParameterMetadata.Path("accountId", "Usage account ID"))
                .WithResponse(200, OpenApiJson.For<AccountLoginStatus>("Login status"))
                .WithSecurity("ApiKey"));
        }

        #endregion

        #region Private-Methods

        private static object Refuse(ApiRequest req, AuthContext ctx)
        {
            req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
            return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = ctx.IsAuthenticated ? "Administrator access required" : "Authentication required" };
        }

        private static object Error(ApiRequest req, AccountLoginException ex)
        {
            req.Http.Response.StatusCode = ex.StatusCode;
            ApiResultEnum error = ex.StatusCode == 404 ? ApiResultEnum.NotFound : ex.StatusCode == 409 ? ApiResultEnum.Conflict : ApiResultEnum.BadRequest;
            return new ApiErrorResponse { Error = error, Message = ex.Code + ": " + ex.Message };
        }

        private static object Run(ApiRequest req, Func<object> action)
        {
            try { return action(); }
            catch (AccountLoginException ex) { return Error(req, ex); }
        }

        private T ReadBody<T>(ApiRequest req) where T : class
        {
            try
            {
                return JsonSerializer.Deserialize<T>(req.Http.Request.DataAsString, _JsonOptions)
                    ?? throw new AccountLoginException(AccountLoginService.ReasonInputInvalid, 400, "A request body is required.");
            }
            catch (JsonException)
            {
                // The body may hold a key, so the parser's message, which can quote it, is not passed on.
                throw new AccountLoginException(AccountLoginService.ReasonInputInvalid, 400, "The request body is not valid JSON.");
            }
        }

        private UsageAccountSettings? FindAccount(string accountId)
        {
            List<UsageAccountSettings>? accounts = _Settings.ModelTier?.UsageRouting?.Accounts;
            return accounts?.FirstOrDefault(a => a != null && String.Equals(a.Id, accountId, StringComparison.Ordinal));
        }

        private UsageAccountSettings RequireAccount(string accountId)
        {
            _Logins.HomeFor(accountId);
            UsageAccountSettings? account = FindAccount(accountId);
            if (account == null || !account.Runtime.HasValue)
                throw new AccountLoginException(ReasonAccountNotConfigured, 404, "Save the account with a runtime in the routing policy before logging in.");
            return account;
        }

        private void RequireManagedHome(UsageAccountSettings account)
        {
            string home = _Logins.HomeFor(account.Id);
            if (String.IsNullOrWhiteSpace(account.HomeDirectory) || !SamePath(account.HomeDirectory, home))
                throw new AccountLoginException(ReasonNotManaged, 409, "Set the account's homeDirectory to the server-derived folder before logging in from the dashboard.");
        }

        private void RequireManagedKeyFile(UsageAccountSettings account)
        {
            string file = Path.Combine(_Logins.HomeFor(account.Id), AccountLoginPaths.CursorKeyFileName);
            if (String.IsNullOrWhiteSpace(account.LaunchCredentialFile) || !SamePath(account.LaunchCredentialFile, file))
                throw new AccountLoginException(ReasonNotManaged, 409, "Set the Cursor account's launchCredentialFile to the server-derived key file before storing a key.");
        }

        private static bool SamePath(string configured, string derived)
        {
            try
            {
                string left = Path.GetFullPath(configured).TrimEnd(Path.DirectorySeparatorChar);
                string right = Path.GetFullPath(derived).TrimEnd(Path.DirectorySeparatorChar);
                return String.Equals(left, right, StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return false;
            }
        }

        private AccountLoginStatus BuildStatusAfterCancel(string accountId)
        {
            _Logins.Cancel(accountId);
            return BuildStatus(accountId);
        }

        private AccountLoginStatus BuildStatus(string accountId)
        {
            AccountLoginStatus status = new AccountLoginStatus
            {
                AccountId = accountId,
                HomeDirectory = _Logins.HomeFor(accountId),
                Session = _Logins.GetSession(accountId)
            };
            UsageAccountSettings? account = FindAccount(accountId);
            if (account == null) return status;
            status.Configured = true;
            status.Runtime = account.Runtime;
            if (!CaptainAccountLaunch.HasLaunchIdentity(account)) return status;
            UsageRoutingService usage = UsageRoutingService.For(_Settings);
            string? problem = usage.GetLoginProblem(account, DateTime.UtcNow);
            status.LoginReady = problem == null;
            status.LoginReason = problem;
            status.LoginCheckedUtc = usage.GetLoginCheckedUtc(account.Id);
            return status;
        }

        #endregion
    }
}
