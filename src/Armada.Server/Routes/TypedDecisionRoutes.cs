namespace Armada.Server.Routes
{
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
    using SyslogLogging;

    /// <summary>
    /// Administrator routes for the typed-decision system: the effective and stored modes, per-decision modes and
    /// thresholds, and the provider key file. The key is accepted, written, and removed here and never returned.
    /// </summary>
    public class TypedDecisionRoutes
    {
        #region Private-Members

        private const string _Header = "[TypedDecisionRoutes] ";
        private readonly ArmadaSettings _Settings;
        private readonly TypedDecisionKeyStore _Keys;
        private readonly JsonSerializerOptions _JsonOptions;
        private readonly LoggingModule _Logging;
        private readonly Func<Task> _Save;
        private readonly SemaphoreSlim _Lock = new SemaphoreSlim(1, 1);

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create the routes.</summary>
        /// <param name="settings">Live settings.</param>
        /// <param name="keys">The key store.</param>
        /// <param name="jsonOptions">JSON options.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="save">Saves settings through the normal settings save path.</param>
        public TypedDecisionRoutes(ArmadaSettings settings, TypedDecisionKeyStore keys, JsonSerializerOptions jsonOptions, LoggingModule logging, Func<Task> save)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Keys = keys ?? throw new ArgumentNullException(nameof(keys));
            _JsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Save = save ?? throw new ArgumentNullException(nameof(save));
        }

        #endregion

        #region Public-Methods

        /// <summary>Only a caller with settings write permission (a global administrator) may use these routes.</summary>
        /// <param name="ctx">Authentication context.</param>
        /// <param name="authz">Authorization service.</param>
        /// <returns>True when permitted.</returns>
        public static bool IsPermitted(AuthContext ctx, IAuthorizationService authz)
        {
            if (ctx == null || authz == null) return false;
            return authz.IsAuthorized(ctx, "PUT", "/api/v1/settings");
        }

        /// <summary>Register the routes.</summary>
        /// <param name="app">Webserver.</param>
        /// <param name="authenticate">Authentication delegate.</param>
        /// <param name="authz">Authorization service.</param>
        public void Register(
            Webserver app,
            Func<WatsonWebserver.Core.HttpContextBase, Task<AuthContext>> authenticate,
            IAuthorizationService authz)
        {
            app.Get("/api/v1/typed-decisions", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!IsPermitted(ctx, authz)) return Refuse(req, ctx);
                return TypedDecisionStatusBuilder.Build(_Settings.TypedDecisions, _Keys);
            },
            api => api
                .WithTag("Settings")
                .WithSummary("Get typed-decision modes and key presence")
                .WithDescription("Returns the effective global mode (Off with reason typed_decisions_no_key when no key resolves), the stored global mode, whether a key is present and its source (env or file), and every decision's mode, threshold, and description. Never returns the key.")
                .WithResponse(200, OpenApiJson.For<TypedDecisionStatus>("Typed-decision status"))
                .WithSecurity("ApiKey"));

            app.Put<TypedDecisionsUpdateRequest>("/api/v1/typed-decisions", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!IsPermitted(ctx, authz)) return Refuse(req, ctx);
                TypedDecisionsUpdateRequest body;
                try
                {
                    body = JsonSerializer.Deserialize<TypedDecisionsUpdateRequest>(req.Http.Request.DataAsString, _JsonOptions)
                        ?? throw new ArgumentException("A request body is required.");
                    Validate(body);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is JsonException)
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = ex is JsonException ? "The request body is not valid JSON or names an unknown mode." : ex.Message };
                }

                await _Lock.WaitAsync().ConfigureAwait(false);
                try
                {
                    TypedDecisionSettings live = _Settings.TypedDecisions;
                    TypedDecisionModeEnum previousMode = live.Mode;
                    Dictionary<string, TypedDecisionRuleSettings> previousDecisions = Snapshot(live.Decisions);
                    if (body.Mode.HasValue) live.Mode = body.Mode.Value;
                    if (body.Decisions != null)
                    {
                        foreach (KeyValuePair<string, TypedDecisionRuleUpdate> pair in body.Decisions)
                        {
                            if (!live.Decisions.TryGetValue(pair.Key, out TypedDecisionRuleSettings? rule) || rule == null)
                            {
                                rule = new TypedDecisionRuleSettings();
                                live.Decisions[pair.Key] = rule;
                            }
                            if (pair.Value.Mode.HasValue) rule.Mode = pair.Value.Mode.Value;
                            if (pair.Value.GateThreshold.HasValue) rule.GateThreshold = pair.Value.GateThreshold.Value;
                        }
                    }
                    try
                    {
                        await _Save().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        live.Mode = previousMode;
                        live.Decisions = previousDecisions;
                        req.Http.Response.StatusCode = 500;
                        return new ApiErrorResponse { Error = ApiResultEnum.InternalError, Message = "typed_decisions_save_failed: settings could not be saved; nothing changed." };
                    }
                    _Logging.Info(_Header + "typed-decision modes updated via API: mode=" + live.Mode
                        + (body.Decisions != null ? " decisions=" + String.Join(",", body.Decisions.Keys) : String.Empty));
                }
                finally { _Lock.Release(); }
                return TypedDecisionStatusBuilder.Build(_Settings.TypedDecisions, _Keys);
            },
            api => api
                .WithTag("Settings")
                .WithSummary("Update typed-decision modes and thresholds")
                .WithDescription("Updates the global mode and any shipped decision's mode (Off, Shadow, Gate) or gateThreshold (0 to 1), then saves settings. Unknown decisions, modes, or thresholds are refused with 400.")
                .WithRequestBody(OpenApiJson.BodyFor<TypedDecisionsUpdateRequest>("Mode and threshold changes", true))
                .WithResponse(200, OpenApiJson.For<TypedDecisionStatus>("Typed-decision status"))
                .WithSecurity("ApiKey"));

            app.Put<TypedDecisionKeyRequest>("/api/v1/typed-decisions/key", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!IsPermitted(ctx, authz)) return Refuse(req, ctx);
                TypedDecisionKeyRequest? body;
                try
                {
                    body = JsonSerializer.Deserialize<TypedDecisionKeyRequest>(req.Http.Request.DataAsString, _JsonOptions);
                }
                catch (JsonException)
                {
                    // The body holds a key, so the parser's message, which can quote it, is not passed on.
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "The request body is not valid JSON." };
                }
                try
                {
                    await _Keys.WriteKeyAsync(body?.ApiKey).ConfigureAwait(false);
                }
                catch (ArgumentException ex)
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "typed_decisions_key_invalid: " + ex.Message };
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    req.Http.Response.StatusCode = 500;
                    return new ApiErrorResponse { Error = ApiResultEnum.InternalError, Message = "typed_decisions_key_write_failed: the key file could not be written." };
                }
                _Logging.Info(_Header + "typed-decision key file written via API");
                req.Http.Response.StatusCode = 204;
                return null;
            },
            api => api
                .WithTag("Settings")
                .WithSummary("Store the typed-decision provider key")
                .WithDescription("Writes the key to <data directory>/secrets/typesafe-api-key (folder 0700, file 0600). Takes effect without a restart. The key is never logged, recorded, stored in settings, or returned. The environment variable named by typedDecisions.apiKeyEnv still wins when set.")
                .WithRequestBody(OpenApiJson.BodyFor<TypedDecisionKeyRequest>("Provider key", true))
                .WithResponse(204, OpenApiResponseMetadata.NoContent())
                .WithSecurity("ApiKey"));

            app.Delete("/api/v1/typed-decisions/key", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!IsPermitted(ctx, authz)) return Refuse(req, ctx);
                bool removed;
                try
                {
                    removed = _Keys.DeleteKeyFile();
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    req.Http.Response.StatusCode = 500;
                    return new ApiErrorResponse { Error = ApiResultEnum.InternalError, Message = "typed_decisions_key_delete_failed: the key file could not be removed." };
                }
                _Logging.Info(_Header + "typed-decision key file " + (removed ? "removed" : "was absent") + " via API");
                TypedDecisionStatus status = TypedDecisionStatusBuilder.Build(_Settings.TypedDecisions, _Keys);
                return new
                {
                    FileRemoved = removed,
                    EnvironmentSuppliesKey = _Keys.EnvironmentSuppliesKey(_Settings.TypedDecisions),
                    status.KeyPresent,
                    status.KeySource,
                    status.EffectiveMode,
                    status.EffectiveReason
                };
            },
            api => api
                .WithTag("Settings")
                .WithSummary("Remove the typed-decision key file")
                .WithDescription("Deletes the key file. When the environment variable still supplies a key, environmentSuppliesKey is true and the effective mode is unchanged.")
                .WithSecurity("ApiKey"));
        }

        #endregion

        #region Private-Methods

        private static void Validate(TypedDecisionsUpdateRequest body)
        {
            if (body.Mode.HasValue && !Enum.IsDefined(typeof(TypedDecisionModeEnum), body.Mode.Value))
                throw new ArgumentException("typed_decisions_mode_invalid: mode must be Off, Shadow, or Gate.");
            if (body.Decisions == null) return;
            foreach (KeyValuePair<string, TypedDecisionRuleUpdate> pair in body.Decisions)
            {
                if (String.IsNullOrWhiteSpace(pair.Key) || !TypedDecisionSettings.ShippedDecisionNames.Contains(pair.Key))
                    throw new ArgumentException("typed_decisions_unknown_decision: " + (pair.Key ?? String.Empty) + " is not a shipped decision.");
                if (pair.Value == null) throw new ArgumentException("typed_decisions_update_invalid: " + pair.Key + " has no changes.");
                if (pair.Value.Mode.HasValue && !Enum.IsDefined(typeof(TypedDecisionModeEnum), pair.Value.Mode.Value))
                    throw new ArgumentException("typed_decisions_mode_invalid: " + pair.Key + " mode must be Off, Shadow, or Gate.");
                if (pair.Value.GateThreshold.HasValue && (!Double.IsFinite(pair.Value.GateThreshold.Value) || pair.Value.GateThreshold.Value < 0 || pair.Value.GateThreshold.Value > 1))
                    throw new ArgumentException("typed_decisions_threshold_invalid: " + pair.Key + " gateThreshold must be between 0 and 1.");
            }
        }

        private static Dictionary<string, TypedDecisionRuleSettings> Snapshot(Dictionary<string, TypedDecisionRuleSettings> decisions)
        {
            Dictionary<string, TypedDecisionRuleSettings> copy = new Dictionary<string, TypedDecisionRuleSettings>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, TypedDecisionRuleSettings> pair in decisions)
                copy[pair.Key] = new TypedDecisionRuleSettings { Mode = pair.Value.Mode, GateThreshold = pair.Value.GateThreshold };
            return copy;
        }

        private static object Refuse(ApiRequest req, AuthContext ctx)
        {
            req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
            return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = ctx.IsAuthenticated ? "Administrator access required" : "Authentication required" };
        }

        #endregion
    }
}
