namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// The fields a caller may set when creating or updating a prompt template. Every surface deserializes its
    /// input into this one allow-list, so identifiers, ownership, built-in status and timestamps can never come
    /// from a request body. On update a field left out keeps its stored value.
    /// </summary>
    public class PromptTemplateWriteRequest
    {
        #region Public-Members

        /// <summary>
        /// Template name, trimmed. Required on create; an update is addressed by name and never renames.
        /// </summary>
        public string? Name { get; set; } = null;

        /// <summary>
        /// Category such as persona or mission, trimmed. Required on create; null leaves it unchanged on update.
        /// </summary>
        public string? Category { get; set; } = null;

        /// <summary>
        /// Template content. Required and non-blank on create; null leaves it unchanged on update.
        /// </summary>
        public string? Content { get; set; } = null;

        /// <summary>
        /// Description, trimmed. Null leaves it unchanged on update; an empty string clears it.
        /// </summary>
        public string? Description { get; set; } = null;

        /// <summary>
        /// Whether the template is active. Null leaves it unchanged (true for a new template).
        /// </summary>
        public bool? Active { get; set; } = null;

        /// <summary>
        /// Requested ownership scope on create. An administrator's choice is kept (tenant-wide when omitted);
        /// any other caller's record is always user-specific. Ignored on update.
        /// </summary>
        public OwnershipScopeEnum? OwnershipScope { get; set; } = null;

        #endregion
    }
}
