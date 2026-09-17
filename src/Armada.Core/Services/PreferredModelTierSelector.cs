namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// Pure static helper for preferredModel tier selectors (low, mid, high). A selector names a tier floor
    /// on the captain Capability tier: low is Economy, mid is Standard, high is Premium. A captain's tier
    /// comes from its record (<see cref="CaptainTierSelector.EffectiveTier"/>); a persona flagged as a
    /// specialist always requires Premium. Literal model names pass through unchanged and are handled by
    /// <see cref="LegacyCaptainSelector"/>. Tier values are case-insensitive.
    /// </summary>
    public static class PreferredModelTierSelector
    {
        #region Public-Members

        /// <summary>Canonical tier name for low-complexity work: an Economy floor.</summary>
        public const string LowTier = "low";

        /// <summary>Canonical tier name for mid-complexity work: a Standard floor.</summary>
        public const string MidTier = "mid";

        /// <summary>Canonical tier name for high-complexity work: a Premium floor.</summary>
        public const string HighTier = "high";

        /// <summary>
        /// Capability hint requesting a captain with strong audit reasoning and
        /// cross-repository analysis fit. Maps to the AuditReasoningFit profile dimension.
        /// </summary>
        public const string AuditHint = "audit";

        /// <summary>
        /// Capability hint requesting a captain suited for reasoning-intensive tasks.
        /// Maps to the AuditReasoningFit profile dimension.
        /// </summary>
        public const string ReasoningHeavyHint = "reasoning-heavy";

        /// <summary>
        /// Capability hint requesting a captain optimized for high-volume mechanical
        /// coding work. Maps to the MechanicalThroughput profile dimension.
        /// </summary>
        public const string MechanicalHint = "mechanical";

        /// <summary>
        /// Capability hint requesting a fast, low-cost captain for documentation-only
        /// tasks. Maps to the MechanicalThroughput profile dimension.
        /// </summary>
        public const string DocOnlyHint = "doc-only";

        #endregion

        #region Private-Members

        private static readonly Dictionary<string, string> _Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "quick", MidTier },
            { "medium", MidTier }
        };

        private static readonly HashSet<string> _TierNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            LowTier, MidTier, HighTier, "quick", "medium"
        };

        private static readonly HashSet<string> _CapabilityHintNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            AuditHint, ReasoningHeavyHint, MechanicalHint, DocOnlyHint
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
        /// Returns true when the value is a recognized capability hint (audit, reasoning-heavy,
        /// mechanical, or doc-only). Matching is case-insensitive. Returns false for null, empty,
        /// whitespace, and unrecognized values.
        /// </summary>
        public static bool IsCapabilityHint(string? value)
        {
            if (String.IsNullOrWhiteSpace(value)) return false;
            return _CapabilityHintNames.Contains(value);
        }

        /// <summary>
        /// Normalizes a capability hint to its canonical lowercase form. Returns null for
        /// null, empty, whitespace, or unrecognized hints -- unrecognized hints degrade
        /// gracefully to no-hint behavior without throwing.
        /// </summary>
        public static string? NormalizeCapabilityHint(string? value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            if (String.Equals(value, AuditHint, StringComparison.OrdinalIgnoreCase)) return AuditHint;
            if (String.Equals(value, ReasoningHeavyHint, StringComparison.OrdinalIgnoreCase)) return ReasoningHeavyHint;
            if (String.Equals(value, MechanicalHint, StringComparison.OrdinalIgnoreCase)) return MechanicalHint;
            if (String.Equals(value, DocOnlyHint, StringComparison.OrdinalIgnoreCase)) return DocOnlyHint;
            return null;
        }

        /// <summary>
        /// Returns true when the persona is in the supplied specialist set.
        /// Worker and null personas return false. When <paramref name="specialistPersonas"/>
        /// is null or empty, no persona is reserved for high: product defaults are empty.
        /// </summary>
        /// <param name="persona">Persona name to test.</param>
        /// <param name="specialistPersonas">Optional override set; null uses the built-in default.</param>
        public static bool RequiresHighTier(string? persona, IReadOnlyCollection<string>? specialistPersonas = null)
        {
            return IsSpecialistPersona(persona, specialistPersonas);
        }

        /// <summary>
        /// Returns a PreferredModel value safe to store on a mission with the given persona.
        /// For specialist personas (Judge, Architect, etc.) this upgrades any tier selector
        /// below "high" to "high". Null/empty preferredModel becomes "high" when the persona
        /// requires it; literal model names are passed through unchanged (operator-pinned
        /// literals stay honest -- the dispatcher's tier-fallback handles the runtime case
        /// if no captain matches). When <paramref name="specialistPersonas"/> is null or
        /// empty, no persona is treated as a specialist.
        /// </summary>
        /// <param name="preferredModel">Requested tier selector or literal model name.</param>
        /// <param name="persona">Persona the mission requires.</param>
        /// <param name="specialistPersonas">Optional specialist set; null or empty treats no persona as a specialist.</param>
        public static string? EnforceHighTierForPersona(
            string? preferredModel,
            string? persona,
            IReadOnlyCollection<string>? specialistPersonas = null)
        {
            if (!RequiresHighTier(persona, specialistPersonas)) return preferredModel;
            if (String.IsNullOrWhiteSpace(preferredModel)) return HighTier;
            if (IsTierSelector(preferredModel))
            {
                if (String.Equals(NormalizeTier(preferredModel), HighTier, StringComparison.OrdinalIgnoreCase))
                    return preferredModel;
                return HighTier;
            }
            return preferredModel;
        }

        /// <summary>
        /// Returns a PreferredModel value safe to store on a mission whose persona may differ
        /// from the mission the value was inherited from. This is the two-way counterpart to
        /// <see cref="EnforceHighTierForPersona"/>: it upgrades to "high" for specialist
        /// personas exactly as that method does, and it additionally caps a "high" selector
        /// down to "mid" for a persona that is not a specialist.
        ///
        /// The cap matters because the high tier is reserved for specialist and reviewer
        /// personas. A non-specialist persona carrying "high" is unassignable whenever the
        /// captain roster has no high-tier captain that accepts that persona: the mission
        /// waits at WaitingForIdleCaptain forever while captains sit Idle, which reads as a
        /// capacity problem and is not one. Inheriting a tier across a persona change is the
        /// way that state is normally reached.
        ///
        /// Literal model names are passed through unchanged so operator-pinned literals stay
        /// honest, and a null or empty value is only filled in when the persona requires high.
        /// </summary>
        /// <param name="preferredModel">Requested tier selector or literal model name.</param>
        /// <param name="persona">Persona the mission will actually run as.</param>
        /// <param name="specialistPersonas">Optional specialist set; null or empty treats no persona as a specialist.</param>
        public static string? ResolveTierForPersona(
            string? preferredModel,
            string? persona,
            IReadOnlyCollection<string>? specialistPersonas = null)
        {
            if (RequiresHighTier(persona, specialistPersonas))
                return EnforceHighTierForPersona(preferredModel, persona, specialistPersonas);

            if (String.IsNullOrWhiteSpace(preferredModel)) return preferredModel;
            if (!IsTierSelector(preferredModel)) return preferredModel;
            if (String.Equals(NormalizeTier(preferredModel), HighTier, StringComparison.OrdinalIgnoreCase))
                return MidTier;

            return preferredModel;
        }

        /// <summary>
        /// Resolves the PreferredModel a pipeline stage should persist: a stage override,
        /// else mission inheritance, then persona-aware tier normalization via
        /// <see cref="ResolveTierForPersona"/>.
        /// </summary>
        /// <param name="stagePreferredModel">Optional stage-level override.</param>
        /// <param name="missionPreferredModel">Optional per-mission value inherited when the stage override is null.</param>
        /// <param name="persona">Persona the mission will actually run as.</param>
        /// <param name="specialistPersonas">Optional specialist set; null or empty treats no persona as a specialist.</param>
        public static string? ResolveEffectivePreferredModel(
            string? stagePreferredModel,
            string? missionPreferredModel,
            string? persona,
            IReadOnlyCollection<string>? specialistPersonas = null)
        {
            return ResolveTierForPersona(stagePreferredModel ?? missionPreferredModel, persona, specialistPersonas);
        }

        /// <summary>
        /// Normalizes a tier selector value to its canonical form (low, mid or high). The <c>quick</c> and
        /// <c>medium</c> aliases map to mid.
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
            if (_Aliases.TryGetValue(value, out alias)) return NormalizeTier(alias);

            throw new ArgumentException("Unknown tier selector: " + value, nameof(value));
        }

        /// <summary>
        /// The Capability tier floor a tier selector names: low is Economy, mid is Standard, high is Premium.
        /// </summary>
        /// <param name="tierSelector">Tier selector value (low, mid, high, or an alias).</param>
        /// <returns>The tier floor.</returns>
        public static CaptainTierEnum FloorOf(string tierSelector)
        {
            string normalized = NormalizeTier(tierSelector);
            if (normalized == LowTier) return CaptainTierEnum.Economy;
            if (normalized == MidTier) return CaptainTierEnum.Standard;
            return CaptainTierEnum.Premium;
        }

        /// <summary>
        /// The tiers a mission may land on, in the order they are tried. A specialist persona lands only on
        /// Premium. With a floor, the floor is tried first and every tier above it follows, lowest first, so a
        /// stronger captain is not consumed while one at the floor is idle. Without a floor, Standard is tried
        /// first, then Premium, then Economy.
        /// </summary>
        /// <param name="floor">The mission's tier floor, or null when it names none.</param>
        /// <param name="isSpecialist">True when the mission's persona is a specialist.</param>
        /// <returns>The ordered tiers.</returns>
        public static List<CaptainTierEnum> TierOrder(CaptainTierEnum? floor, bool isSpecialist)
        {
            if (isSpecialist) return new List<CaptainTierEnum> { CaptainTierEnum.Premium };
            if (!floor.HasValue) return new List<CaptainTierEnum> { CaptainTierEnum.Standard, CaptainTierEnum.Premium, CaptainTierEnum.Economy };
            List<CaptainTierEnum> order = new List<CaptainTierEnum>();
            foreach (CaptainTierEnum tier in new[] { CaptainTierEnum.Economy, CaptainTierEnum.Standard, CaptainTierEnum.Premium })
                if (tier >= floor.Value) order.Add(tier);
            return order;
        }

        /// <summary>
        /// Returns true when the captain's effective tier is at or above the floor the tier selector names.
        /// </summary>
        /// <param name="captain">Captain to test.</param>
        /// <param name="tierSelector">Tier selector value (low, mid, high, or an alias).</param>
        /// <returns>True when the captain may serve the tier.</returns>
        public static bool CaptainMatchesTierOrAbove(Captain captain, string tierSelector)
        {
            if (captain == null) return false;
            return CaptainTierSelector.EffectiveTier(captain) >= FloorOf(tierSelector);
        }

        /// <summary>
        /// The tier floor a concrete model pin implies when no idle captain runs the model: the tier of the
        /// captains in the roster that run it, else the built-in model family classification. Null when neither
        /// knows the model, so an unknown model sets no floor.
        /// </summary>
        /// <param name="model">Concrete model name.</param>
        /// <param name="settings">Settings whose record snapshot is read.</param>
        /// <returns>The tier, or null.</returns>
        public static CaptainTierEnum? TierOfModel(string? model, ModelTierSettings? settings)
        {
            CaptainTierEnum? roster = settings?.Records.TierOfModel(model);
            return roster ?? CaptainTierSelector.ClassifyKnownFamily(model);
        }

        #endregion

        #region Private-Methods

        private static bool IsSpecialistPersona(string? persona, IReadOnlyCollection<string>? specialistPersonas)
        {
            if (String.IsNullOrWhiteSpace(persona)) return false;
            if (specialistPersonas == null) return false;
            string normalized = PersonaCatalog.NormalizeName(persona);
            foreach (string specialist in specialistPersonas)
            {
                if (String.Equals(PersonaCatalog.NormalizeName(specialist), normalized, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        #endregion
    }
}
