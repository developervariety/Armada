namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// A rule that assigns a suspected cause to a stage attempt that fell short. Rules are evaluated in ascending Priority; the first enabled rule that matches wins. Built-in rules ship as defaults and can be edited or disabled but not deleted.
    /// </summary>
    public class FailureCauseRule
    {
        #region Public-Members

        /// <summary>
        /// Unique identifier (fcr_ prefix).
        /// </summary>
        public string Id
        {
            get => _Id;
            set => _Id = String.IsNullOrEmpty(value) ? throw new ArgumentNullException(nameof(Id)) : value;
        }

        /// <summary>
        /// Tenant the rule applies to; null applies to every tenant.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// Short name, unique per tenant.
        /// </summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// What the rule recognizes and why.
        /// </summary>
        public string? Description { get; set; } = null;

        /// <summary>
        /// Evaluation order, lowest first.
        /// </summary>
        public int Priority { get; set; } = 100;

        /// <summary>
        /// Whether the rule is evaluated.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Whether Armada ships the rule (it can be edited or disabled, not deleted).
        /// </summary>
        public bool IsBuiltIn { get; set; } = false;

        /// <summary>
        /// What the rule matches.
        /// </summary>
        public FailureCauseRuleMatch Match { get; set; } = new FailureCauseRuleMatch();

        /// <summary>
        /// Cause assigned on a match.
        /// </summary>
        public FailureCauseEnum Cause { get; set; } = FailureCauseEnum.Unknown;

        /// <summary>
        /// User who created the rule, or null for a built-in.
        /// </summary>
        public string? CreatedByUserId { get; set; } = null;

        /// <summary>
        /// UTC creation time.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// UTC last update time.
        /// </summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;

        #endregion

        #region Private-Members

        private string _Id = Constants.IdGenerator.GenerateKSortable(Constants.FailureCauseRuleIdPrefix, 24);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public FailureCauseRule()
        {
        }

        #endregion
    }
}
