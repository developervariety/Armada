namespace Armada.Core.Settings
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Dispatch-time validation policy. Defaults are off and empty so a fresh
    /// install does not reject titles that a generic Armada user would send.
    /// </summary>
    public class VoyageDispatchSettings
    {
        #region Public-Members

        /// <summary>
        /// When true, a mission title that already opens with a configured stage-persona
        /// prefix is rejected at dispatch. Default false.
        /// </summary>
        public bool RejectStagePersonaTitlePrefixes { get; set; } = false;

        /// <summary>
        /// Title prefixes treated as materialized pipeline stage tags when
        /// <see cref="RejectStagePersonaTitlePrefixes"/> is true. Each entry is matched
        /// at the start of the trimmed title, case-insensitively. Setting this to null
        /// restores the empty default list.
        /// </summary>
        public List<string> StagePersonaTitlePrefixes
        {
            get => _StagePersonaTitlePrefixes;
            set => _StagePersonaTitlePrefixes = value ?? new List<string>();
        }

        #endregion

        #region Private-Members

        private List<string> _StagePersonaTitlePrefixes = new List<string>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with the guard off and an empty prefix list.
        /// </summary>
        public VoyageDispatchSettings()
        {
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Copy every value from another instance into this one, in place.
        /// </summary>
        /// <param name="source">Instance to copy values from. Null is ignored.</param>
        public void CopyFrom(VoyageDispatchSettings source)
        {
            if (source == null) return;
            RejectStagePersonaTitlePrefixes = source.RejectStagePersonaTitlePrefixes;
            StagePersonaTitlePrefixes = source.StagePersonaTitlePrefixes;
        }

        #endregion
    }
}
