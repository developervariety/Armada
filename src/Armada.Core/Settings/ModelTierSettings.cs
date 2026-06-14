namespace Armada.Core.Settings
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Settings that govern how captain model tiers are reserved across personas.
    /// Specialist personas (reviewers, Judge, Architect, etc.) are reserved for
    /// high-tier captains; non-specialist work runs on mid/low tiers with high held
    /// back as a last resort. The specialist set is configurable so operators can
    /// reclassify a persona without a code change.
    /// </summary>
    public class ModelTierSettings
    {
        #region Public-Members

        /// <summary>
        /// Persona names that are reserved for high-tier captains. A mission whose
        /// persona is in this set is routed only to high-tier captains; all other
        /// personas prefer mid/low and fall up to high only when no mid/low captain
        /// is available. Setting this to null restores the built-in default set.
        /// </summary>
        public List<string> SpecialistPersonas
        {
            get => _SpecialistPersonas;
            set => _SpecialistPersonas = value ?? BuildDefaultSpecialistPersonas();
        }

        #endregion

        #region Private-Members

        private List<string> _SpecialistPersonas = BuildDefaultSpecialistPersonas();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with the built-in default specialist persona set.
        /// </summary>
        public ModelTierSettings()
        {
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Returns true when the supplied persona is reserved for high-tier captains.
        /// Matching is case-insensitive. Null, empty, or whitespace personas return false.
        /// </summary>
        /// <param name="persona">Persona name to test.</param>
        public bool IsSpecialistPersona(string? persona)
        {
            if (String.IsNullOrWhiteSpace(persona)) return false;
            foreach (string specialist in _SpecialistPersonas)
            {
                if (String.Equals(specialist, persona, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        #endregion

        #region Private-Methods

        private static List<string> BuildDefaultSpecialistPersonas()
        {
            return new List<string>
            {
                "Judge",
                "Architect",
                "TestEngineer",
                "DiagnosticProtocolReviewer",
                "TenantSecurityReviewer",
                "MigrationDataReviewer",
                "PerformanceMemoryReviewer",
                "PortingReferenceAnalyst",
                "FrontendWorkflowReviewer",
                "MemoryConsolidator"
            };
        }

        #endregion
    }
}
