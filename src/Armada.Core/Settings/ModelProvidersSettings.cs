namespace Armada.Core.Settings
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Registry of external model providers keyed by the model-id namespace prefix,
    /// for example <c>example-provider</c>. Runtimes resolve a captain's model against this
    /// registry so provider behavior is configuration, not code.
    /// </summary>
    /// <remarks>
    /// Lives under the <c>modelProviders</c> key in settings.json. The built-in
    /// default registry is empty; every external provider must be registered in
    /// settings.json by the operator.
    /// </remarks>
    public class ModelProvidersSettings
    {
        #region Public-Members

        /// <summary>
        /// Provider definitions keyed by model-id namespace prefix, matched
        /// case-insensitively. Setting this to null restores the built-in default set.
        /// </summary>
        public Dictionary<string, ModelProviderSettings> Providers
        {
            get => _Providers;
            set => _Providers = value ?? BuildDefaultProviders();
        }

        #endregion

        #region Private-Members

        private Dictionary<string, ModelProviderSettings> _Providers = BuildDefaultProviders();

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the built-in provider registry. Empty: external providers are
        /// registered per deployment in settings.json, keeping provider behavior
        /// neutral by default.
        /// </summary>
        /// <returns>Case-insensitive registry dictionary.</returns>
        public static Dictionary<string, ModelProviderSettings> BuildDefaultProviders()
        {
            return new Dictionary<string, ModelProviderSettings>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Find a registered provider by namespace prefix, case-insensitively.
        /// </summary>
        /// <param name="providerId">Provider namespace prefix, for example "example-provider".</param>
        /// <returns>The provider definition, or null when not registered.</returns>
        public ModelProviderSettings? Find(string? providerId)
        {
            if (String.IsNullOrWhiteSpace(providerId)) return null;
            if (_Providers.TryGetValue(providerId.Trim(), out ModelProviderSettings? provider)) return provider;
            return null;
        }

        #endregion
    }
}
