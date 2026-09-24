namespace Armada.Core.Models
{
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// The fields a caller may set when creating or updating a pipeline. Every surface deserializes its
    /// input into this one allow-list, so ownership, built-in status, identifiers and timestamps can never
    /// come from a request body.
    /// </summary>
    public class PipelineWriteRequest
    {
        #region Public-Members

        /// <summary>
        /// Pipeline name. Required on create; an update is addressed by name and never renames.
        /// </summary>
        public string? Name { get; set; } = null;

        /// <summary>
        /// Description. Null leaves it unchanged on update.
        /// </summary>
        public string? Description { get; set; } = null;

        /// <summary>
        /// Ordered stages. Required and non-empty on create; on update null leaves the stages unchanged and a
        /// non-empty list replaces them. An empty list is refused.
        /// </summary>
        public List<PipelineStageWriteRequest>? Stages { get; set; } = null;

        /// <summary>
        /// Whether the pipeline is active. Null leaves it unchanged (true for a new pipeline).
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
