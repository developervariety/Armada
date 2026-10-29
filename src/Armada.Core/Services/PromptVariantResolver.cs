namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Chooses the persona prompt variant (<see cref="PromptVariantEnum"/>) a mission renders with. Order: the voyage's
    /// captain override for the mission's persona (an exact persona match, then the wildcard <c>*</c>), then the
    /// assigned captain's <c>promptVariant</c> runtime option (<see cref="CaptainPromptOptions"/>), then
    /// <see cref="PromptVariantEnum.Current"/>. An override or option without a variant does not count.
    /// </summary>
    public static class PromptVariantResolver
    {
        #region Public-Methods

        /// <summary>
        /// Resolve the prompt variant for a mission.
        /// </summary>
        /// <param name="persona">Mission persona, or null for Worker.</param>
        /// <param name="voyageOverrides">The voyage's captain overrides, or null.</param>
        /// <param name="captain">The assigned captain, or null.</param>
        /// <returns>The variant to render.</returns>
        public static PromptVariantEnum Resolve(string? persona, IEnumerable<CaptainAssignmentOverride>? voyageOverrides, Captain? captain)
        {
            PromptVariantEnum? fromVoyage = FromVoyageOverrides(persona, voyageOverrides);
            if (fromVoyage.HasValue) return fromVoyage.Value;
            return FromCaptain(captain) ?? PromptVariantEnum.Current;
        }

        /// <summary>
        /// The variant a voyage's captain overrides choose for a persona: the first exact persona match that sets a
        /// variant, else the first wildcard (<c>*</c> or empty persona) that sets one, else null.
        /// </summary>
        /// <param name="persona">Mission persona, or null for Worker.</param>
        /// <param name="voyageOverrides">The voyage's captain overrides, or null.</param>
        /// <returns>The chosen variant, or null.</returns>
        public static PromptVariantEnum? FromVoyageOverrides(string? persona, IEnumerable<CaptainAssignmentOverride>? voyageOverrides)
        {
            if (voyageOverrides == null) return null;
            string routingPersona = String.IsNullOrWhiteSpace(persona) ? PersonaCatalog.Worker : persona.Trim();

            PromptVariantEnum? wildcard = null;
            foreach (CaptainAssignmentOverride ov in voyageOverrides)
            {
                if (ov == null || !ov.PromptVariant.HasValue || !Enum.IsDefined(ov.PromptVariant.Value)) continue;
                if (String.IsNullOrEmpty(ov.Persona) || ov.Persona == "*")
                {
                    if (!wildcard.HasValue) wildcard = ov.PromptVariant;
                    continue;
                }

                if (PersonaCatalog.Matches(ov.Persona, routingPersona)) return ov.PromptVariant;
            }

            return wildcard;
        }

        /// <summary>
        /// The variant set in a captain's runtime options (<c>promptVariant</c>), or null when the captain is null, sets
        /// none, or its options are unreadable.
        /// </summary>
        /// <param name="captain">Captain, or null.</param>
        /// <returns>The captain's variant, or null.</returns>
        public static PromptVariantEnum? FromCaptain(Captain? captain)
        {
            if (captain == null || String.IsNullOrWhiteSpace(captain.RuntimeOptionsJson)) return null;
            try
            {
                PromptVariantEnum? variant = CaptainRuntimeOptions.Deserialize<CaptainPromptOptions>(captain.RuntimeOptionsJson)?.PromptVariant;
                if (variant.HasValue && !Enum.IsDefined(variant.Value)) return null;
                return variant;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Parse a variant name (case-insensitive: Current, Lean or Compact).
        /// </summary>
        /// <param name="value">Variant name, or null.</param>
        /// <returns>The variant, or null when the value is null or whitespace.</returns>
        /// <exception cref="ArgumentException">Thrown when the value names no variant.</exception>
        public static PromptVariantEnum? Parse(string? value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            string trimmed = value.Trim();
            foreach (PromptVariantEnum variant in Enum.GetValues<PromptVariantEnum>())
            {
                if (String.Equals(variant.ToString(), trimmed, StringComparison.OrdinalIgnoreCase)) return variant;
            }

            throw new ArgumentException("promptVariant must be Current, Lean or Compact.", nameof(value));
        }

        #endregion
    }
}
