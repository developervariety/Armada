namespace Armada.Core.Services
{
    using System;
    using System.Text.Json;

    /// <summary>
    /// The one definition of what an egress check reads: a decision's state BEFORE redaction, as text. Every
    /// path that sends decision state off the host - the adapter skeleton, the custom decisions and the captain
    /// tools - asks the same question of the same text, so the rule cannot drift between them. The check reads
    /// the unredacted state because the redactor replaces absolute workspace paths - where dock and sibling-checkout
    /// paths live - with a placeholder, taking a marker inside them along.
    /// </summary>
    public static class TypedDecisionEgress
    {
        /// <summary>The unavailable reason a content-marker exclusion records.</summary>
        public const string ExcludedContentReason = "egress_excluded_content";

        /// <summary>The unavailable reason a vessel exclusion records.</summary>
        public const string ExcludedVesselReason = "egress_excluded_vessel";

        /// <summary>The raw state as text, exactly as the check reads it.</summary>
        /// <param name="state">The state before redaction.</param>
        /// <returns>The text; empty for no state.</returns>
        public static string RawText(object? state)
        {
            if (state == null) return String.Empty;
            if (state is string text) return text;
            try
            {
                return JsonSerializer.Serialize(state);
            }
            catch (Exception)
            {
                // A state that cannot be read cannot be cleared either; the caller treats it as excluded.
                return ((char)0).ToString();
            }
        }

        /// <summary>Whether the raw text could not be produced, which a caller must treat as excluded.</summary>
        /// <param name="rawText">Text from <see cref="RawText"/>.</param>
        /// <returns>True when the state was unreadable.</returns>
        public static bool IsUnreadable(string rawText) => rawText.Length == 1 && rawText[0] == (char)0;
    }
}
