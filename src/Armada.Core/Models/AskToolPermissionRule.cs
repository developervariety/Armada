namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>A remembered decision for one native tool in one Ask thread.</summary>
    public sealed class AskToolPermissionRule
    {
        /// <summary>Rule identifier (atr_ prefix).</summary>
        public string Id { get; set; } = Constants.IdGenerator.GenerateKSortable(Constants.AskToolPermissionRuleIdPrefix, 24);
        /// <summary>Owning tenant.</summary>
        public string TenantId { get; set; } = string.Empty;
        /// <summary>Owning user.</summary>
        public string UserId { get; set; } = string.Empty;
        /// <summary>Thread the rule applies to.</summary>
        public string ThreadId { get; set; } = string.Empty;
        /// <summary>Native tool name the rule matches.</summary>
        public string ToolName { get; set; } = string.Empty;
        /// <summary>Allow or deny.</summary>
        public AskToolPermissionRuleActionEnum Action { get; set; } = AskToolPermissionRuleActionEnum.Allow;
        /// <summary>When the rule was remembered.</summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    }
}
