namespace Armada.Core.Models
{
    /// <summary>Single fire request: where to POST + what bearer token + what to send.</summary>
    public sealed class FireRequest
    {
        /// <summary>Full fire URL for the target routine.</summary>
        public string FireUrl { get; set; } = "";

        /// <summary>Bearer token for the target routine.</summary>
        public string BearerToken { get; set; } = "";

        /// <summary>Value for the anthropic-beta header.</summary>
        public string BetaHeader { get; set; } = "";

        /// <summary>Value for the anthropic-version header.</summary>
        public string AnthropicVersion { get; set; } = "";

        /// <summary>Event-context summary; lands in the routine's `text` field.</summary>
        public string Text { get; set; } = "";
    }

    /// <summary>Outcome of a single fire attempt.</summary>
    public enum FireOutcome
    {
        /// <summary>HTTP 2xx received; routine was accepted.</summary>
        Success,

        /// <summary>5xx, network error, timeout, or auth failure (401/403). Worth retrying.</summary>
        RetriableFailure,

        /// <summary>4xx other than 401/403. Retrying is unlikely to help.</summary>
        NonRetriableFailure,
    }

    /// <summary>Result of a single fire attempt.</summary>
    public sealed class FireResult
    {
        /// <summary>Categorized outcome of this fire attempt.</summary>
        public FireOutcome Outcome { get; set; }

        /// <summary>HTTP status code; null on network or timeout errors.</summary>
        public int? StatusCode { get; set; }

        /// <summary>Raw response body; populated on success or failure-with-body.</summary>
        public string? ResponseBody { get; set; }

        /// <summary>Value of claude_code_session_url from a successful 2xx response; null otherwise.</summary>
        public string? SessionUrl { get; set; }

        /// <summary>Human-readable error description; populated on RetriableFailure or NonRetriableFailure.</summary>
        public string? ErrorMessage { get; set; }
    }
}
