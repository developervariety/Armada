namespace Armada.Core.Settings
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// An extra pipeline seeded from settings on startup. Built-in product pipelines
    /// stay in code; deployment-specific specialist pipelines belong here.
    /// </summary>
    public class AdditionalPipelineSettings
    {
        #region Public-Members

        /// <summary>
        /// Pipeline name, for example <c>DiagnosticProtocolTested</c>.
        /// </summary>
        public string Name
        {
            get => _Name;
            set => _Name = value ?? String.Empty;
        }

        /// <summary>
        /// Pipeline description stored on the record.
        /// </summary>
        public string Description
        {
            get => _Description;
            set => _Description = value ?? String.Empty;
        }

        /// <summary>
        /// Ordered stages. Setting this to null restores an empty list.
        /// </summary>
        public List<AdditionalPipelineStageSettings> Stages
        {
            get => _Stages;
            set => _Stages = value ?? new List<AdditionalPipelineStageSettings>();
        }

        #endregion

        #region Private-Members

        private string _Name = String.Empty;
        private string _Description = String.Empty;
        private List<AdditionalPipelineStageSettings> _Stages = new List<AdditionalPipelineStageSettings>();

        #endregion
    }
}
