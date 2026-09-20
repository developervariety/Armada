namespace Armada.Core.Services
{
    using System;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// The typed-decision client every decision point holds. It delegates to the live provider client while a key
    /// resolves and to the null client otherwise, checking on every call, so adding or removing the key takes effect
    /// without an Admiral restart.
    /// </summary>
    public sealed class SwitchableTypedDecisionClient : ITypedDecisionClient
    {
        #region Private-Members

        private readonly TypedDecisionSettings _Settings;
        private readonly TypedDecisionKeyStore _Keys;
        private readonly NullTypedDecisionClient _Null = new NullTypedDecisionClient();
        private readonly TypeSafeDecisionClient _Live;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create the client.</summary>
        /// <param name="settings">The live typed-decision settings instance.</param>
        /// <param name="keys">The key store.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="httpClient">HTTP client for the provider.</param>
        public SwitchableTypedDecisionClient(TypedDecisionSettings settings, TypedDecisionKeyStore keys, LoggingModule logging, HttpClient httpClient)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Keys = keys ?? throw new ArgumentNullException(nameof(keys));
            _Live = new TypeSafeDecisionClient(settings, logging, httpClient, () => _Keys.ResolveKey(_Settings, out string? _));
        }

        #endregion

        #region Public-Members

        /// <summary>Observe provider model versions across key changes without replacing the client.</summary>
        public Action<string>? ModelObserved
        {
            get => _Live.ModelObserved;
            set => _Live.ModelObserved = value;
        }

        /// <summary>The client a call made now would use: the provider client with a key, the null client without.</summary>
        public ITypedDecisionClient Current => _Keys.HasKey(_Settings, out string? _) ? _Live : _Null;

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public Task<TypedDecisionResult> DecideAsync(TypedDecisionRequest request, CancellationToken token)
        {
            return Current.DecideAsync(request, token);
        }

        #endregion
    }
}
