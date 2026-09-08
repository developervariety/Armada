namespace Armada.Core.Settings
{
    using System;

    /// <summary>
    /// One stage of an <see cref="AdditionalPipelineSettings"/> pipeline.
    /// </summary>
    public class AdditionalPipelineStageSettings
    {
        #region Public-Members

        /// <summary>
        /// Execution order within the pipeline (1-based).
        /// </summary>
        public int Order { get; set; } = 1;

        /// <summary>
        /// Persona name for this stage.
        /// </summary>
        public string PersonaName
        {
            get => _PersonaName;
            set => _PersonaName = value ?? String.Empty;
        }

        /// <summary>
        /// Optional preferred-model tier or literal for this stage.
        /// </summary>
        public string? PreferredModel { get; set; }

        #endregion

        #region Private-Members

        private string _PersonaName = String.Empty;

        #endregion
    }
}
