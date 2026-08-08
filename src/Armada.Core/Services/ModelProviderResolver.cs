namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// Resolves which external provider endpoint serves a captain's model, if any.
    /// Provider-neutral: the registry in <see cref="ModelProvidersSettings"/> owns the
    /// defaults, and a captain's own key and base URL override them.
    /// </summary>
    /// <remarks>
    /// Resolution rules:
    /// <list type="bullet">
    /// <item>A model id with a registered provider prefix (for example
    /// <c>cun-ai/claude-fable-5</c>) routes to that provider's endpoint.</item>
    /// <item>A model id with an unregistered prefix is left alone so runtimes with
    /// their own namespaces (for example OpenCode's built-in providers) are untouched.</item>
    /// <item>A model id without a prefix routes only when the captain carries an
    /// explicit base URL and key (a custom-endpoint captain). This is how a native
    /// model name such as <c>claude-fable-5</c> can be served by any Anthropic-compatible
    /// provider.</item>
    /// </list>
    /// A resolved captain always needs a usable key; a half-configured launch fails per
    /// step and reads as a provider outage, so resolution returns null instead.
    /// </remarks>
    public static class ModelProviderResolver
    {
        #region Public-Methods

        /// <summary>
        /// Split a model id into its provider namespace prefix and the remaining model
        /// id. <c>cun-ai/claude-fable-5</c> becomes <c>cun-ai</c> and <c>claude-fable-5</c>;
        /// <c>claude-fable-5</c> yields a null prefix.
        /// </summary>
        /// <param name="model">Model id to split.</param>
        /// <param name="providerId">Namespace prefix, or null when the id carries none.</param>
        /// <param name="modelId">Model id after the prefix, or the whole id.</param>
        /// <returns>True when the model is non-empty; false for null or blank input.</returns>
        public static bool TrySplitModel(string? model, out string? providerId, out string modelId)
        {
            providerId = null;
            modelId = String.Empty;
            if (String.IsNullOrWhiteSpace(model)) return false;

            string trimmed = model.Trim();
            int slash = trimmed.IndexOf('/');
            if (slash <= 0)
            {
                modelId = trimmed;
                return true;
            }

            providerId = trimmed.Substring(0, slash);
            modelId = trimmed.Substring(slash + 1);
            return true;
        }

        /// <summary>
        /// Resolve the provider endpoint and credential for a captain's model.
        /// </summary>
        /// <param name="captain">Captain being launched; may be null.</param>
        /// <param name="model">Model id to resolve; the captain's own model wins when both exist.</param>
        /// <param name="providers">Provider registry; null uses the built-in default set.</param>
        /// <returns>Resolved endpoint and key, or null when the captain stays native.</returns>
        public static ResolvedModelProvider? Resolve(Captain? captain, string? model, ModelProvidersSettings? providers)
        {
            ModelProvidersSettings registry = providers ?? new ModelProvidersSettings();
            string? effectiveModel = captain?.Model ?? model;
            if (String.IsNullOrWhiteSpace(effectiveModel)) return null;

            if (TrySplitModel(effectiveModel, out string? providerId, out string modelId))
            {
                if (providerId == null)
                    return ResolveCustomEndpoint(captain, modelId);

                ModelProviderSettings? provider = registry.Find(providerId);
                if (provider == null) return null;

                string baseUrl = !String.IsNullOrWhiteSpace(captain?.ApiBaseUrl)
                    ? captain!.ApiBaseUrl!
                    : provider.BaseUrl;

                string? key = ResolveKey(captain, provider);
                if (String.IsNullOrWhiteSpace(key) || String.IsNullOrWhiteSpace(baseUrl)) return null;

                return new ResolvedModelProvider
                {
                    ProviderId = providerId,
                    BaseUrl = baseUrl,
                    ApiKey = key,
                    ApiModelId = modelId
                };
            }

            return null;
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Resolve a custom-endpoint captain: no provider prefix, so routing requires an
        /// explicit base URL and key on the captain record itself.
        /// </summary>
        /// <param name="captain">Captain being launched; may be null.</param>
        /// <param name="modelId">Model id as carried on the captain record.</param>
        /// <returns>Resolved endpoint and key, or null when not fully configured.</returns>
        private static ResolvedModelProvider? ResolveCustomEndpoint(Captain? captain, string modelId)
        {
            if (captain == null) return null;
            if (String.IsNullOrWhiteSpace(captain.ApiBaseUrl)) return null;
            if (String.IsNullOrWhiteSpace(captain.ApiKey)) return null;

            return new ResolvedModelProvider
            {
                ProviderId = null,
                BaseUrl = captain.ApiBaseUrl,
                ApiKey = captain.ApiKey,
                ApiModelId = modelId
            };
        }

        /// <summary>
        /// Resolve the credential: the captain's own key wins, the provider's host
        /// environment variable is the fallback.
        /// </summary>
        /// <param name="captain">Captain being launched; may be null.</param>
        /// <param name="provider">Provider whose environment-variable name supplies the fallback.</param>
        /// <returns>Credential string, or null when none is configured.</returns>
        private static string? ResolveKey(Captain? captain, ModelProviderSettings provider)
        {
            if (captain != null && !String.IsNullOrWhiteSpace(captain.ApiKey))
                return captain.ApiKey;

            if (String.IsNullOrWhiteSpace(provider.ApiKeyEnv)) return null;
            return Environment.GetEnvironmentVariable(provider.ApiKeyEnv);
        }

        #endregion
    }
}
