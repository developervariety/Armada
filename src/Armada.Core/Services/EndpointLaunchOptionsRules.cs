namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// The rules for endpoint launch options. A list longer than
    /// <see cref="CollectionLimits.MaxConfigItems"/> is a collection-limit refusal. A name, range
    /// or duplicate failure is an <see cref="EndpointLaunchOptionsException"/>.
    /// </summary>
    public static class EndpointLaunchOptionsRules
    {
        #region Public-Methods

        /// <summary>
        /// Validate launch options. Null options are the defaults and are valid.
        /// </summary>
        /// <param name="options">Options to validate.</param>
        public static void Validate(EndpointLaunchOptions? options)
        {
            if (options == null) return;
            if (options.MaxIterations < 1 || options.MaxIterations > EndpointLaunchOptions.MaxIterationLimit)
                throw new EndpointLaunchOptionsException("MaxIterations", "MaxIterations must be from 1 to " + EndpointLaunchOptions.MaxIterationLimit + ".");
            if (options.MaxTokens.HasValue && (options.MaxTokens.Value < 1 || options.MaxTokens.Value > EndpointLaunchOptions.MaxTokenLimit))
                throw new EndpointLaunchOptionsException("MaxTokens", "MaxTokens must be from 1 to " + EndpointLaunchOptions.MaxTokenLimit + " when it is set.");

            if (options.AllowedTools == null) return;
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string name in options.AllowedTools)
            {
                if (String.IsNullOrWhiteSpace(name))
                    throw new EndpointLaunchOptionsException("AllowedTools", "An allowed tool name must not be blank.");
                if (!String.Equals(name, name.Trim(), StringComparison.Ordinal))
                    throw new EndpointLaunchOptionsException("AllowedTools", "An allowed tool name must not have surrounding whitespace.");
                if (name.Length > EndpointLaunchOptions.MaxToolNameLength)
                    throw new EndpointLaunchOptionsException("AllowedTools", "An allowed tool name must be at most " + EndpointLaunchOptions.MaxToolNameLength + " characters.");
                if (!seen.Add(name))
                    throw new EndpointLaunchOptionsException("AllowedTools", "Allowed tool names must be unique.");
            }

            CollectionLimits.Require(options.AllowedTools, "AllowedTools", CollectionLimits.MaxConfigItems);
        }

        #endregion
    }
}
