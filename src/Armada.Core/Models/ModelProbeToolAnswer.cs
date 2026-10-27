namespace Armada.Core.Models
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// The tool-calling probe's answer block, as the model writes it.
    /// </summary>
    public class ModelProbeToolAnswer
    {
        #region Public-Members

        /// <summary>
        /// SHA-256 of the single-call text.
        /// </summary>
        [JsonPropertyName("single")]
        public string? Single { get; set; } = null;

        /// <summary>
        /// SHA-256 of the file written in the sequential step.
        /// </summary>
        [JsonPropertyName("sequential")]
        public string? Sequential { get; set; } = null;

        /// <summary>
        /// SHA-256 of the first parallel text.
        /// </summary>
        [JsonPropertyName("parallelA")]
        public string? ParallelA { get; set; } = null;

        /// <summary>
        /// SHA-256 of the second parallel text.
        /// </summary>
        [JsonPropertyName("parallelB")]
        public string? ParallelB { get; set; } = null;

        /// <summary>
        /// Whether the tool reported an error for the missing file.
        /// </summary>
        [JsonPropertyName("missingFileError")]
        public bool? MissingFileError { get; set; } = null;

        /// <summary>
        /// The error text the tool returned.
        /// </summary>
        [JsonPropertyName("missingFileErrorText")]
        public string? MissingFileErrorText { get; set; } = null;

        /// <summary>
        /// The requested line of the large file.
        /// </summary>
        [JsonPropertyName("largeLine")]
        public string? LargeLine { get; set; } = null;

        /// <summary>
        /// Lines the model read from the large file.
        /// </summary>
        [JsonPropertyName("largeLineCount")]
        public int? LargeLineCount { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public ModelProbeToolAnswer()
        {
        }

        #endregion
    }
}
