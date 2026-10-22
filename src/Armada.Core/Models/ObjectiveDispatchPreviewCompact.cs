namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Enums;

    /// <summary>
    /// The blocking part of a dispatch preview: readiness, the Error issues and the failed preflight facts. An
    /// audit that previews many objectives reads this shape instead of the full preview.
    /// </summary>
    public class ObjectiveDispatchPreviewCompact
    {
        #region Public-Members

        /// <summary>
        /// Objective identifier.
        /// </summary>
        public string ObjectiveId { get; set; } = String.Empty;

        /// <summary>
        /// True when the preview found no blocking issue.
        /// </summary>
        public bool IsReady { get; set; }

        /// <summary>
        /// The preview's Error issues, in preview order.
        /// </summary>
        public List<ObjectiveDispatchPreviewIssue> Errors { get; set; } = new List<ObjectiveDispatchPreviewIssue>();

        /// <summary>
        /// The preflight facts whose status is Fail, in preview order.
        /// </summary>
        public List<ObjectiveDispatchPreflightFact> FailedFacts { get; set; } = new List<ObjectiveDispatchPreflightFact>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Build the compact shape of a full preview.
        /// </summary>
        /// <param name="preview">The full preview.</param>
        /// <returns>The compact preview.</returns>
        public static ObjectiveDispatchPreviewCompact From(ObjectiveDispatchPreview preview)
        {
            if (preview == null) throw new ArgumentNullException(nameof(preview));
            return new ObjectiveDispatchPreviewCompact
            {
                ObjectiveId = preview.ObjectiveId,
                IsReady = preview.IsReady,
                Errors = (preview.Issues ?? new List<ObjectiveDispatchPreviewIssue>())
                    .Where(issue => issue != null && issue.Severity == ReadinessSeverityEnum.Error)
                    .ToList(),
                FailedFacts = (preview.Preflight?.Facts ?? new List<ObjectiveDispatchPreflightFact>())
                    .Where(fact => fact != null && fact.Status == PreflightFactStatusEnum.Fail)
                    .ToList()
            };
        }

        #endregion
    }
}
