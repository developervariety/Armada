namespace Armada.Core.Settings
{
    /// <summary>
    /// Configuration for admiral-side event-driven orchestrator wakes via Claude Code Routines
    /// /fire API. Lives under the `remoteTrigger` key in ~/.armada/settings.json.
    /// </summary>
    public sealed class RemoteTriggerSettings
    {
        /// <summary>Master enable; false (or section absent) disables the feature entirely.</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>Per-routine fire URL from the claude.ai/code/routines API trigger modal. Form: https://api.anthropic.com/v1/claude_code/routines/&lt;routine-id&gt;/fire</summary>
        public string? DrainerFireUrl { get; set; }

        /// <summary>Bearer token for the drainer routine; one-time-shown in the API trigger modal.</summary>
        public string? DrainerBearerToken { get; set; }

        /// <summary>Optional separate fire URL for audit-Critical events. If null, Critical events route through DrainerFireUrl with a different `text` body so the drainer prompt can branch on it.</summary>
        public string? CriticalFireUrl { get; set; }

        /// <summary>Bearer token for the critical routine. Required when CriticalFireUrl is set.</summary>
        public string? CriticalBearerToken { get; set; }

        /// <summary>Anthropic beta header value. User-configurable so future migrations do not require admiral rebuild.</summary>
        public string BetaHeader { get; set; } = "experimental-cc-routine-2026-04-01";

        /// <summary>Anthropic API version header value.</summary>
        public string AnthropicVersion { get; set; } = "2023-06-01";

        /// <summary>Returns true if the section has the minimum config to fire drainer wakes.</summary>
        public bool IsDrainerConfigured()
        {
            return Enabled
                && !string.IsNullOrEmpty(DrainerFireUrl)
                && !string.IsNullOrEmpty(DrainerBearerToken)
                && !string.IsNullOrEmpty(BetaHeader)
                && !string.IsNullOrEmpty(AnthropicVersion);
        }

        /// <summary>Returns true if the section has the minimum config to fire critical wakes via a dedicated route.</summary>
        public bool IsCriticalConfigured()
        {
            return Enabled
                && !string.IsNullOrEmpty(CriticalFireUrl)
                && !string.IsNullOrEmpty(CriticalBearerToken)
                && !string.IsNullOrEmpty(BetaHeader)
                && !string.IsNullOrEmpty(AnthropicVersion);
        }
    }
}
