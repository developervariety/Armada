namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Evidence payload POSTed to the configured CD webhook endpoint when a release is approved
    /// (transitions to Shipped). Carries the release identity plus links to the voyages, missions,
    /// and check runs that produced and verified the work.
    /// </summary>
    public sealed class ReleaseWebhookPayload
    {
        /// <summary>Event discriminator; always "release.shipped" for this payload.</summary>
        public string Event { get; set; } = "release.shipped";

        /// <summary>Release ID (rel_ prefix).</summary>
        public string ReleaseId { get; set; } = "";

        /// <summary>Vessel ID the release belongs to.</summary>
        public string VesselId { get; set; } = "";

        /// <summary>Release title.</summary>
        public string Title { get; set; } = "";

        /// <summary>Semantic version resolved for the release.</summary>
        public string Version { get; set; } = "";

        /// <summary>Git tag or image tag associated with the release.</summary>
        public string TagName { get; set; } = "";

        /// <summary>Short human-readable summary.</summary>
        public string Summary { get; set; } = "";

        /// <summary>Release status name at dispatch time; always "Shipped".</summary>
        public string Status { get; set; } = "Shipped";

        /// <summary>Linked voyage IDs (vyg_ prefix).</summary>
        public List<string> VoyageIds { get; set; } = new List<string>();

        /// <summary>Linked mission IDs (msn_ prefix).</summary>
        public List<string> MissionIds { get; set; } = new List<string>();

        /// <summary>Linked check-run IDs (chk_ prefix).</summary>
        public List<string> CheckRunIds { get; set; } = new List<string>();

        /// <summary>When the release was marked Shipped.</summary>
        public DateTime? PublishedUtc { get; set; }

        /// <summary>When the payload was emitted.</summary>
        public DateTime EmittedUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>Outcome of a single webhook dispatch attempt.</summary>
    public enum WebhookDispatchOutcome
    {
        /// <summary>HTTP 2xx received; endpoint accepted the payload.</summary>
        Success,

        /// <summary>5xx, network error, timeout, or auth failure (401/403). Worth retrying.</summary>
        RetriableFailure,

        /// <summary>4xx other than 401/403. Retrying is unlikely to help.</summary>
        NonRetriableFailure,
    }

    /// <summary>Result of a single webhook dispatch attempt.</summary>
    public sealed class WebhookDispatchResult
    {
        /// <summary>Categorized outcome of this dispatch attempt.</summary>
        public WebhookDispatchOutcome Outcome { get; set; }

        /// <summary>HTTP status code; null on network or timeout errors.</summary>
        public int? StatusCode { get; set; }

        /// <summary>Raw response body; populated on success or failure-with-body.</summary>
        public string? ResponseBody { get; set; }

        /// <summary>Human-readable error description; populated on failure outcomes.</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>Total attempts made, including the initial attempt and any retries.</summary>
        public int Attempts { get; set; } = 1;
    }
}
