namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>One native tool call that an attended Ask turn asked the thread owner to allow.</summary>
    public sealed class AskToolPermissionRequest
    {
        /// <summary>Request identifier (atp_ prefix).</summary>
        public string Id { get; set; } = Constants.IdGenerator.GenerateKSortable(Constants.AskToolPermissionRequestIdPrefix, 24);
        /// <summary>Owning tenant.</summary>
        public string TenantId { get; set; } = string.Empty;
        /// <summary>Owning user.</summary>
        public string UserId { get; set; } = string.Empty;
        /// <summary>Thread whose turn raised the request.</summary>
        public string ThreadId { get; set; } = string.Empty;
        /// <summary>Turn request identifier that raised the request.</summary>
        public string RequestId { get; set; } = string.Empty;
        /// <summary>Native tool name, for example Bash or Edit.</summary>
        public string ToolName { get; set; } = string.Empty;
        /// <summary>The tool input as JSON, cut to the storage limit.</summary>
        public string InputJson { get; set; } = "{}";
        /// <summary>The runtime's identifier for the tool call, when it sends one.</summary>
        public string? ToolUseId { get; set; }
        /// <summary>Current state.</summary>
        public AskToolPermissionStateEnum State { get; set; } = AskToolPermissionStateEnum.Pending;
        /// <summary>What decided the request, once it is decided.</summary>
        public AskToolPermissionSourceEnum? DecisionSource { get; set; }
        /// <summary>Rule that decided the request, when a rule did.</summary>
        public string? RuleId { get; set; }
        /// <summary>When the request was raised.</summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        /// <summary>When an undecided request is denied.</summary>
        public DateTime ExpiresUtc { get; set; }
        /// <summary>When the request was decided.</summary>
        public DateTime? DecidedUtc { get; set; }
    }
}
