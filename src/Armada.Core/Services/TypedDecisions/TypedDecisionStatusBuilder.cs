namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>Builds the operator view of the typed-decision system from settings and the key store.</summary>
    public static class TypedDecisionStatusBuilder
    {
        #region Public-Methods

        /// <summary>Build the view. The key itself is never read into the result.</summary>
        /// <param name="settings">Live typed-decision settings.</param>
        /// <param name="keys">The key store.</param>
        /// <returns>The view.</returns>
        public static TypedDecisionStatus Build(TypedDecisionSettings settings, TypedDecisionKeyStore keys)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (keys == null) throw new ArgumentNullException(nameof(keys));
            bool keyPresent = keys.HasKey(settings, out string? source);
            TypedDecisionStatus status = new TypedDecisionStatus
            {
                StoredMode = settings.Mode,
                EffectiveMode = TypedDecisionSettings.ResolveEffectiveMode(settings.Mode, keyPresent),
                EffectiveReason = keyPresent ? null : TypedDecisionKeyStore.ReasonNoKey,
                KeyPresent = keyPresent,
                KeySource = source
            };
            IEnumerable<string> names = TypedDecisionSettings.ShippedDecisionNames
                .Concat(settings.Decisions.Keys.Where(k => !TypedDecisionSettings.ShippedDecisionNames.Contains(k)).OrderBy(k => k, StringComparer.Ordinal));
            foreach (string name in names)
            {
                if (!settings.Decisions.TryGetValue(name, out TypedDecisionRuleSettings? rule) || rule == null) continue;
                status.Decisions.Add(new TypedDecisionStatusEntry
                {
                    Key = name,
                    Mode = rule.Mode,
                    Threshold = rule.GateThreshold,
                    Description = TypedDecisionCatalog.Describe(name),

                    // A decision no decision point consults reports why, so a bare mode is never read
                    // as enforcement of something that never runs.
                    UnwiredReason = TypedDecisionWiring.UnwiredReason(name)
                });
            }
            foreach (KeyValuePair<string, CustomTypedDecisionSettings> pair in settings.Custom.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (pair.Value == null) continue;
                status.Custom.Add(CustomTypedDecisionView.From(pair.Key, pair.Value));
            }
            return status;
        }

        #endregion
    }
}
