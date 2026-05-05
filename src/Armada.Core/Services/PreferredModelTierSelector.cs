namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// Pure static helper that maps preferredModel tier values (low/mid/high) to
    /// a concrete model name by randomly selecting across eligible peer models.
    /// Tier values are case-insensitive. Literal model names pass through unchanged
    /// and are handled by the calling dispatcher.
    /// </summary>
    public static class PreferredModelTierSelector
    {
        #region Public-Members

        /// <summary>Canonical tier name for low-complexity work.</summary>
        public const string LowTier = "low";

        /// <summary>Canonical tier name for mid-complexity work.</summary>
        public const string MidTier = "mid";

        /// <summary>Canonical tier name for high-complexity work.</summary>
        public const string HighTier = "high";

        #endregion

        #region Private-Members

        private static readonly string[] _LowModels = new string[] { "kimi-k2.5" };

        private static readonly string[] _MidModels = new string[]
        {
            "composer-2-fast",
            "claude-sonnet-4-6",
            "gemini-3.5-pro",
            "claude-4.6-sonnet-medium",
            "claude-4.6-sonnet-medium-thinking",
            "gemini-3.1-pro",
        };

        private static readonly string[] _HighModels = new string[]
        {
            "claude-opus-4-7",
            "gpt-5.5",
            "claude-opus-4-7-high",
            "claude-opus-4-7-max",
            "claude-opus-4-7-thinking-high",
            "claude-opus-4-7-thinking-xhigh",
            "claude-opus-4-7-thinking-max",
            "claude-4.6-opus-high",
            "claude-4.6-opus-max",
            "claude-4.6-opus-high-thinking",
            "claude-4.6-opus-max-thinking",
        };

        private static readonly Dictionary<string, string> _Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "quick", LowTier },
            { "medium", MidTier }
        };

        private static readonly HashSet<string> _TierNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            LowTier, MidTier, HighTier, "quick", "medium"
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Returns true if the value is a recognized tier selector (low, mid, high, or an alias).
        /// Returns false for null, empty, and literal model names.
        /// </summary>
        public static bool IsTierSelector(string? value)
        {
            if (String.IsNullOrWhiteSpace(value)) return false;
            return _TierNames.Contains(value);
        }

        /// <summary>
        /// Normalizes a tier selector value to its canonical form (low, mid, or high).
        /// Throws <see cref="ArgumentException"/> if value is not a tier selector.
        /// </summary>
        public static string NormalizeTier(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Tier value must not be null or empty.", nameof(value));

            if (String.Equals(value, LowTier, StringComparison.OrdinalIgnoreCase)) return LowTier;
            if (String.Equals(value, MidTier, StringComparison.OrdinalIgnoreCase)) return MidTier;
            if (String.Equals(value, HighTier, StringComparison.OrdinalIgnoreCase)) return HighTier;

            string? alias = null;
            if (_Aliases.TryGetValue(value, out alias)) return alias;

            throw new ArgumentException("Unknown tier selector: " + value, nameof(value));
        }

        /// <summary>
        /// Returns the model names for the specified tier (low, mid, or high).
        /// </summary>
        public static IReadOnlyList<string> GetTierModels(string tier)
        {
            string normalized = NormalizeTier(tier);
            if (normalized == LowTier) return _LowModels;
            if (normalized == MidTier) return _MidModels;
            return _HighModels;
        }

        /// <summary>
        /// Returns all model names in the specified tier and every tier above it.
        /// Used to validate whether a pinned captain's model is acceptable for the
        /// requested tier or its upward fallback chain.
        /// </summary>
        public static IReadOnlyList<string> GetTierAndAboveModels(string tier)
        {
            string normalized = NormalizeTier(tier);
            List<string> result = new List<string>();
            if (normalized == LowTier)
            {
                result.AddRange(_LowModels);
                result.AddRange(_MidModels);
                result.AddRange(_HighModels);
            }
            else if (normalized == MidTier)
            {
                result.AddRange(_MidModels);
                result.AddRange(_HighModels);
            }
            else
            {
                result.AddRange(_HighModels);
            }
            return result;
        }

        /// <summary>
        /// Selects a concrete model name from the requested tier (or the next available
        /// tier upward) based on which idle captains are eligible for the given persona.
        /// </summary>
        /// <param name="tierValue">Tier selector value (low, mid, high, or alias).</param>
        /// <param name="idleCaptains">All currently idle captains.</param>
        /// <param name="persona">Optional persona name the mission requires.</param>
        /// <param name="randomPick">
        /// Delegate that accepts an exclusive upper bound and returns a random index in
        /// [0, upperBound). Inject a deterministic function in tests.
        /// </param>
        /// <returns>
        /// A model name string if an eligible model was found, or null if no idle captain
        /// with a tier model is available (mission stays Pending).
        /// </returns>
        public static string? SelectModel(
            string tierValue,
            IReadOnlyList<Captain> idleCaptains,
            string? persona,
            Func<int, int> randomPick)
        {
            if (randomPick == null)
                throw new ArgumentNullException(nameof(randomPick));

            string normalized = NormalizeTier(tierValue);

            // Build the ordered list of tiers to try (requested tier, then upward only)
            string[] tierOrder;
            if (normalized == LowTier)
                tierOrder = new string[] { LowTier, MidTier, HighTier };
            else if (normalized == MidTier)
                tierOrder = new string[] { MidTier, HighTier };
            else
                tierOrder = new string[] { HighTier };

            foreach (string tier in tierOrder)
            {
                IReadOnlyList<string> tierModels = GetTierModels(tier);
                List<string> eligibleModels = new List<string>();

                foreach (string model in tierModels)
                {
                    if (HasEligibleCaptain(model, idleCaptains, persona))
                        eligibleModels.Add(model);
                }

                if (eligibleModels.Count > 0)
                {
                    int idx = randomPick(eligibleModels.Count);
                    return eligibleModels[idx];
                }
            }

            return null;
        }

        #endregion

        #region Private-Methods

        private static bool HasEligibleCaptain(string model, IReadOnlyList<Captain> idleCaptains, string? persona)
        {
            foreach (Captain captain in idleCaptains)
            {
                if (String.IsNullOrEmpty(captain.Model))
                    continue;
                if (!String.Equals(captain.Model, model, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (IsPersonaEligible(captain, persona))
                    return true;
            }
            return false;
        }

        private static bool IsPersonaEligible(Captain captain, string? persona)
        {
            if (String.IsNullOrEmpty(persona)) return true;
            if (String.IsNullOrEmpty(captain.AllowedPersonas)) return true;
            return captain.AllowedPersonas.Contains("\"" + persona + "\"", StringComparison.OrdinalIgnoreCase);
        }

        #endregion
    }
}
