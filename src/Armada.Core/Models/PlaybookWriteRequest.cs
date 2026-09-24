namespace Armada.Core.Models
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// The fields a caller may set when creating or updating a playbook. Every surface deserializes its input
    /// into this one allow-list, so identifiers, ownership and timestamps can never come from a request body.
    /// On update a field left out keeps its stored value.
    /// </summary>
    public class PlaybookWriteRequest
    {
        #region Public-Members

        /// <summary>
        /// Markdown file name, ending in .md and unique inside the tenant. Required on create.
        /// </summary>
        public string? FileName { get; set; } = null;

        /// <summary>
        /// Markdown content. Required and non-blank on create; null leaves it unchanged on update.
        /// </summary>
        public string? Content { get; set; } = null;

        /// <summary>
        /// Description. Omitted leaves it unchanged; null or an empty string clears it.
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
        [JsonIgnore]
        public bool DescriptionSupplied { get; private set; } = false;

        /// <summary>
        /// Whether the playbook is active. Null leaves it unchanged (true for a new playbook).
        /// </summary>
        public bool? Active { get; set; } = null;

        #endregion

        #region Private-Members

        private string? _Description = null;

        #endregion
    }
}
