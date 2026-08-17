namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// A per-persona captain override selected at dispatch time. Binds a pipeline step (identified by its
    /// persona name) to a preferred captain and a fallback capability tier. When the preferred captain is
    /// idle, dispatch assigns it; when it is busy, dispatch falls back to an idle captain at or above
    /// <see cref="FallbackTier"/>. Applies to every mission of the persona in the voyage, including fan-out
    /// missions produced by an Architect stage.
    /// </summary>
    public class CaptainAssignmentOverride
    {
        #region Public-Members

        /// <summary>
        /// Persona name the override applies to (for example "Worker", "Architect", "Judge",
        /// "Test Engineer"). Required and non-empty.
        /// </summary>
        public string Persona
        {
            get => _Persona;
            set
            {
                if (String.IsNullOrEmpty(value)) throw new ArgumentNullException(nameof(Persona));
                _Persona = value;
            }
        }

        /// <summary>
        /// Preferred captain identifier (cpt_ prefix), or null to leave the persona on normal routing while
        /// still supplying a <see cref="FallbackTier"/>.
        /// </summary>
        public string? CaptainId { get; set; } = null;

        /// <summary>
        /// Fallback capability tier used when the preferred captain is busy. Null means the preferred
        /// captain's own tier is used as the fallback (resolved at dispatch), or normal routing when no
        /// preferred captain is set.
        /// </summary>
        public CaptainTierEnum? FallbackTier { get; set; } = null;

        #endregion

        #region Private-Members

        private string _Persona = "Worker";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with defaults.
        /// </summary>
        public CaptainAssignmentOverride()
        {
        }

        /// <summary>
        /// Instantiate with a persona, preferred captain, and fallback tier.
        /// </summary>
        /// <param name="persona">Persona name the override applies to.</param>
        /// <param name="captainId">Preferred captain identifier, or null.</param>
        /// <param name="fallbackTier">Fallback capability tier, or null.</param>
        public CaptainAssignmentOverride(string persona, string? captainId, CaptainTierEnum? fallbackTier)
        {
            Persona = persona;
            CaptainId = captainId;
            FallbackTier = fallbackTier;
        }

        #endregion
    }
}
