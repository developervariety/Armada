namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// One stage of a pipeline create or update request. A field left out of an update keeps the value of
    /// the existing stage for the same persona; an explicit null clears a text field.
    /// </summary>
    public class PipelineStageWriteRequest
    {
        #region Public-Members

        /// <summary>
        /// Persona name for this stage. Required.
        /// </summary>
        public string? PersonaName { get; set; } = null;

        /// <summary>
        /// Execution order. Give every stage a positive order (stages that share one run as parallel siblings)
        /// or give none, and list position numbers the stages 1..n. A list that orders only some stages is refused.
        /// </summary>
        public int? Order { get; set; } = null;

        /// <summary>
        /// Whether the stage is optional. Null leaves it unchanged (false for a new stage).
        /// </summary>
        public bool? IsOptional { get; set; } = null;

        /// <summary>
        /// Whether the stage requires an explicit review approval. Null leaves it unchanged (false for a new stage).
        /// </summary>
        public bool? RequiresReview { get; set; } = null;

        /// <summary>
        /// Action when the review gate is denied. Null leaves it unchanged (RetryStage for a new stage).
        /// </summary>
        public ReviewDenyActionEnum? ReviewDenyAction { get; set; } = null;

        /// <summary>
        /// Stage description.
        /// </summary>
        public string? Description
        {
            get => _Description;
            set
            {
                _Description = value;
                DescriptionSupplied = true;
            }
        }

        /// <summary>
        /// True when the request named <see cref="Description"/>, including an explicit null.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool DescriptionSupplied { get; private set; } = false;

        /// <summary>
        /// Per-stage complexity tier or model pin.
        /// </summary>
        public string? PreferredModel
        {
            get => _PreferredModel;
            set
            {
                _PreferredModel = value;
                PreferredModelSupplied = true;
            }
        }

        /// <summary>
        /// True when the request named <see cref="PreferredModel"/>, including an explicit null.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool PreferredModelSupplied { get; private set; } = false;

        #endregion

        #region Private-Members

        private string? _Description = null;
        private string? _PreferredModel = null;

        #endregion
    }
}
